// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
#include <dirent.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>

#include "utils.h"

CFTypeRef SecTaskCopyValueForEntitlement(void* task, NSString* entitlement, CFErrorRef _Nullable *error);
void* SecTaskCreateFromSelf(CFAllocatorRef allocator);

BOOL getEntitlementValue(NSString *key) {
    void *secTask = SecTaskCreateFromSelf(NULL);
    CFTypeRef value = SecTaskCopyValueForEntitlement(secTask, key, nil);
    CFRelease(secTask);
    if (value == nil) {
        return NO;
    }
    BOOL result = ![(__bridge id)value isKindOfClass:NSNumber.class] || [(__bridge id)value boolValue];
    CFRelease(value);
    return result;
}

BOOL isJITEnabled(void) {
    int flags;
    csops(getpid(), 0, &flags, sizeof(flags));
    if ((flags & CS_DEBUGGED) == 0) {
        return NO;
    }
    if (!DeviceHasJITFlags(JIT_FLAG_FORCE_MIRRORED | JIT_FLAG_HAS_TXM)) {
        // Below iOS 26 or without TXM, CS_DEBUGGED is enough
        return YES;
    }
    // iOS 26+ with TXM needs the debugger to stay attached so the JIT script can map code pages
    return JIT26IsLikelyDebuggerKeepAttached();
}

void customNSLog(const char *file, int lineNumber, const char *functionName, NSString *format, ...) {
    va_list ap;
    va_start(ap, format);
    NSString *body = [[NSString alloc] initWithFormat:format arguments:ap];
    printf("%s", body.UTF8String);
    if (![format hasSuffix:@"\n"]) {
        printf("\n");
    }
    va_end(ap);
}

__attribute__((noinline,optnone,naked))
void* JIT26CreateRegionLegacy(size_t len) {
    asm("brk #0x69 \n"
        "ret");
}
__attribute__((noinline,optnone,naked))
void* JIT26PrepareRegion(void *addr, size_t len) {
    asm("mov x16, #1 \n"
        "brk #0xf00d \n"
        "ret");
}
__attribute__((noinline,optnone,naked))
void BreakSendJITScript(char* script, size_t len) {
   asm("mov x16, #2 \n"
       "brk #0xf00d \n"
       "ret");
}
__attribute__((noinline,optnone,naked))
void JIT26SetDetachAfterFirstBr(BOOL value) {
   asm("mov x16, #3 \n"
       "brk #0xf00d \n"
       "ret");
}
__attribute__((noinline,optnone,naked))
void JIT26PrepareRegionForPatching(void *addr, size_t size) {
   asm("mov x16, #4 \n"
       "brk #0xf00d \n"
       "ret");
}
void JIT26SendJITScript(NSString* script) {
    NSCAssert(script, @"Script must not be nil");
    BreakSendJITScript((char*)script.UTF8String, script.length);
}

BOOL JIT26IsLikelyDebuggerKeepAttached(void) {
    // getppid() returns launchd (1) unless a debugger is attached
    return getppid() != 1;
}

static BOOL DeviceCanCreateRXMap(void) {
    uint32_t *map = mmap(NULL, getpagesize(), PROT_READ | PROT_WRITE, MAP_ANONYMOUS | MAP_SHARED, -1, 0);
    if (map == MAP_FAILED) return NO;
    *map = 0xFFFFFFFF;
    int ret = mprotect(map, getpagesize(), PROT_READ | PROT_EXEC) | mprotect(map, getpagesize(), PROT_READ | PROT_EXEC);
    munmap(map, getpagesize());
    return ret == 0;
}

static BOOL DeviceHasTXMReal(void) {
    DIR *d = opendir("/private/preboot");
    if (!d) {
        // /private/preboot is not readable on newer releases, guess from the chip
        NSUInteger (*MGGetSInt64Answer)(NSString *) = dlsym(RTLD_DEFAULT, "MGGetSInt64Answer");
        NSUInteger chipID = MGGetSInt64Answer ? MGGetSInt64Answer(@"ChipID") : 0;
        switch (chipID) {
            case 0x8020: // A12
            case 0x8027: // A12X/Z
                return NO;
            case 0x8030: // A13
            case 0x8101: // A14
            case 0x8103: // M1
                if (@available(iOS 27.0, *)) return YES;
                return NO;
            default:
                if (@available(iOS 19.0, *)) return YES;
                return NO;
        }
    }
    struct dirent *dir;
    char txmPath[PATH_MAX] = {0};
    while ((dir = readdir(d)) != NULL) {
        if (strlen(dir->d_name) == 96) {
            snprintf(txmPath, sizeof(txmPath), "/private/preboot/%s/usr/standalone/firmware/FUD/Ap,TrustedExecutionMonitor.img4", dir->d_name);
            break;
        }
    }
    closedir(d);
    return txmPath[0] && access(txmPath, F_OK) == 0;
}

// Looked up by the iOS JRE
__attribute__((visibility("default"))) BOOL DeviceHasTXM(void) {
    return DeviceHasJITFlags(JIT_FLAG_HAS_TXM);
}

JITFlags DeviceGetJITFlags(BOOL refresh) {
    static JITFlags cachedFlags = 0;
    static dispatch_once_t onceToken;
    if (refresh) {
        onceToken = 0;
        cachedFlags = 0;
    }
    dispatch_once(&onceToken, ^{
        const char *s = getenv("JIT_FLAGS");
        if (s) {
            if (s[0] == '0' && tolower(s[1]) == 'b') {
                cachedFlags = (JITFlags)strtoul(s + 2, NULL, 2);
            } else {
                cachedFlags = (JITFlags)strtoul(s, NULL, 0);
            }
            NSLog(@"[JIT] Using overridden JIT flags: 0x%X", cachedFlags);
            return;
        }
        if (@available(iOS 26.0, *)) {
            cachedFlags |= JIT_FLAG_IS_IOS_26;
            if (!DeviceCanCreateRXMap()) {
                cachedFlags |= JIT_FLAG_FORCE_MIRRORED;
            }
        }
        if (DeviceHasTXMReal()) {
            cachedFlags |= JIT_FLAG_HAS_TXM;
        }
        NSLog(@"[JIT] Using computed JIT flags: 0x%X", cachedFlags);
    });
    return cachedFlags;
}

BOOL DeviceHasJITFlags(JITFlags flags) {
    return (DeviceGetJITFlags(NO) & flags) == flags;
}

jstring UIKit_accessClipboard(JNIEnv* env, jint action, jstring copySrc) {
    if (action == CLIPBOARD_PASTE) {
        NSString *text = UIPasteboard.generalPasteboard.hasStrings ? UIPasteboard.generalPasteboard.string : @"";
        return (*env)->NewStringUTF(env, text.UTF8String ?: "");
    } else if (action == CLIPBOARD_COPY) {
        const char *copySrcC = (*env)->GetStringUTFChars(env, copySrc, 0);
        if (copySrcC) {
            UIPasteboard.generalPasteboard.string = @(copySrcC);
            (*env)->ReleaseStringUTFChars(env, copySrc, copySrcC);
        }
        return NULL;
    }
    NSLog(@"Warning: unknown clipboard action: %x", action);
    return NULL;
}
