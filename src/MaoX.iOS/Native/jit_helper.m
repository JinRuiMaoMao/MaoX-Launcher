// Built-in JIT: starts the launcher's own app extension (PlugIns/MaoXJITHelper.appex, src/MaoX.iOS/JITHelper), which
// enables JIT for this process with StikJIT. A process cannot attach a debugger to itself, hence the second process.
// The extension is started through the private NSExtension API (ExtensionFoundation), the same calls LiveContainer
// uses; one JSON request goes in the first NSExtensionItem's userInfo and one JSON response comes back the same way.
// Whether JIT is on is still decided by isJITEnabled() (CS_DEBUGGED), not by the helper's answer.
#import <Foundation/Foundation.h>
#import <OSLog/OSLog.h>

#include <dlfcn.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>

#define EXPORT __attribute__((visibility("default")))

// Status codes, shared with Game/IosBuiltInJit.cs
enum {
    JH_OK = 0,              // the helper answered; the text is its JSON response
    JH_UNSUPPORTED_OS = 1,  // StikJIT needs iOS 17.4
    JH_LIVECONTAINER = 2,   // app extensions of a guest app cannot be started inside LiveContainer
    JH_MISSING = 3,         // no PlugIns/MaoXJITHelper.appex (standard IPA, or the sideloader removed extensions)
    JH_API_MISSING = 4,     // NSExtension or one of its methods is not there on this iOS
    JH_NOT_REGISTERED = 5,  // the helper is in the bundle but iOS did not register it
    JH_BUSY = 6,            // another request is still running
    JH_BAD_REQUEST = 7,
    JH_START_FAILED = 8,    // iOS did not start the helper process
    JH_CANCELLED = 9,
    JH_INTERRUPTED = 10,    // the helper process exited without answering
    JH_NO_RESPONSE = 11,    // the request completed without a response item
};

static NSString *const HelperBundle = @"MaoXJITHelper.appex";
static NSString *const RequestKey = @"io.github.jinruimaomao.maox.jit.request";
static NSString *const ResponseKey = @"io.github.jinruimaomao.maox.jit.response";

typedef void (*maox_jit_helper_callback)(int64_t token, int status, const char *text);

@interface NSObject (MaoXExtensionPrivate)
+ (id)extensionWithIdentifier:(NSString *)identifier error:(NSError **)error;
- (void)beginExtensionRequestWithInputItems:(NSArray *)items completion:(void (^)(NSUUID *requestIdentifier))completion;
- (pid_t)pidForRequestIdentifier:(NSUUID *)requestIdentifier;
- (void)cancelExtensionRequestWithIdentifier:(NSUUID *)requestIdentifier;
- (void)setRequestCompletionBlock:(void (^)(NSUUID *requestIdentifier, NSArray *items))block;
- (void)setRequestCancellationBlock:(void (^)(NSUUID *requestIdentifier, NSError *error))block;
- (void)setRequestInterruptionBlock:(void (^)(NSUUID *requestIdentifier))block;
- (void)_kill:(int)signal;
- (void)setPreferredLanguages:(NSArray *)languages;
@end

@interface MaoXJITHelperRequest : NSObject
@property(nonatomic, strong) id extension;
@property(nonatomic, strong) NSUUID *identifier;
@property(nonatomic) int64_t token;
@property(nonatomic) maox_jit_helper_callback callback;
@property(nonatomic) pid_t pid;
@property(nonatomic) BOOL cancelled;
@property(nonatomic) BOOL done;
@property(nonatomic, strong) NSDate *started;
@property(nonatomic, copy) NSString *startError; // cancellation error that arrived before iOS named the request
@property(nonatomic) BOOL failing;
@end

@implementation MaoXJITHelperRequest
@end

// All request state lives on this queue; callbacks into .NET run on a global queue.
static dispatch_queue_t stateQueue(void) {
    static dispatch_queue_t queue;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        queue = dispatch_queue_create("io.github.jinruimaomao.maox.jit-helper", DISPATCH_QUEUE_SERIAL);
    });
    return queue;
}

