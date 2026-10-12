//! Device-initiated pairing (iOS 27+) for MaoX Launcher, built on idevice (MIT).
//!
//! The launcher acts as a "pairable host": it listens on a TCP port, the caller advertises it as
//! `_remotepairing-pairable-host._tcp` with NetService (raw multicast needs an entitlement that
//! sideloaded apps don't get), the user taps "Pair with MaoX Launcher" in Settings > Privacy &
//! Security > Developer Mode and types the PIN, and the result is an RPPairing file for StikJIT.
//! Modelled on idevice's ffi/src/pairable_host.rs, without its mDNS daemon.

use std::ffi::{CStr, CString, c_char, c_void};
use std::net::Ipv4Addr;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr::null_mut;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use idevice::remote_pairing::{PairableHost, PairableHostInfo, RpPairingFile, RpPairingSocket};
use tokio::sync::Semaphore;

pub const MAOX_PAIR_OK: i32 = 0;
pub const MAOX_PAIR_CANCELLED: i32 = 1;
pub const MAOX_PAIR_FAILED: i32 = 2;

/// Connections that fail before a PIN was shown (not a pairing device, or the device gave up early)
/// are ignored this many times before the error is reported.
const MAX_EARLY_FAILURES: u32 = 3;

pub type ReadyCallback =
    extern "C" fn(context: *mut c_void, service: *const c_char, port: u16, txt: *const c_char);
pub type PinCallback = extern "C" fn(context: *mut c_void, pin: *const c_char);

/// A zero-permit semaphore: acquiring never succeeds, closing wakes every waiter, and a cancel
/// signalled before the wait starts is not lost.
pub struct MaoxPairCancel(Arc<Semaphore>);

#[unsafe(no_mangle)]
pub extern "C" fn maox_pair_cancel_new() -> *mut MaoxPairCancel {
    Box::into_raw(Box::new(MaoxPairCancel(Arc::new(Semaphore::new(0)))))
}

/// # Safety
/// `cancel` must come from `maox_pair_cancel_new` and not be freed yet.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn maox_pair_cancel_signal(cancel: *const MaoxPairCancel) {
    if let Some(cancel) = unsafe { cancel.as_ref() } {
        cancel.0.close();
    }
}

/// # Safety
/// `cancel` must come from `maox_pair_cancel_new` (or be NULL) and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn maox_pair_cancel_free(cancel: *mut MaoxPairCancel) {
    if !cancel.is_null() {
        drop(unsafe { Box::from_raw(cancel) });
    }
}

/// # Safety
/// `s` must be a string returned by `maox_pair_run` (or NULL).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn maox_pair_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(unsafe { CString::from_raw(s) });
    }
}

#[derive(Clone, Copy)]
struct Context(*mut c_void);
unsafe impl Send for Context {}
unsafe impl Sync for Context {}

enum Failure {
    Cancelled,
    Error(String),
}

fn to_cstring(s: &str) -> CString {
    CString::new(s.replace('\0', " ")).unwrap_or_default()
}

unsafe fn arg(s: *const c_char, default: &str) -> String {
    if s.is_null() {
        return default.to_string();
    }
    match unsafe { CStr::from_ptr(s) }.to_str() {
        Ok(s) if !s.is_empty() => s.to_string(),
        _ => default.to_string(),
    }
}

unsafe fn put(out: *mut *mut c_char, value: &str) {
    if !out.is_null() {
        unsafe { *out = to_cstring(value).into_raw() };
    }
}

