// Terracotta library entry point for iOS (replaces src/lib.rs of Terracotta v0.4.2 when building for iOS).
// Derived from Terracotta's Android lib.rs, Copyright Burning_TNT, AGPL-3.0: https://github.com/burningtnt/Terracotta
// Changes: C ABI instead of JNI, logs go to a file descriptor only, no VpnService (rooms run EasyTier with --no-tun),
// and the host's LAN port can be set directly because iOS apps can't receive multicast without a special entitlement.
#![feature(
    panic_backtrace_config,
    const_convert,
    const_trait_impl,
    unsafe_cell_access,
    panic_update_hook,
    internal_output_capture,
    string_from_utf8_lossy_owned
)]

extern crate core;
#[cfg(not(target_os = "ios"))]
compile_error!("This Terracotta library entry point is intended for iOS.");

#[macro_export]
macro_rules! logging {
    ($prefix:expr, $($arg:tt)*) => {
        crate::write_log(std::format!("[{}]: {}", $prefix, std::format_args!($($arg)*)));
    };
}

use crate::controller::Room;
use crate::once_cell::OnceCell;
use chrono::{FixedOffset, TimeZone, Utc};
use libc::{c_char, c_int};
use std::ffi::{CStr, CString};
use std::io::Write;
use std::os::fd::FromRawFd;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};
use std::{
    net::{IpAddr, Ipv4Addr, Ipv6Addr}, sync::{Arc, Mutex}, thread,
};

pub mod controller;
mod easytier;
mod scaffolding;
pub const MOTD: &'static str = "§6§l双击进入陶瓦联机大厅（请保持陶瓦运行）";

mod mc;
mod ports;
mod once_cell;

lazy_static::lazy_static! {
    static ref ADDRESSES: Vec<IpAddr> = {
        let mut addresses: Vec<IpAddr> = vec![];

        if let Ok(networks) = local_ip_address::list_afinet_netifas() {
            logging!("UI", "Local IP Addresses: {:?}", networks);

            for (_, address) in networks.into_iter() {
                match address {
                    IpAddr::V4(ip) => {
                        let parts = ip.octets();
                        if !(parts[0] == 10 && parts[1] == 144 && parts[2] == 144) && ip != Ipv4Addr::LOCALHOST && ip != Ipv4Addr::UNSPECIFIED {
                            addresses.push(address);
                        }
                    },
                    IpAddr::V6(ip) => {
                        if ip != Ipv6Addr::LOCALHOST && ip != Ipv6Addr::UNSPECIFIED {
                            addresses.push(address);
                        }
                    }
                };
            }
        }

        addresses.push(IpAddr::V4(Ipv4Addr::UNSPECIFIED));
        addresses.push(IpAddr::V6(Ipv6Addr::UNSPECIFIED));

        addresses.sort_by(|ip1, ip2| ip2.cmp(ip1));
        addresses
    };
}

static MACHINE_ID_FILE: OnceCell<std::path::PathBuf> = OnceCell::new();
static LOGGING_FD: Mutex<Option<std::fs::File>> = Mutex::new(None);
static STARTED: AtomicBool = AtomicBool::new(false);

/// Port of the LAN world opened by the game running in this process, 0 when none. Read by the server scanner.
pub(crate) static LAN_PORT: AtomicU16 = AtomicU16::new(0);

fn guard<T>(default: T, f: impl FnOnce() -> T) -> T {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or(default)
}

unsafe fn parse_cstr(value: *const c_char) -> Option<String> {
    if value.is_null() {
        None
    } else {
        // SAFETY: the caller passes a valid NUL-terminated string.
        Some(unsafe { CStr::from_ptr(value) }.to_string_lossy().into_owned())
    }
}

fn into_cstr(value: String) -> *mut c_char {
    CString::new(value.replace('\0', " ")).unwrap().into_raw()
}