static MaoXJITHelperRequest *current;
static char *lastError; // why the last request could not be started (stateQueue)

static void setLastError(NSString *text) {
    free(lastError);
    lastError = strdup((text ?: @"").UTF8String);
}

// What this process logged about extensions since `since` (ExtensionFoundation, PlugInKit and RunningBoard log the
// reason a launch failed here, not in the callbacks). Slow; only for failures.
static NSString *recentExtensionLog(NSDate *since) {
    NSError *error = nil;
    OSLogStore *store = [OSLogStore storeWithScope:OSLogStoreCurrentProcessIdentifier error:&error];
    if (!store) {
        return [NSString stringWithFormat:@"(system log unavailable: %@)", error.localizedDescription];
    }
    OSLogEnumerator *entries = [store entriesEnumeratorWithOptions:0
                                                          position:[store positionWithDate:since]
                                                         predicate:nil
                                                             error:&error];
    if (!entries) {
        return [NSString stringWithFormat:@"(system log unavailable: %@)", error.localizedDescription];
    }
    NSDateFormatter *time = [NSDateFormatter new];
    time.dateFormat = @"HH:mm:ss.SSS";
    NSMutableArray<NSString *> *lines = [NSMutableArray array];
    for (OSLogEntry *entry in entries) {
        if (![entry isKindOfClass:OSLogEntryLog.class]) {
            continue;
        }
        OSLogEntryLog *log = (OSLogEntryLog *)entry;
        NSString *subsystem = log.subsystem ?: @"";
        NSString *message = log.composedMessage ?: @"";
        NSString *lower = [subsystem stringByAppendingString:message].lowercaseString;
        BOOL relevant = log.level >= OSLogEntryLogLevelError || [lower containsString:@"extension"] ||
                        [lower containsString:@"plugin"] || [lower containsString:@"runningboard"] ||
                        [lower containsString:@"jithelper"] || [lower containsString:@"launch"] ||
                        [lower containsString:@"xpc"];
        if (relevant) {
            [lines addObject:[NSString stringWithFormat:@"%@ [%@:%@] %@", [time stringFromDate:log.date], subsystem,
                                                        log.category ?: @"", message]];
        }
    }
    if (lines.count > 40) {
        [lines removeObjectsInRange:NSMakeRange(0, lines.count - 40)];
    }
    return lines.count ? [lines componentsJoinedByString:@"\n"] : @"(nothing about extensions in the log)";
}

// Read from the helper's own Info.plist: sideloaders append their team ID to the app's and the helper's identifier
static NSString *helperIdentifier(void) {
    NSURL *plugins = NSBundle.mainBundle.builtInPlugInsURL;
    if (!plugins) {
        return nil;
    }
    return [NSBundle bundleWithURL:[plugins URLByAppendingPathComponent:HelperBundle]].bundleIdentifier;
}

static Class extensionClass(void) {
    Class cls = NSClassFromString(@"NSExtension");
    if (!cls) {
        dlopen("/System/Library/Frameworks/ExtensionFoundation.framework/ExtensionFoundation", RTLD_NOW);
        cls = NSClassFromString(@"NSExtension");
    }
    return cls;
}

// Runs on stateQueue. Reports the request's one result.
static void finish(MaoXJITHelperRequest *request, int status, NSString *text) {
    if (request.done) {
        return;
    }
    request.done = YES;
    if (current == request) {
        current = nil;
    }
    request.extension = nil;
    NSLog(@"[MaoX] JIT helper request %lld (pid %d) finished with status %d%@%@", request.token, request.pid, status,
          status == JH_OK ? @"" : @": ", status == JH_OK ? @"" : (text ?: @""));
    maox_jit_helper_callback callback = request.callback;
    int64_t token = request.token;
    char *utf8 = strdup((text ?: @"").UTF8String);
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        callback(token, status, utf8);
        free(utf8);
    });
}

static NSString *describeError(NSError *error) {
    NSString *text = [NSString stringWithFormat:@"%@ (%@ %ld)", error.localizedDescription, error.domain, (long)error.code];
    NSError *underlying = error.userInfo[NSUnderlyingErrorKey];
    return underlying ? [NSString stringWithFormat:@"%@; %@", text, describeError(underlying)] : text;
}

