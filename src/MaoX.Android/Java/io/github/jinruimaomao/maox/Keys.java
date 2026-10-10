package io.github.jinruimaomao.maox;

import android.view.KeyEvent;

import org.lwjgl.glfw.CallbackBridge;

/** 安卓按键码 → GLFW 按键码。 */
public final class Keys {
    private Keys() {
    }

    public static final int GLFW_MOUSE_BUTTON_LEFT = 0;
    public static final int GLFW_MOUSE_BUTTON_RIGHT = 1;
    public static final int GLFW_MOUSE_BUTTON_MIDDLE = 2;

    public static final int GLFW_KEY_UNKNOWN = 0;
    public static final int GLFW_KEY_SPACE = 32;
    public static final int GLFW_KEY_APOSTROPHE = 39;
    public static final int GLFW_KEY_COMMA = 44;
    public static final int GLFW_KEY_MINUS = 45;
    public static final int GLFW_KEY_PERIOD = 46;
    public static final int GLFW_KEY_SLASH = 47;
    public static final int GLFW_KEY_0 = 48;
    public static final int GLFW_KEY_SEMICOLON = 59;
    public static final int GLFW_KEY_EQUAL = 61;
    public static final int GLFW_KEY_A = 65;
    public static final int GLFW_KEY_LEFT_BRACKET = 91;
    public static final int GLFW_KEY_BACKSLASH = 92;
    public static final int GLFW_KEY_RIGHT_BRACKET = 93;
    public static final int GLFW_KEY_GRAVE_ACCENT = 96;
    public static final int GLFW_KEY_ESCAPE = 256;
    public static final int GLFW_KEY_ENTER = 257;
    public static final int GLFW_KEY_TAB = 258;
    public static final int GLFW_KEY_BACKSPACE = 259;
    public static final int GLFW_KEY_INSERT = 260;
    public static final int GLFW_KEY_DELETE = 261;
    public static final int GLFW_KEY_RIGHT = 262;
    public static final int GLFW_KEY_LEFT = 263;
    public static final int GLFW_KEY_DOWN = 264;
    public static final int GLFW_KEY_UP = 265;
    public static final int GLFW_KEY_PAGE_UP = 266;
    public static final int GLFW_KEY_PAGE_DOWN = 267;
    public static final int GLFW_KEY_HOME = 268;
    public static final int GLFW_KEY_END = 269;
    public static final int GLFW_KEY_CAPS_LOCK = 280;
    public static final int GLFW_KEY_NUM_LOCK = 282;
    public static final int GLFW_KEY_F1 = 290;
    public static final int GLFW_KEY_LEFT_SHIFT = 340;
    public static final int GLFW_KEY_LEFT_CONTROL = 341;
    public static final int GLFW_KEY_LEFT_ALT = 342;
    public static final int GLFW_KEY_RIGHT_SHIFT = 344;
    public static final int GLFW_KEY_RIGHT_CONTROL = 345;
    public static final int GLFW_KEY_RIGHT_ALT = 346;

