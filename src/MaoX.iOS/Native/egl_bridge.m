// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
// The pojav* functions are looked up by the patched LWJGL GLFW through dlsym(RTLD_DEFAULT).
#import <Foundation/Foundation.h>

#include <dlfcn.h>
#include <stdlib.h>
#include <string.h>

#include "jni.h"
#include "glfw_keycodes.h"
#include "ctxbridges/bridge_tbl.h"
#include "utils.h"

#define EXPORT __attribute__((visibility("default")))

static int clientAPI;

static void JNI_LWJGL_changeRenderer(const char* value_c) {
    JNIEnv *env;
    (*runtimeJavaVMPtr)->GetEnv(runtimeJavaVMPtr, (void **)&env, JNI_VERSION_1_4);
    jstring key = (*env)->NewStringUTF(env, "org.lwjgl.opengl.libname");
    jstring value = (*env)->NewStringUTF(env, value_c);
    jclass clazz = (*env)->FindClass(env, "java/lang/System");
    jmethodID method = (*env)->GetStaticMethodID(env, clazz, "setProperty", "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
    (*env)->CallStaticObjectMethod(env, clazz, method, key, value);
}

EXPORT void pojavTerminate(void) {
    CallbackBridge_nativeSetInputReady(NO);
    if (!br_terminate) return;
    br_terminate();
}

EXPORT void* pojavGetCurrentContext(void) {
    return br_get_current();
}

EXPORT int pojavInit(BOOL useStackQueue) {
    clientAPI = GLFW_OPENGL_API;
    isInputReady = 1;
    isUseStackQueueCall = useStackQueue;
    return JNI_TRUE;
}

static int pojavInitOpenGL(void) {
    NSString *renderer = NSProcessInfo.processInfo.environment[@"RENDERER"];
    if (renderer == nil || [renderer isEqualToString:@"auto"]) {
        // No context version requested: legacy fixed-function game, use gl4es
        renderer = @ RENDERER_NAME_GL4ES;
        setenv("RENDERER", renderer.UTF8String, 1);
    }
    set_gl_bridge_tbl();
    JNI_LWJGL_changeRenderer(renderer.UTF8String);
    dlopen([NSString stringWithFormat:@"@rpath/%@", renderer].UTF8String, RTLD_GLOBAL);
    return !br_init();
}

EXPORT void pojavSetWindowHint(int hint, int value) {
    const char *renderer = getenv("RENDERER");
    if (hint == GLFW_CLIENT_API) {
        clientAPI = value;
    } else if (renderer && strcmp(renderer, "auto") == 0 && hint == GLFW_CONTEXT_VERSION_MAJOR) {
        // MobileGlues is not bundled, so OpenGL 3+ goes through Metal ANGLE
        const char *picked = value <= 2 ? RENDERER_NAME_GL4ES : RENDERER_NAME_MTL_ANGLE;
        setenv("RENDERER", picked, 1);
        JNI_LWJGL_changeRenderer(picked);
    }
}

EXPORT void pojavSwapBuffers(void) {
    if (br_swap_buffers) br_swap_buffers();
}

EXPORT void pojavMakeCurrent(basic_render_window_t* window) {
    if (br_make_current) br_make_current(window);
}

EXPORT void* pojavCreateContext(basic_render_window_t* contextSrc) {
    if (clientAPI == GLFW_NO_API) {
        // Vulkan: MoltenVK renders straight into the layer
        return maox_surface_layer;
    }

    static BOOL inited = NO;
    if (!inited) {
        inited = YES;
        if (pojavInitOpenGL() != 0) {
            NSLog(@"EGLBridge: renderer initialization failed");
            return NULL;
        }
    }
    return br_init_context(contextSrc);
}

EXPORT void pojavSwapInterval(int interval) {
    if (!br_swap_interval) return;
    br_swap_interval(interval);
}