// Runs on stateQueue. iOS did not start the helper: report it with iOS's reason and what this process logged about it.
static void startFailed(MaoXJITHelperRequest *request, NSString *identifier) {
    if (request.done || request.failing) {
        return;
    }
    request.failing = YES;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        NSString *log = recentExtensionLog(request.started);
        dispatch_async(stateQueue(), ^{
            NSString *reason = request.startError ? [@" - " stringByAppendingString:request.startError] : @"";
            finish(request, JH_START_FAILED,
                   [NSString stringWithFormat:@"%@: iOS did not start the process%@\n%@", identifier, reason, log]);
        });
    });
}

// Runs on stateQueue, once the request has an identifier.
static void stopHelper(MaoXJITHelperRequest *request, id extension) {
    if ([extension respondsToSelector:@selector(cancelExtensionRequestWithIdentifier:)]) {
        [extension cancelExtensionRequestWithIdentifier:request.identifier];
    }
    if ([extension respondsToSelector:@selector(_kill:)]) {
        [extension _kill:SIGKILL];
    }
}

EXPORT int maox_jit_helper_availability(void) {
    if (!helperIdentifier()) {
        return JH_MISSING;
    }
    if (![NSProcessInfo.processInfo isOperatingSystemAtLeastVersion:(NSOperatingSystemVersion){17, 4, 0}]) {
        return JH_UNSUPPORTED_OS;
    }
    if (getenv("LC_HOME_PATH")) {
        return JH_LIVECONTAINER;
    }
    if (!extensionClass()) {
        return JH_API_MISSING;
    }
    return JH_OK;
}

