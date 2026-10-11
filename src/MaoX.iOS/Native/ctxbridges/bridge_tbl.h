// Adapted from Amethyst-iOS (https://github.com/AngelAuraMC/Amethyst-iOS), GPL-3.0.
#pragma once

#include <stdbool.h>
#include <stdlib.h>
#include "gl_bridge.h"

typedef union {
    gl_render_window_t gl;
} basic_render_window_t;

typedef basic_render_window_t* (*br_init_context_t)(basic_render_window_t* share);
typedef void (*br_make_current_t)(basic_render_window_t* bundle);

bool (*br_init)(void);
br_init_context_t br_init_context;
br_make_current_t br_make_current;
void (*br_swap_buffers)(void);
void (*br_swap_interval)(int swapInterval);
void (*br_terminate)(void);

extern __thread basic_render_window_t* currentBundle;
static inline basic_render_window_t* br_get_current(void) {
    return currentBundle;
}
