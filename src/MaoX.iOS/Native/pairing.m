// Device-initiated pairing (iOS 27+) for the built-in JIT. Runs libmaoxpair (src/MaoX.iOS/Pairing) on a
// background thread, advertises it with NetService, keeps the app running while the user is in Settings
// (BGContinuedProcessingTask, otherwise an ordinary background task) and shows the PIN in a notification.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <BackgroundTasks/BackgroundTasks.h>
#import <UserNotifications/UserNotifications.h>
#include <dlfcn.h>
#include <stdatomic.h>

#define EXPORT __attribute__((visibility("default")))

// Availability / start results
enum { PA_OK = 0, PA_UNSUPPORTED_OS = 1, PA_MISSING = 2, PA_BUSY = 3, PA_BAD_REQUEST = 4 };
// Events; DONE, FAILED, CANCELLED, EXPIRED and PUBLISH_FAILED end the session
enum { PE_ADVERTISING = 1, PE_PIN = 2, PE_DONE = 3, PE_FAILED = 4, PE_CANCELLED = 5, PE_EXPIRED = 6, PE_PUBLISH_FAILED = 7 };

typedef void (*maox_pair_event_cb)(int64_t token, int32_t kind, const char *text);

typedef void (*ready_fn)(void *context, const char *service, uint16_t port, const char *txt);
typedef void (*pin_fn)(void *context, const char *pin);
static struct {
    void *(*cancel_new)(void);
    void (*cancel_signal)(const void *);
    void (*cancel_free)(void *);
    int32_t (*run)(const char *, const char *, ready_fn, pin_fn, void *, const void *, char **, char **, char **);
    void (*string_free)(char *);
} lib;

static atomic_bool busy;

static BOOL load_library(void) {
    static BOOL loaded;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        NSString *path = [NSBundle.mainBundle.privateFrameworksPath stringByAppendingPathComponent:@"libmaoxpair.dylib"];
        void *handle = dlopen(path.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
        if (!handle) {
            NSLog(@"[MaoX] pairing: %s", dlerror());
            return;
        }
        lib.cancel_new = dlsym(handle, "maox_pair_cancel_new");
        lib.cancel_signal = dlsym(handle, "maox_pair_cancel_signal");
        lib.cancel_free = dlsym(handle, "maox_pair_cancel_free");
        lib.run = dlsym(handle, "maox_pair_run");
        lib.string_free = dlsym(handle, "maox_pair_string_free");
        loaded = lib.cancel_new && lib.cancel_signal && lib.cancel_free && lib.run && lib.string_free;
    });
    return loaded;
}

static NSString *take_string(char *s) {
    if (!s)
        return @"";
    NSString *result = [NSString stringWithUTF8String:s] ?: @"";
    lib.string_free(s);
    return result;
}

@interface MaoXPairSession : NSObject <NSNetServiceDelegate>
@property(nonatomic) int64_t token;
@property(nonatomic) maox_pair_event_cb callback;
@property(nonatomic, copy) NSDictionary<NSString *, NSString *> *texts;
@property(nonatomic) void *cancel;
@property(nonatomic) NSNetService *service;
@property(nonatomic) id task; // BGContinuedProcessingTask
@property(nonatomic) UIBackgroundTaskIdentifier backgroundId;
@property(nonatomic) NSTimer *ticker;
@property(nonatomic) BOOL pinShown, expired, finished;
@property(nonatomic, copy) NSString *publishError;
@end

static MaoXPairSession *current; // main queue only

static void on_ready(void *context, const char *service, uint16_t port, const char *txt);
static void on_pin(void *context, const char *pin);

@implementation MaoXPairSession

- (NSString *)text:(NSString *)key {
    return self.texts[key] ?: @"";
}

- (void)emit:(int32_t)kind text:(NSString *)text {
    self.callback(self.token, kind, (text ?: @"").UTF8String);
}

- (void)begin {
    [UNUserNotificationCenter.currentNotificationCenter
        requestAuthorizationWithOptions:UNAuthorizationOptionAlert | UNAuthorizationOptionSound
                      completionHandler:^(BOOL granted, NSError *error) {}];

    __weak MaoXPairSession *weak = self;
    self.backgroundId = [UIApplication.sharedApplication beginBackgroundTaskWithName:@"MaoX pairing" expirationHandler:^{
        MaoXPairSession *s = weak;
        if (!s)
            return;
        // A granted continued-processing task keeps running on its own
        if (!s.task)
            [s expire];
        [s endBackgroundTask];
    }];
    [self requestContinuedProcessing];
    self.ticker = [NSTimer scheduledTimerWithTimeInterval:5 repeats:YES block:^(NSTimer *timer) {
        [weak tick];
    }];

    void *context = (__bridge void *)self;
    const void *cancel = self.cancel;
    NSString *name = [self text:@"name"], *model = [self text:@"model"];
    MaoXPairSession *session = self;
    [NSThread detachNewThreadWithBlock:^{
        char *file = NULL, *device = NULL, *error = NULL;
        int32_t rc = lib.run(name.UTF8String, model.UTF8String, on_ready, on_pin, context, cancel, &file, &device, &error);
        NSString *fileText = take_string(file), *deviceText = take_string(device), *errorText = take_string(error);
        dispatch_async(dispatch_get_main_queue(), ^{
            [session finish:rc file:fileText device:deviceText error:errorText];
        });
    }];
}

