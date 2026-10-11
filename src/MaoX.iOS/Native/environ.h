// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
// Shared state of the input / render bridges (compiled with -fcommon, one definition per symbol).
#pragma once

#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include "jni.h"

typedef struct {
    short type;
    union {
        int i1;
        float f1;
    };
    union {
        int i2;
        float f2;
    };
    short i3;
    short i4;
} GLFWInputEvent;

typedef void GLFW_invoke_Char_func(void* window, unsigned int codepoint);
typedef void GLFW_invoke_CharMods_func(void* window, unsigned int codepoint, int mods);
typedef void GLFW_invoke_CursorEnter_func(void* window, int entered);
typedef void GLFW_invoke_CursorPos_func(void* window, double xpos, double ypos);
typedef void GLFW_invoke_FramebufferSize_func(void* window, int width, int height);
typedef void GLFW_invoke_Key_func(void* window, int key, int scancode, int action, int mods);
typedef void GLFW_invoke_MouseButton_func(void* window, int button, int action, int mods);
typedef void GLFW_invoke_Scroll_func(void* window, double xoffset, double yoffset);
typedef void GLFW_invoke_WindowPos_func(void* window, int x, int y);
typedef void GLFW_invoke_WindowSize_func(void* window, int width, int height);

jclass class_CTCClipboard;
jmethodID method_SystemClipboardDataReceived;

atomic_size_t eventCounter;
GLFWInputEvent events[8000];
double cursorX, cursorY, cLastX, cLastY;
jmethodID method_internalWindowSizeChanged;
jclass vmGlfwClass;
jboolean isGrabbing;
jbyte* keyDownBuffer;
JavaVM* runtimeJavaVMPtr;
JNIEnv* runtimeJNIEnvPtr;
long showingWindow;
bool isInputReady, isUseStackQueueCall;
int windowWidth, windowHeight;

#define ADD_CALLBACK_WWIN(NAME) \
    GLFW_invoke_##NAME##_func* GLFW_invoke_##NAME;
ADD_CALLBACK_WWIN(Char);
ADD_CALLBACK_WWIN(CharMods);
ADD_CALLBACK_WWIN(CursorEnter);
ADD_CALLBACK_WWIN(CursorPos);
ADD_CALLBACK_WWIN(FramebufferSize);
ADD_CALLBACK_WWIN(Key);
ADD_CALLBACK_WWIN(MouseButton);
ADD_CALLBACK_WWIN(Scroll);
ADD_CALLBACK_WWIN(WindowPos);
ADD_CALLBACK_WWIN(WindowSize);
#undef ADD_CALLBACK_WWIN

uint64_t hwRedirectOrig[6], hwRedirectTarget[6];
