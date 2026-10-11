// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
#import <Foundation/Foundation.h>

#include <dlfcn.h>
#include "bridge_tbl.h"
#include "gl_bridge.h"
#include "../utils.h"

__thread basic_render_window_t* currentBundle;

static EGLDisplay g_EglDisplay;
static egl_library handle;

static bool dlsym_EGL(void) {
    void* dl_handle = dlopen("@rpath/libtinygl4angle.dylib", RTLD_GLOBAL);
    if (!dl_handle) {
        NSLog(@"EGLBridge: dlopen libtinygl4angle failed: %s", dlerror());
        return false;
    }
    handle.eglBindAPI = dlsym(dl_handle, "eglBindAPI");
    handle.eglChooseConfig = dlsym(dl_handle, "eglChooseConfig");
    handle.eglCreateContext = dlsym(dl_handle, "eglCreateContext");
    handle.eglCreateWindowSurface = dlsym(dl_handle, "eglCreateWindowSurface");
    handle.eglDestroyContext = dlsym(dl_handle, "eglDestroyContext");
    handle.eglDestroySurface = dlsym(dl_handle, "eglDestroySurface");
    handle.eglGetConfigAttrib = dlsym(dl_handle, "eglGetConfigAttrib");
    handle.eglGetCurrentContext = dlsym(dl_handle, "eglGetCurrentContext");
    handle.eglGetDisplay = dlsym(dl_handle, "eglGetDisplay");
    handle.eglGetError = dlsym(dl_handle, "eglGetError");
    handle.eglGetPlatformDisplay = dlsym(dl_handle, "eglGetPlatformDisplay");
    handle.eglInitialize = dlsym(dl_handle, "eglInitialize");
    handle.eglMakeCurrent = dlsym(dl_handle, "eglMakeCurrent");
    handle.eglSwapBuffers = dlsym(dl_handle, "eglSwapBuffers");
    handle.eglReleaseThread = dlsym(dl_handle, "eglReleaseThread");
    handle.eglSwapInterval = dlsym(dl_handle, "eglSwapInterval");
    handle.eglTerminate = dlsym(dl_handle, "eglTerminate");
    handle.eglGetCurrentSurface = dlsym(dl_handle, "eglGetCurrentSurface");
    return true;
}

static bool gl_init(void) {
    if (!dlsym_EGL()) {
        return false;
    }

    g_EglDisplay = handle.eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (g_EglDisplay == EGL_NO_DISPLAY) {
        NSLog(@"EGLBridge: eglGetDisplay(EGL_DEFAULT_DISPLAY) returned EGL_NO_DISPLAY");
        return false;
    }
    if (!handle.eglInitialize(g_EglDisplay, NULL, NULL)) {
        NSLog(@"EGLBridge: Error eglInitialize() failed: 0x%x", handle.eglGetError());
        return false;
    }
    return true;
}

static gl_render_window_t* gl_init_context(gl_render_window_t *share) {
    gl_render_window_t* bundle = calloc(1, sizeof(gl_render_window_t));

    NSString *renderer = NSProcessInfo.processInfo.environment[@"RENDERER"];
    BOOL angleDesktopGL = [renderer isEqualToString:@ RENDERER_NAME_MTL_ANGLE];

    const EGLint attribs[] = {
        EGL_RED_SIZE, 8,
        EGL_GREEN_SIZE, 8,
        EGL_BLUE_SIZE, 8,
        EGL_ALPHA_SIZE, 8,
        EGL_DEPTH_SIZE, 24,
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT|EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, angleDesktopGL ? EGL_OPENGL_BIT : EGL_OPENGL_ES3_BIT,
        EGL_NONE
    };

    EGLint num_configs;
    EGLint vid;
    if (!handle.eglChooseConfig(g_EglDisplay, attribs, &bundle->config, 1, &num_configs) || num_configs <= 0) {
        NSLog(@"EGLBridge: Error couldn't get an EGL visual config: 0x%x", handle.eglGetError());
        free(bundle);
        return NULL;
    }

    if (!handle.eglGetConfigAttrib(g_EglDisplay, bundle->config, EGL_NATIVE_VISUAL_ID, &vid)) {
        NSLog(@"EGLBridge: Error eglGetConfigAttrib() failed: 0x%x", handle.eglGetError());
        free(bundle);
        return NULL;
    }

    EGLBoolean bindResult;
    if (angleDesktopGL) {
        NSLog(@"EGLBridge: Binding to desktop OpenGL");
        bindResult = handle.eglBindAPI(EGL_OPENGL_API);
    } else {
        NSLog(@"EGLBridge: Binding to OpenGL ES");
        bindResult = handle.eglBindAPI(EGL_OPENGL_ES_API);
    }
    if (!bindResult) NSLog(@"EGLBridge: bind failed: 0x%x", handle.eglGetError());

    bundle->surface = handle.eglCreateWindowSurface(g_EglDisplay, bundle->config, (EGLNativeWindowType)maox_surface_layer, NULL);
    if (!bundle->surface) {
        NSLog(@"EGLBridge: eglCreateWindowSurface finished with error: 0x%x", handle.eglGetError());
        free(bundle);
        return NULL;
    }

    const EGLint ctx_attribs[] = {
        EGL_CONTEXT_CLIENT_VERSION, 3,
        EGL_NONE
    };
    bundle->context = handle.eglCreateContext(g_EglDisplay, bundle->config, share ? share->context : EGL_NO_CONTEXT, ctx_attribs);
    if (!bundle->context) {
        NSLog(@"EGLBridge: Error eglCreateContext finished with error: 0x%x", handle.eglGetError());
        free(bundle);
        return NULL;
    }
    return bundle;
}

static void gl_make_current(gl_render_window_t* bundle) {
    if (!bundle) {
        if (handle.eglMakeCurrent(g_EglDisplay, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT)) {
            currentBundle = NULL;
        }
        return;
    }

    if (handle.eglMakeCurrent(g_EglDisplay, bundle->surface, bundle->surface, bundle->context)) {
        currentBundle = (basic_render_window_t *)bundle;
    } else {
        NSLog(@"EGLBridge: eglMakeCurrent returned with error: 0x%x", handle.eglGetError());
    }
}

static void gl_swap_buffers(void) {
    if (!currentBundle) return;
    if (!handle.eglSwapBuffers(g_EglDisplay, currentBundle->gl.surface) && handle.eglGetError() == EGL_BAD_SURFACE) {
        NSLog(@"eglSwapBuffers error 0x%x", handle.eglGetError());
    }
}

static void gl_swap_interval(int swapInterval) {
    handle.eglSwapInterval(g_EglDisplay, swapInterval);
}

static void gl_terminate(void) {
    handle.eglMakeCurrent(g_EglDisplay, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    if (currentBundle) {
        handle.eglDestroySurface(g_EglDisplay, currentBundle->gl.surface);
        handle.eglDestroyContext(g_EglDisplay, currentBundle->gl.context);
    }
    handle.eglTerminate(g_EglDisplay);
    handle.eglReleaseThread();
    free(currentBundle);
    currentBundle = NULL;
}

void set_gl_bridge_tbl(void) {
    br_init = gl_init;
    br_init_context = (br_init_context_t) gl_init_context;
    br_make_current = (br_make_current_t) gl_make_current;
    br_swap_buffers = gl_swap_buffers;
    br_swap_interval = gl_swap_interval;
    br_terminate = gl_terminate;
}