/// Waits for one device-initiated pairing. Blocks until it succeeds, fails or `cancel` is signalled.
///
/// `ready` is called once with the service instance name, TCP port and TXT records ("key=value"
/// lines) to advertise as `_remotepairing-pairable-host._tcp`; `pin` is called with the 6-digit
/// code after a device connects. Returns `MAOX_PAIR_OK` with `out_file` (RPPairing file, XML plist)
/// and `out_device` (device name), `MAOX_PAIR_CANCELLED`, or `MAOX_PAIR_FAILED` with `out_error`.
/// Free the returned strings with `maox_pair_string_free`.
///
/// # Safety
/// String arguments must be NULL or valid C strings; `cancel` must be NULL or a live token;
/// the out pointers must be NULL or writable.
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn maox_pair_run(
    name: *const c_char,
    model: *const c_char,
    ready: ReadyCallback,
    pin: PinCallback,
    context: *mut c_void,
    cancel: *const MaoxPairCancel,
    out_file: *mut *mut c_char,
    out_device: *mut *mut c_char,
    out_error: *mut *mut c_char,
) -> i32 {
    let name = unsafe { arg(name, "MaoX Launcher") };
    let model = unsafe { arg(model, "Mac17,7") };
    let cancel = unsafe { cancel.as_ref() }.map(|c| c.0.clone());
    let context = Context(context);

    let result = catch_unwind(AssertUnwindSafe(|| run(name, model, ready, pin, context, cancel)))
        .unwrap_or_else(|_| Err(Failure::Error("internal error".to_string())));
    match result {
        Ok((file, device)) => {
            unsafe {
                put(out_file, &file);
                put(out_device, &device);
            }
            MAOX_PAIR_OK
        }
        Err(Failure::Cancelled) => MAOX_PAIR_CANCELLED,
        Err(Failure::Error(e)) => {
            unsafe { put(out_error, &e) };
            MAOX_PAIR_FAILED
        }
    }
}

fn run(
    name: String,
    model: String,
    ready: ReadyCallback,
    pin: PinCallback,
    context: Context,
    cancel: Option<Arc<Semaphore>>,
) -> Result<(String, String), Failure> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| Failure::Error(format!("runtime: {e}")))?;
    runtime.block_on(async move {
        let work = pair(name, model, ready, pin, context);
        match cancel {
            Some(cancel) => tokio::select! {
                biased;
                _ = cancel.acquire() => Err(Failure::Cancelled),
                r = work => r,
            },
            None => work.await,
        }
    })
}

async fn pair(
    name: String,
    model: String,
    ready: ReadyCallback,
    pin: PinCallback,
    context: Context,
) -> Result<(String, String), Failure> {
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::UNSPECIFIED, 0))
        .await
        .map_err(|e| Failure::Error(format!("bind: {e}")))?;
    let port = listener
        .local_addr()
        .map_err(|e| Failure::Error(format!("bind: {e}")))?
        .port();

    // A fresh identity every time, so the device never takes us for a host it paired with before
    // (that makes it ask for pair-verify, which a new pairing can't answer)
    let mut pairing_file = RpPairingFile::generate(&format!("{name}-{}", uuid::Uuid::new_v4()));
    let host_info = PairableHostInfo::generate(name.as_str(), model.as_str());
    let service = pairing_file.identifier.clone();
    let txt = host_info
        .mdns_txt_records(&service)
        .into_iter()
        .map(|(k, v)| format!("{k}={v}"))
        .collect::<Vec<_>>()
        .join("\n");
    {
        let service = to_cstring(&service);
        let txt = to_cstring(&txt);
        ready(context.0, service.as_ptr(), port, txt.as_ptr());
    }

    let mut early_failures = 0;
    loop {
        let (stream, _) = listener
            .accept()
            .await
            .map_err(|e| Failure::Error(format!("accept: {e}")))?;
        let shown = Arc::new(AtomicBool::new(false));
        let shown_by_callback = shown.clone();
        let mut host = PairableHost::new(RpPairingSocket::new_device(stream), host_info.clone());
        let result = host
            .accept(&mut pairing_file, move |code| async move {
                shown_by_callback.store(true, Ordering::SeqCst);
                let code = to_cstring(&code);
                pin(context.0, code.as_ptr());
            })
            .await;
        match result {
            Ok(device) => {
                let label = if device.model.is_empty() {
                    device.name.clone()
                } else {
                    format!("{} ({})", device.name, device.model)
                };
                let file = String::from_utf8_lossy(&pairing_file.to_bytes()).into_owned();
                return Ok((file, label));
            }
            Err(_) if !shown.load(Ordering::SeqCst) && early_failures + 1 < MAX_EARLY_FAILURES => {
                early_failures += 1;
            }
            Err(e) => return Err(Failure::Error(e.to_string())),
        }
    }
}