- (void)requestContinuedProcessing {
    if (@available(iOS 26.0, *)) {
        static BOOL registered;
        NSString *identifier = [NSBundle.mainBundle.bundleIdentifier stringByAppendingString:@".pairing"];
        @try {
            if (!registered)
                registered = [BGTaskScheduler.sharedScheduler registerForTaskWithIdentifier:identifier
                                                                                 usingQueue:dispatch_get_main_queue()
                                                                              launchHandler:^(BGTask *task) {
                    MaoXPairSession *s = current;
                    if (s && !s.finished)
                        [s attachTask:task];
                    else
                        [task setTaskCompletedWithSuccess:YES];
                }];
            if (!registered) {
                NSLog(@"[MaoX] pairing: could not register %@", identifier);
                return;
            }
            BGContinuedProcessingTaskRequest *request =
                [[BGContinuedProcessingTaskRequest alloc] initWithIdentifier:identifier
                                                                       title:[self text:@"title"]
                                                                    subtitle:[self text:@"waiting"]];
            request.strategy = BGContinuedProcessingTaskRequestSubmissionStrategyQueue;
            NSError *error;
            if (![BGTaskScheduler.sharedScheduler submitTaskRequest:request error:&error])
                NSLog(@"[MaoX] pairing: continued processing unavailable: %@", error);
        } @catch (NSException *e) {
            NSLog(@"[MaoX] pairing: continued processing unavailable: %@", e);
        }
    }
}

- (void)attachTask:(BGTask *)task {
    if (@available(iOS 26.0, *)) {
        BGContinuedProcessingTask *continued = (BGContinuedProcessingTask *)task;
        self.task = continued;
        continued.progress.totalUnitCount = 100;
        continued.progress.completedUnitCount = self.pinShown ? 50 : 5;
        __weak MaoXPairSession *weak = self;
        continued.expirationHandler = ^{
            dispatch_async(dispatch_get_main_queue(), ^{
                [weak expire];
            });
        };
    }
}

// The system expires continued-processing tasks that stop reporting progress, so keep it moving while waiting
- (void)tick {
    if (@available(iOS 26.0, *)) {
        BGContinuedProcessingTask *task = self.task;
        if (!task)
            return;
        int64_t limit = self.pinShown ? 95 : 45;
        if (task.progress.completedUnitCount < limit)
            task.progress.completedUnitCount += 1;
    }
}

- (void)endBackgroundTask {
    if (self.backgroundId != UIBackgroundTaskInvalid) {
        [UIApplication.sharedApplication endBackgroundTask:self.backgroundId];
        self.backgroundId = UIBackgroundTaskInvalid;
    }
}

- (void)expire {
    if (self.finished)
        return;
    self.expired = YES;
    lib.cancel_signal(self.cancel);
}

- (void)cancelPairing {
    if (!self.finished)
        lib.cancel_signal(self.cancel);
}

- (void)publish:(NSString *)name port:(uint16_t)port txt:(NSDictionary<NSString *, NSData *> *)txt {
    if (self.finished)
        return;
    NSNetService *service = [[NSNetService alloc] initWithDomain:@"" type:@"_remotepairing-pairable-host._tcp." name:name port:port];
    [service setTXTRecordData:[NSNetService dataFromTXTRecordDictionary:txt]];
    service.delegate = self;
    self.service = service;
    [service publish];
}

- (void)netServiceDidPublish:(NSNetService *)sender {
    [self emit:PE_ADVERTISING text:sender.name];
}

- (void)netService:(NSNetService *)sender didNotPublish:(NSDictionary<NSString *, NSNumber *> *)errorDict {
    self.publishError = [NSString stringWithFormat:@"%@", errorDict[NSNetServicesErrorCode] ?: @"?"];
    [self cancelPairing];
}

- (void)showPin:(NSString *)pin {
    if (self.finished)
        return;
    self.pinShown = YES;
    NSString *subtitle = [[self text:@"pinSubtitle"] stringByReplacingOccurrencesOfString:@"{pin}" withString:pin];
    if (@available(iOS 26.0, *)) {
        BGContinuedProcessingTask *task = self.task;
        if (task) {
            [task updateTitle:[self text:@"title"] subtitle:subtitle];
            task.progress.completedUnitCount = MAX(task.progress.completedUnitCount, 50);
        }
    }
    [self notify:@"pin" title:[self text:@"pinTitle"]
            body:[[self text:@"pinBody"] stringByReplacingOccurrencesOfString:@"{pin}" withString:pin]];
    [self emit:PE_PIN text:pin];
}