/// Starts the background services. `dir` stores the machine id; logs are appended to `logging_fd` (taken over).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn terracotta_start(dir: *const c_char, logging_fd: c_int) -> c_int {
    if STARTED.swap(true, Ordering::SeqCst) {
        return 0;
    }
    let Some(dir) = (unsafe { parse_cstr(dir) }) else {
        STARTED.store(false, Ordering::SeqCst);
        return 1;
    };

    guard(2, move || {
        std::panic::set_backtrace_style(std::panic::BacktraceStyle::Full);
        std::panic::update_hook(|prev, info| {
            let data = Arc::new(Mutex::new(Vec::<u8>::new()));
            std::io::set_output_capture(Some(data.clone()));
            prev(info);
            std::io::set_output_capture(None);

            let data = match Arc::try_unwrap(data) {
                Ok(data) => String::from_utf8_lossy_owned(data.into_inner().unwrap()),
                Err(data) => String::from_utf8_lossy_owned(data.lock().unwrap().clone()),
            };
            write_log(data);
        });

        if logging_fd >= 0 {
            let _ = LOGGING_FD.lock().unwrap().replace(unsafe { std::fs::File::from_raw_fd(logging_fd) });
        }

        logging!(
            "UI",
            "Welcome using Terracotta v{}, compiled at {}. Easytier: {}. Target: {}-{}-{}-{}.",
            env!("TERRACOTTA_VERSION"),
            Utc.timestamp_millis_opt(timestamp::compile_time!() as i64)
                .unwrap()
                .with_timezone(&FixedOffset::east_opt(8 * 3600).unwrap())
                .format("%Y-%m-%d %H:%M:%S"),
            env!("TERRACOTTA_ET_VERSION"),
            env!("CARGO_CFG_TARGET_ARCH"),
            env!("CARGO_CFG_TARGET_VENDOR"),
            env!("CARGO_CFG_TARGET_OS"),
            env!("CARGO_CFG_TARGET_ENV"),
        );

        MACHINE_ID_FILE.set(PathBuf::from(dir).join("machine-id"));

        thread::spawn(|| {
            lazy_static::initialize(&controller::SCAFFOLDING_PORT);
        });
        0
    })
}

fn write_log(line: String) {
    if let Ok(mut fd) = LOGGING_FD.lock() && let Some(fd) = fd.as_mut() {
        let _ = fd.write_all(line.as_bytes());
        let _ = fd.write_all(b"\n");
    }
}

/// JSON state, same format as the desktop HTTP API's /state. Free with terracotta_free.
#[unsafe(no_mangle)]
pub extern "C" fn terracotta_get_state() -> *mut c_char {
    guard(std::ptr::null_mut(), || into_cstr(serde_json::to_string(&controller::get_state()).unwrap()))
}

#[unsafe(no_mangle)]
pub extern "C" fn terracotta_set_waiting() {
    guard((), controller::set_waiting)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn terracotta_set_scanning(room: *const c_char, player: *const c_char) {
    let room = unsafe { parse_cstr(room) };
    let player = unsafe { parse_cstr(player) };
    guard((), move || controller::set_scanning(room, player, vec![]))
}

/// Returns 1 when joining started, 0 when the room code is invalid or another room is active.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn terracotta_set_guesting(room: *const c_char, player: *const c_char) -> c_int {
    let room = unsafe { parse_cstr(room) };
    let player = unsafe { parse_cstr(player) };
    guard(0, move || {
        if let Some(room) = room.as_ref().and_then(|room| Room::from(room)) && controller::set_guesting(room, player, vec![]) {
            1
        } else {
            0
        }
    })
}

/// "version\0compile time\0EasyTier version" with NULs replaced by newlines. Free with terracotta_free.
#[unsafe(no_mangle)]
pub extern "C" fn terracotta_get_metadata() -> *mut c_char {
    guard(std::ptr::null_mut(), || into_cstr(format!(
        "{}\n{}\n{}", env!("TERRACOTTA_VERSION"), timestamp::compile_time!() as i64, env!("TERRACOTTA_ET_VERSION")
    )))
}

/// Tells the scanner which port the in-process game opened to LAN; 0 clears it.
#[unsafe(no_mangle)]
pub extern "C" fn terracotta_set_lan_port(port: c_int) {
    let port = u16::try_from(port).unwrap_or(0);
    LAN_PORT.store(port, Ordering::SeqCst);
    logging!("Server Scanner", "LAN port reported by launcher: {}", port);
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn terracotta_free(value: *mut c_char) {
    if !value.is_null() {
        drop(unsafe { CString::from_raw(value) });
    }
}

pub(crate) fn on_vpnservice_change(_: crate::easytier::EasyTierTunRequest) {
    // Rooms run EasyTier with --no-tun; the virtual address is only used through port forwarding.
}