// Starts one request. Returns JH_OK when the request was handed to iOS: the callback then runs exactly once, on a
// background thread, with the helper's JSON response (JH_OK) or a failure status and message. Any other return value
// means nothing was started and the callback is not called. Not for the main thread: looking the extension up talks
// to system daemons.
EXPORT int maox_jit_helper_request(const char *json, int64_t token, maox_jit_helper_callback callback) {
    if (!json || !callback) {
        return JH_BAD_REQUEST;
    }
    int availability = maox_jit_helper_availability();
    if (availability != JH_OK) {
        return availability;
    }
    NSString *identifier = helperIdentifier();
    NSData *data = [NSData dataWithBytes:json length:strlen(json)];
    __block int status = JH_OK;
    dispatch_sync(stateQueue(), ^{
        if (current) {
            status = JH_BUSY;
            return;
        }
        Class cls = extensionClass();
        if (![cls respondsToSelector:@selector(extensionWithIdentifier:error:)]) {
            status = JH_API_MISSING;
            return;
        }
        NSError *error = nil;
        id extension = [cls extensionWithIdentifier:identifier error:&error];
        if (!extension) {
            NSLog(@"[MaoX] JIT helper %@ is not registered: %@", identifier, error);
            setLastError([NSString stringWithFormat:@"%@: %@", identifier, error ? describeError(error) : @"not found"]);
            status = JH_NOT_REGISTERED;
            return;
        }
        SEL required[] = {
            @selector(beginExtensionRequestWithInputItems:completion:), @selector(pidForRequestIdentifier:),
            @selector(setRequestCompletionBlock:), @selector(setRequestCancellationBlock:),
            @selector(setRequestInterruptionBlock:),
        };
        for (size_t i = 0; i < sizeof(required) / sizeof(required[0]); i++) {
            if (![extension respondsToSelector:required[i]]) {
                NSLog(@"[MaoX] NSExtension has no %@", NSStringFromSelector(required[i]));
                status = JH_API_MISSING;
                return;
            }
        }

        MaoXJITHelperRequest *request = [MaoXJITHelperRequest new];
        request.extension = extension;
        request.token = token;
        request.callback = callback;
        request.started = [NSDate dateWithTimeIntervalSinceNow:-1];
        current = request;
        if ([extension respondsToSelector:@selector(setPreferredLanguages:)]) {
            [extension setPreferredLanguages:@[]]; // as LiveContainer does before starting LiveProcess
        }

        // The blocks belong to the NSExtension object; they are replaced for every request and may run on any queue
        [extension setRequestCompletionBlock:^(NSUUID *uuid, NSArray *items) {
            NSData *payload = nil;
            for (id item in items) {
                if ([item isKindOfClass:NSExtensionItem.class]) {
                    id value = ((NSExtensionItem *)item).userInfo[ResponseKey];
                    if ([value isKindOfClass:NSData.class] && [value length] > 0) {
                        payload = value;
                        break;
                    }
                }
            }
            NSString *text = payload ? [[NSString alloc] initWithData:payload encoding:NSUTF8StringEncoding] : nil;
            dispatch_async(stateQueue(), ^{
                if (text) {
                    finish(request, JH_OK, text);
                } else {
                    finish(request, JH_NO_RESPONSE, @"The JIT helper finished without a response");
                }
            });
        }];
        [extension setRequestCancellationBlock:^(NSUUID *uuid, NSError *cancelError) {
            NSString *message = cancelError ? describeError(cancelError) : @"The JIT helper request was cancelled";
            dispatch_async(stateQueue(), ^{
                if (!request.identifier && !request.cancelled) {
                    // A failed launch reports its reason here, around the time the begin completion gets no identifier
                    request.startError = message;
                    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC), stateQueue(), ^{
                        startFailed(request, identifier);
                    });
                    return;
                }
                finish(request, JH_CANCELLED, message);
            });
        }];
        [extension setRequestInterruptionBlock:^(NSUUID *uuid) {
            dispatch_async(stateQueue(), ^{
                finish(request, JH_INTERRUPTED, @"The JIT helper exited unexpectedly");
            });
        }];

        NSExtensionItem *item = [NSExtensionItem new];
        item.userInfo = @{RequestKey: data};
        NSLog(@"[MaoX] Starting JIT helper %@ (request %lld)", identifier, token);
        // From the main thread, like LiveContainer and Husk
        dispatch_async(dispatch_get_main_queue(), ^{
            [extension beginExtensionRequestWithInputItems:@[item] completion:^(NSUUID *uuid) {
                dispatch_async(stateQueue(), ^{
                    if (!uuid) {
                        // iOS's reason, if any, comes through the cancellation block: give it a moment
                        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC / 2), stateQueue(), ^{
                            startFailed(request, identifier);
                        });
                        return;
                    }
                    request.identifier = uuid;
                    request.pid = [extension pidForRequestIdentifier:uuid];
                    NSLog(@"[MaoX] JIT helper request %lld runs as pid %d", request.token, request.pid);
                    if (request.cancelled) {
                        // Cancelled before iOS said which request it was: stop the helper now
                        stopHelper(request, extension);
                    }
                });
            }];
        });
    });
    return status;
}

// Stops the request with this token (timeouts). The callback reports JH_CANCELLED right away. Killing the helper is
// only meant for a helper that has not attached yet: the launcher stops waiting before that point, never after.
EXPORT void maox_jit_helper_cancel(int64_t token) {
    dispatch_async(stateQueue(), ^{
        MaoXJITHelperRequest *request = current;
        if (!request || request.token != token) {
            return;
        }
        request.cancelled = YES;
        id extension = request.extension;
        if (request.identifier) {
            stopHelper(request, extension);
        }
        finish(request, JH_CANCELLED, @"Cancelled by the launcher");
    });
}

// Why the last maox_jit_helper_request returned JH_NOT_REGISTERED (diagnostics). The caller frees the string.
EXPORT char *maox_jit_helper_last_error(void) {
    __block char *text = NULL;
    dispatch_sync(stateQueue(), ^{
        text = strdup(lastError ?: "");
    });
    return text;
}

// Process ID of the running helper request, 0 when there is none (diagnostics).
EXPORT int maox_jit_helper_pid(void) {
    __block pid_t pid = 0;
    dispatch_sync(stateQueue(), ^{
        pid = current ? current.pid : 0;
    });
    return pid;
}