- (void)notify:(NSString *)name title:(NSString *)title body:(NSString *)body {
    UNMutableNotificationContent *content = [UNMutableNotificationContent new];
    content.title = title;
    content.body = body;
    content.sound = UNNotificationSound.defaultSound;
    NSString *identifier = [@"io.github.jinruimaomao.maox.pairing." stringByAppendingString:name];
    [UNUserNotificationCenter.currentNotificationCenter
        addNotificationRequest:[UNNotificationRequest requestWithIdentifier:identifier content:content trigger:nil]
         withCompletionHandler:nil];
}

- (void)finish:(int32_t)rc file:(NSString *)file device:(NSString *)device error:(NSString *)error {
    self.finished = YES;
    [self.ticker invalidate];
    self.ticker = nil;
    [self.service stop];
    self.service = nil;
    [UNUserNotificationCenter.currentNotificationCenter
        removeDeliveredNotificationsWithIdentifiers:@[ @"io.github.jinruimaomao.maox.pairing.pin" ]];

    if (rc == 0) {
        if (UIApplication.sharedApplication.applicationState != UIApplicationStateActive)
            [self notify:@"done" title:[self text:@"doneTitle"] body:[self text:@"doneBody"]];
        NSData *json = [NSJSONSerialization dataWithJSONObject:@{ @"file" : file, @"device" : device } options:0 error:nil];
        [self emit:PE_DONE text:[[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]];
    } else if (self.publishError) {
        [self emit:PE_PUBLISH_FAILED text:self.publishError];
    } else if (self.expired) {
        [self emit:PE_EXPIRED text:@""];
    } else if (rc == 1) {
        [self emit:PE_CANCELLED text:@""];
    } else {
        [self emit:PE_FAILED text:error];
    }

    if (@available(iOS 26.0, *)) {
        BGContinuedProcessingTask *task = self.task;
        if (task) {
            if (rc == 0)
                task.progress.completedUnitCount = 100;
            [task setTaskCompletedWithSuccess:rc == 0];
        }
    }
    self.task = nil;
    [self endBackgroundTask];
    lib.cancel_free(self.cancel);
    self.cancel = NULL;
    if (current == self)
        current = nil;
    atomic_store(&busy, false);
}

@end

static void on_ready(void *context, const char *service, uint16_t port, const char *txt) {
    MaoXPairSession *session = (__bridge MaoXPairSession *)context;
    NSString *name = [NSString stringWithUTF8String:service] ?: @"";
    NSMutableDictionary<NSString *, NSData *> *records = [NSMutableDictionary new];
    for (NSString *line in [[NSString stringWithUTF8String:txt] ?: @"" componentsSeparatedByString:@"\n"]) {
        NSRange eq = [line rangeOfString:@"="];
        if (eq.location != NSNotFound)
            records[[line substringToIndex:eq.location]] =
                [[line substringFromIndex:eq.location + 1] dataUsingEncoding:NSUTF8StringEncoding];
    }
    dispatch_async(dispatch_get_main_queue(), ^{
        [session publish:name port:port txt:records];
    });
}

static void on_pin(void *context, const char *pin) {
    MaoXPairSession *session = (__bridge MaoXPairSession *)context;
    NSString *code = [NSString stringWithUTF8String:pin] ?: @"";
    dispatch_async(dispatch_get_main_queue(), ^{
        [session showPin:code];
    });
}

EXPORT int32_t maox_pair_availability(void) {
    if (![NSProcessInfo.processInfo isOperatingSystemAtLeastVersion:(NSOperatingSystemVersion){27, 0, 0}])
        return PA_UNSUPPORTED_OS;
    if (!load_library())
        return PA_MISSING;
    return atomic_load(&busy) ? PA_BUSY : PA_OK;
}

// json: { name, model, title, waiting, pinSubtitle, pinTitle, pinBody, doneTitle, doneBody } ("{pin}" is replaced)
EXPORT int32_t maox_pair_start(const char *json, int64_t token, maox_pair_event_cb callback) {
    int32_t availability = maox_pair_availability();
    if (availability != PA_OK)
        return availability;
    NSDictionary *texts = json && callback
        ? [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:json length:strlen(json)] options:0 error:nil]
        : nil;
    if (![texts isKindOfClass:NSDictionary.class])
        return PA_BAD_REQUEST;
    bool expected = false;
    if (!atomic_compare_exchange_strong(&busy, &expected, true))
        return PA_BUSY;

    MaoXPairSession *session = [MaoXPairSession new];
    session.token = token;
    session.callback = callback;
    session.texts = texts;
    session.cancel = lib.cancel_new();
    session.backgroundId = UIBackgroundTaskInvalid;
    dispatch_async(dispatch_get_main_queue(), ^{
        current = session;
        [session begin];
    });
    return PA_OK;
}

EXPORT void maox_pair_cancel(int64_t token) {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (current && current.token == token)
            [current cancelPairing];
    });
}
