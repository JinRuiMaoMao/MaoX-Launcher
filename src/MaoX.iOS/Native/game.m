// MaoX Launcher iOS game runtime: starts the iOS JRE in-process (based on Amethyst-iOS JavaLauncher.m, GPL-3.0).
#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>

#include <dlfcn.h>
#include <fcntl.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <mach/mach.h>

#include "jni.h"
#include "utils.h"

#define EXPORT __attribute__((visibility("default")))

typedef jint JLI_Launch_func(int argc, const char** argv,
    int jargc, const char** jargv,
    int appclassc, const char** appclassv,
    const char* fullversion, const char* dotversion,
    const char* pname, const char* lname,
    jboolean javaargs, jboolean cpwildcard, jboolean javaw, jint ergo);

void *maox_surface_layer;
char maox_resolv_conf[PATH_MAX];

void maox_set_grab_callback_internal(void (*callback)(int));
void maox_set_exit_callback_internal(void (*callback)(int));
void maox_exit_now(int code);

static char lastError[2048];
static BOOL launched;

typedef struct {
    int argc;
    const char **argv;
    JLI_Launch_func *launch;
    struct sigaction sigbus;
} launch_ctx;

static int fail(const char *message) {
    snprintf(lastError, sizeof(lastError), "%s", message);
    NSLog(@"[MaoX] %s", message);
    return 1;
}