    public static int fromAndroid(int keyCode) {
        if (keyCode >= KeyEvent.KEYCODE_A && keyCode <= KeyEvent.KEYCODE_Z)
            return GLFW_KEY_A + (keyCode - KeyEvent.KEYCODE_A);
        if (keyCode >= KeyEvent.KEYCODE_0 && keyCode <= KeyEvent.KEYCODE_9)
            return GLFW_KEY_0 + (keyCode - KeyEvent.KEYCODE_0);
        if (keyCode >= KeyEvent.KEYCODE_F1 && keyCode <= KeyEvent.KEYCODE_F12)
            return GLFW_KEY_F1 + (keyCode - KeyEvent.KEYCODE_F1);
        switch (keyCode) {
            case KeyEvent.KEYCODE_SPACE: return GLFW_KEY_SPACE;
            case KeyEvent.KEYCODE_APOSTROPHE: return GLFW_KEY_APOSTROPHE;
            case KeyEvent.KEYCODE_COMMA: return GLFW_KEY_COMMA;
            case KeyEvent.KEYCODE_MINUS: return GLFW_KEY_MINUS;
            case KeyEvent.KEYCODE_PERIOD: return GLFW_KEY_PERIOD;
            case KeyEvent.KEYCODE_SLASH: return GLFW_KEY_SLASH;
            case KeyEvent.KEYCODE_SEMICOLON: return GLFW_KEY_SEMICOLON;
            case KeyEvent.KEYCODE_EQUALS: return GLFW_KEY_EQUAL;
            case KeyEvent.KEYCODE_LEFT_BRACKET: return GLFW_KEY_LEFT_BRACKET;
            case KeyEvent.KEYCODE_BACKSLASH: return GLFW_KEY_BACKSLASH;
            case KeyEvent.KEYCODE_RIGHT_BRACKET: return GLFW_KEY_RIGHT_BRACKET;
            case KeyEvent.KEYCODE_GRAVE: return GLFW_KEY_GRAVE_ACCENT;
            case KeyEvent.KEYCODE_ESCAPE: return GLFW_KEY_ESCAPE;
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_NUMPAD_ENTER: return GLFW_KEY_ENTER;
            case KeyEvent.KEYCODE_TAB: return GLFW_KEY_TAB;
            case KeyEvent.KEYCODE_DEL: return GLFW_KEY_BACKSPACE;
            case KeyEvent.KEYCODE_INSERT: return GLFW_KEY_INSERT;
            case KeyEvent.KEYCODE_FORWARD_DEL: return GLFW_KEY_DELETE;
            case KeyEvent.KEYCODE_DPAD_RIGHT: return GLFW_KEY_RIGHT;
            case KeyEvent.KEYCODE_DPAD_LEFT: return GLFW_KEY_LEFT;
            case KeyEvent.KEYCODE_DPAD_DOWN: return GLFW_KEY_DOWN;
            case KeyEvent.KEYCODE_DPAD_UP: return GLFW_KEY_UP;
            case KeyEvent.KEYCODE_PAGE_UP: return GLFW_KEY_PAGE_UP;
            case KeyEvent.KEYCODE_PAGE_DOWN: return GLFW_KEY_PAGE_DOWN;
            case KeyEvent.KEYCODE_MOVE_HOME: return GLFW_KEY_HOME;
            case KeyEvent.KEYCODE_MOVE_END: return GLFW_KEY_END;
            case KeyEvent.KEYCODE_CAPS_LOCK: return GLFW_KEY_CAPS_LOCK;
            case KeyEvent.KEYCODE_NUM_LOCK: return GLFW_KEY_NUM_LOCK;
            case KeyEvent.KEYCODE_SHIFT_LEFT: return GLFW_KEY_LEFT_SHIFT;
            case KeyEvent.KEYCODE_SHIFT_RIGHT: return GLFW_KEY_RIGHT_SHIFT;
            case KeyEvent.KEYCODE_CTRL_LEFT: return GLFW_KEY_LEFT_CONTROL;
            case KeyEvent.KEYCODE_CTRL_RIGHT: return GLFW_KEY_RIGHT_CONTROL;
            case KeyEvent.KEYCODE_ALT_LEFT: return GLFW_KEY_LEFT_ALT;
            case KeyEvent.KEYCODE_ALT_RIGHT: return GLFW_KEY_RIGHT_ALT;
            default: return GLFW_KEY_UNKNOWN;
        }
    }

    public static void updateModifier(int glfwKey, boolean down) {
        switch (glfwKey) {
            case GLFW_KEY_LEFT_SHIFT:
            case GLFW_KEY_RIGHT_SHIFT: CallbackBridge.holdingShift = down; break;
            case GLFW_KEY_LEFT_CONTROL:
            case GLFW_KEY_RIGHT_CONTROL: CallbackBridge.holdingCtrl = down; break;
            case GLFW_KEY_LEFT_ALT:
            case GLFW_KEY_RIGHT_ALT: CallbackBridge.holdingAlt = down; break;
            case GLFW_KEY_CAPS_LOCK: if (down) CallbackBridge.holdingCapslock = !CallbackBridge.holdingCapslock; break;
            case GLFW_KEY_NUM_LOCK: if (down) CallbackBridge.holdingNumlock = !CallbackBridge.holdingNumlock; break;
            default: break;
        }
    }
}
