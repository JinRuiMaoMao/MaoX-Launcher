// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
#pragma once

#import <UIKit/UIKit.h>

#include <stdbool.h>
#include "environ.h"
#include "jni.h"

// Everything goes to stdout so it ends up in the game log
#define NSLog(args...) customNSLog(__FILE__,__LINE__,__PRETTY_FUNCTION__,args);
#define NSDebugLog(...) NSLog(__VA_ARGS__)

// Touch actions for CallbackBridge_nativeSendCursorPos
#define ACTION_DOWN 0
#define ACTION_UP 1
#define ACTION_MOVE 2
#define ACTION_MOVE_MOTION 3

// GLFW event types
#define EVENT_TYPE_CHAR 1000
#define EVENT_TYPE_CHAR_MODS 1001
#define EVENT_TYPE_CURSOR_ENTER 1002
#define EVENT_TYPE_CURSOR_POS 1003
#define EVENT_TYPE_FRAMEBUFFER_SIZE 1004
#define EVENT_TYPE_KEY 1005
#define EVENT_TYPE_MOUSE_BUTTON 1006
#define EVENT_TYPE_SCROLL 1007
#define EVENT_TYPE_WINDOW_POS 1008
#define EVENT_TYPE_WINDOW_SIZE 1009

#define GLFW_FOCUSED 0x00020001
#define GLFW_VISIBLE 0x00020004

#define RENDERER_NAME_GL4ES "libgl4es_114.dylib"
#define RENDERER_NAME_MTL_ANGLE "libtinygl4angle.dylib"
#define RENDERER_NAME_MOBILEGLUES "libmobileglues.dylib"

#define CLIPBOARD_COPY 2000
#define CLIPBOARD_PASTE 2001

#define CS_DEBUGGED 0x10000000
int csops(pid_t pid, unsigned int ops, void *useraddr, size_t usersize);

BOOL getEntitlementValue(NSString *key);
BOOL isJITEnabled(void);

// Breakpoint-based protocol understood by StikDebug's UniversalJIT26 script (iOS 26 with TXM)
void* JIT26CreateRegionLegacy(size_t len);
void* JIT26PrepareRegion(void *addr, size_t len);
void JIT26PrepareRegionForPatching(void *addr, size_t len);
void JIT26SetDetachAfterFirstBr(BOOL value);
void JIT26SendJITScript(NSString* script);
BOOL JIT26IsLikelyDebuggerKeepAttached(void);

typedef enum {
    JIT_FLAG_IS_IOS_26 = 1 << 0,
    JIT_FLAG_FORCE_MIRRORED = 1 << 1,
    JIT_FLAG_HAS_TXM = 1 << 2,
} JITFlags;
JITFlags DeviceGetJITFlags(BOOL refresh);
BOOL DeviceHasJITFlags(JITFlags flags);

void init_bypassDyldLibValidation(void);
void init_hookFunctions(void);
BOOL PLPatchMachOPlatformForFile(const char *path);

void customNSLog(const char *file, int lineNumber, const char *functionName, NSString *format, ...);
jstring UIKit_accessClipboard(JNIEnv* env, jint action, jstring copySrc);

// Set by maox_launch
extern void *maox_surface_layer;
extern char maox_resolv_conf[PATH_MAX];
void maox_handle_exit(int code);

void sendData(short type, int i1, int i2, short i3, short i4);
void sendDataFloat(short type, float i1, float i2, short i3, short i4);
void closeGLFWWindow(void);

void CallbackBridge_nativeSetInputReady(BOOL inputReady);
BOOL CallbackBridge_nativeSendChar(jchar codepoint);
BOOL CallbackBridge_nativeSendCharMods(jchar codepoint, int mods);
void CallbackBridge_nativeSendCursorPos(char event, CGFloat x, CGFloat y);
void CallbackBridge_nativeSendKey(int key, int scancode, int action, int mods);
void CallbackBridge_nativeSendMouseButton(int button, int action, int mods);
void CallbackBridge_nativeSendScreenSize(int width, int height);
void CallbackBridge_nativeSendScroll(CGFloat xoffset, CGFloat yoffset);