static BOOL validateVirtualMemorySpace(size_t sizeMB) {
    size_t size = sizeMB << 20;
    void *map = mmap(0, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (map == MAP_FAILED || munmap(map, size) != 0)
        return NO;
    return YES;
}

static void *launchThread(void *arg) {
    launch_ctx *ctx = arg;
    // The dyld bypass ignores SIGBUS while patching; give the JVM (and .NET) the previous handler back
    sigaction(SIGBUS, &ctx->sigbus, NULL);
    NSLog(@"[MaoX] Calling JLI_Launch");
    jint code = ctx->launch(ctx->argc, ctx->argv,
        0, NULL, 0, NULL,
        // Ignored by Java 9+
        "1.8.0-internal", "1.8",
        "java", "openjdk",
        JNI_FALSE, JNI_TRUE, JNI_FALSE, JNI_TRUE);
    NSLog(@"[MaoX] JLI_Launch returned %d", code);
    fflush(stdout);
    fflush(stderr);
    if (code == 0) {
        exit(0);
    }
    maox_handle_exit(code);
    return NULL;
}

EXPORT int maox_jit_enabled(void) {
    return isJITEnabled() ? 1 : 0;
}

EXPORT int maox_jit_flags(void) {
    return (int)DeviceGetJITFlags(NO);
}

EXPORT int maox_has_entitlement(const char *name) {
    return getEntitlementValue(@(name)) ? 1 : 0;
}

EXPORT const char *maox_last_error(void) {
    return lastError;
}

EXPORT void maox_set_grab_callback(void (*callback)(int)) {
    maox_set_grab_callback_internal(callback);
}

EXPORT void maox_set_exit_callback(void (*callback)(int)) {
    maox_set_exit_callback_internal(callback);
}

EXPORT void maox_exit(int code) {
    maox_exit_now(code);
}

// Returns 0 once the JVM thread is running; otherwise maox_last_error() explains why.
// Error codes: 1 generic, 2 legacy StikDebug script (UniversalJIT26.js not assigned), 3 not enough memory.
EXPORT int maox_launch(const char *json, void *layer) {
    if (launched) {
        return fail("The game is already running");
    }
    NSData *data = [NSData dataWithBytes:json length:strlen(json)];
    NSDictionary *config = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![config isKindOfClass:NSDictionary.class]) {
        return fail("Invalid launch config");
    }

    NSString *logFile = config[@"logFile"];
    if (logFile.length) {
        int fd = open(logFile.UTF8String, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (fd >= 0) {
            dup2(fd, STDOUT_FILENO);
            dup2(fd, STDERR_FILENO);
            close(fd);
            setvbuf(stdout, NULL, _IOLBF, 0);
            setvbuf(stderr, NULL, _IONBF, 0);
        }
    }

    NSDictionary *env = config[@"env"];
    for (NSString *key in env) {
        setenv(key.UTF8String, [env[key] description].UTF8String, 1);
    }
    NSString *resolvConf = config[@"resolvConf"];
    if (resolvConf.length) {
        snprintf(maox_resolv_conf, sizeof(maox_resolv_conf), "%s", resolvConf.UTF8String);
    }

    maox_surface_layer = (void *)CFBridgingRetain((__bridge id)layer);

    DeviceGetJITFlags(YES);
    if (!isJITEnabled()) {
        return fail("JIT is not enabled");
    }
    NSLog(@"[MaoX] JIT flags: 0x%X", DeviceGetJITFlags(NO));

    BOOL requiresTXMWorkaround = DeviceHasJITFlags(JIT_FLAG_FORCE_MIRRORED | JIT_FLAG_HAS_TXM);
    BOOL alwaysAttached = [config[@"jitAlwaysAttached"] boolValue];
    if (requiresTXMWorkaround) {
        static void *result;
        if (!result) result = JIT26CreateRegionLegacy(getpagesize());
        if ((uint32_t)(uintptr_t)result != 0x690000E0) {
            munmap(result, getpagesize());
            fail("Legacy JIT script");
            return 2;
        }
        NSString *extension = [NSString stringWithContentsOfFile:[NSBundle.mainBundle pathForResource:@"UniversalJIT26Extension" ofType:@"js"]
            encoding:NSUTF8StringEncoding error:nil];
        if (extension) {
            JIT26SendJITScript(extension);
        }
        JIT26SetDetachAfterFirstBr(!alwaysAttached);
        // Don't get stuck in EXC_BAD_ACCESS once the debugger detaches
        task_set_exception_ports(mach_task_self(), EXC_MASK_BAD_ACCESS, 0, EXCEPTION_DEFAULT, MACHINE_THREAD_STATE);
    }

    struct sigaction oldSigbus;
    sigaction(SIGBUS, NULL, &oldSigbus);
    init_hookFunctions();
    if (!requiresTXMWorkaround || alwaysAttached) {
        if (alwaysAttached) {
            task_set_exception_ports(mach_task_self(), EXC_MASK_ALL & ~EXC_MASK_BREAKPOINT, 0, EXCEPTION_DEFAULT, THREAD_STATE_NONE);
        }
        init_bypassDyldLibValidation();
    } else {
        NSLog(@"[DyldLVBypass] Hook disabled, only bundled dylibs can be loaded");
    }

    int maxMemory = [config[@"maxMemory"] intValue];
    if (maxMemory > 0 && !validateVirtualMemorySpace(maxMemory)) {
        fail("Insufficient contiguous virtual memory space");
        return 3;
    }

    NSString *javaHome = config[@"javaHome"];
    NSString *jli8 = [javaHome stringByAppendingPathComponent:@"lib/jli/libjli.dylib"];
    NSString *jli = [NSFileManager.defaultManager fileExistsAtPath:jli8] ? jli8 : [javaHome stringByAppendingPathComponent:@"lib/libjli.dylib"];
    setenv("INTERNAL_JLI_PATH", jli.UTF8String, 1);
    void *libjli = dlopen(jli.UTF8String, RTLD_GLOBAL);
    if (!libjli) {
        return fail(dlerror() ?: "dlopen libjli failed");
    }
    JLI_Launch_func *launch = (JLI_Launch_func *)dlsym(libjli, "JLI_Launch");
    if (!launch) {
        return fail("JLI_Launch not found");
    }

    NSString *gameDir = config[@"gameDir"];
    if (gameDir.length) {
        [NSFileManager.defaultManager createDirectoryAtPath:gameDir withIntermediateDirectories:YES attributes:nil error:nil];
        chdir(gameDir.UTF8String);
    }

    NSArray *args = config[@"args"];
    launch_ctx *ctx = calloc(1, sizeof(launch_ctx));
    ctx->argc = (int)args.count;
    ctx->argv = calloc(args.count + 1, sizeof(char *));
    for (NSUInteger i = 0; i < args.count; i++) {
        ctx->argv[i] = strdup([args[i] UTF8String]);
        NSLog(@"[MaoX] arg[%lu] %s", (unsigned long)i, ctx->argv[i]);
    }
    ctx->launch = launch;
    ctx->sigbus = oldSigbus;

    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, 8 << 20);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_t thread;
    int err = pthread_create(&thread, &attr, launchThread, ctx);
    pthread_attr_destroy(&attr);
    if (err) {
        return fail("pthread_create failed");
    }
    launched = YES;
    return 0;
}

// ---- Input from the touch UI (main thread) ----

EXPORT void maox_send_cursor(int event, float x, float y) {
    CallbackBridge_nativeSendCursorPos((char)event, x, y);
}

EXPORT void maox_send_key(int key, int down, int mods) {
    CallbackBridge_nativeSendKey(key, 0, down ? 1 : 0, mods);
}

EXPORT void maox_send_mouse(int button, int down) {
    CallbackBridge_nativeSendMouseButton(button, down ? 1 : 0, 0);
}

EXPORT void maox_send_scroll(float dx, float dy) {
    CallbackBridge_nativeSendScroll(dx, dy);
}

EXPORT void maox_send_char(unsigned int codepoint) {
    CallbackBridge_nativeSendCharMods((jchar)codepoint, 0);
    CallbackBridge_nativeSendChar((jchar)codepoint);
}

EXPORT void maox_send_screen_size(int width, int height) {
    CallbackBridge_nativeSendScreenSize(width, height);
}

EXPORT int maox_is_grabbing(void) {
    return isGrabbing ? 1 : 0;
}

EXPORT void maox_set_window_size(int width, int height) {
    // Initial size before GLFW is ready
    windowWidth = width;
    windowHeight = height;
}
