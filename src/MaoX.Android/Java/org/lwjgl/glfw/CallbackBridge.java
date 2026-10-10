// 改编自 Amethyst-Android（https://github.com/AngelAuraMC/Amethyst-Android ，commit 330c6eae）的同名类，
// 按 LGPL-3.0 授权。类名、方法名和签名被 libpojavexec.so 写死，不能改。
package org.lwjgl.glfw;

import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Choreographer;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.ArrayList;

public class CallbackBridge {
    public static final Choreographer sChoreographer = Choreographer.getInstance();
    private static volatile boolean isGrabbing = false;
    private static final ArrayList<GrabListener> grabListeners = new ArrayList<>();

    public static final int CLIPBOARD_COPY = 2000;
    public static final int CLIPBOARD_PASTE = 2001;
    public static final int CLIPBOARD_OPEN = 2002;

    public static volatile int windowWidth, windowHeight;
    public static volatile int physicalWidth, physicalHeight;
    public static float mouseX, mouseY;
    public static volatile boolean holdingAlt, holdingCapslock, holdingCtrl, holdingNumlock, holdingShift;
    public static float dpiScale = 1f;
    public static Context sContext;

    public static final ByteBuffer sGamepadButtonBuffer;
    public static final FloatBuffer sGamepadAxisBuffer;

    public interface GrabListener {
        void onGrabState(boolean isGrabbing);
    }

    public static void sendCursorPos(float x, float y) {
        mouseX = x;
        mouseY = y;
        nativeSendCursorPos(mouseX, mouseY);
    }

    public static void sendKeycode(int keycode, char keychar, int scancode, int modifiers, boolean isDown) {
        if (keycode != 0) nativeSendKey(keycode, scancode, isDown ? 1 : 0, modifiers);
        if (isDown && keychar != 0 && !Character.isISOControl(keychar)) {
            nativeSendCharMods(keychar, modifiers);
            nativeSendChar(keychar);
        }
    }

    public static void sendKeyPress(int keyCode, int modifiers, boolean status) {
        sendKeycode(keyCode, '\u0000', 0, modifiers, status);
    }

    public static void sendKeyPress(int keyCode) {
        sendKeyPress(keyCode, getCurrentMods(), true);
        sendKeyPress(keyCode, getCurrentMods(), false);
    }

    public static void sendChar(char keychar, int modifiers) {
        nativeSendCharMods(keychar, modifiers);
        nativeSendChar(keychar);
    }

    public static void sendMouseButton(int button, boolean status) {
        nativeSendMouseButton(button, status ? 1 : 0, getCurrentMods());
    }

    public static void sendScroll(double xoffset, double yoffset) {
        nativeSendScroll(xoffset, yoffset);
    }

    public static void sendUpdateWindowSize(int w, int h) {
        nativeSendScreenSize(w, h);
    }

    public static boolean isGrabbing() {
        return isGrabbing;
    }

    public static int getCurrentMods() {
        int mods = 0;
        if (holdingShift) mods |= 0x1;
        if (holdingCtrl) mods |= 0x2;
        if (holdingAlt) mods |= 0x4;
        if (holdingCapslock) mods |= 0x10;
        if (holdingNumlock) mods |= 0x20;
        return mods;
    }

    // 以下几个方法由游戏（JVM）一侧通过 JNI 调用

    @SuppressWarnings("unused")
    public static String accessAndroidClipboard(int type, String copy) {
        if (sContext == null) return null;
        ClipboardManager clipboard = (ClipboardManager) sContext.getSystemService(Context.CLIPBOARD_SERVICE);
        switch (type) {
            case CLIPBOARD_COPY:
                clipboard.setPrimaryClip(ClipData.newPlainText("Copy", copy));
                return null;
            case CLIPBOARD_PASTE:
                if (clipboard.hasPrimaryClip() && clipboard.getPrimaryClipDescription() != null
                        && clipboard.getPrimaryClipDescription().hasMimeType(ClipDescription.MIMETYPE_TEXT_PLAIN)) {
                    CharSequence text = clipboard.getPrimaryClip().getItemAt(0).getText();
                    return text == null ? "" : text.toString();
                }
                return "";
            case CLIPBOARD_OPEN:
                try {
                    Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(copy));
                    intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    sContext.startActivity(intent);
                } catch (Exception e) {
                    Log.w("CallbackBridge", "open link failed", e);
                }
                return null;
            default:
                return null;
        }
    }

    @SuppressWarnings("unused")
    public static boolean notifyLauncher(int type, int... action) {
        return false;
    }

    @SuppressWarnings("unused")
    private static void onDirectInputEnable() {
    }

    @SuppressWarnings("unused")
    private static void onGrabStateChanged(final boolean grabbing) {
        isGrabbing = grabbing;
        sChoreographer.postFrameCallbackDelayed(time -> {
            if (isGrabbing != grabbing) return;
            synchronized (grabListeners) {
                for (GrabListener g : grabListeners) g.onGrabState(grabbing);
            }
        }, 16);
    }

    @SuppressWarnings("unused")
    private static float getAndroidDPI() {
        DisplayMetrics metrics = new DisplayMetrics();
        metrics.setToDefaults();
        return metrics.density * dpiScale;
    }

    public static void addGrabListener(GrabListener listener) {
        synchronized (grabListeners) {
            listener.onGrabState(isGrabbing);
            grabListeners.add(listener);
        }
    }

    public static void removeGrabListener(GrabListener listener) {
        synchronized (grabListeners) {
            grabListeners.remove(listener);
        }
    }

    public static native void nativeSetUseInputStackQueue(boolean useInputStackQueue);
    private static native boolean nativeSendChar(char codepoint);
    private static native boolean nativeSendCharMods(char codepoint, int mods);
    private static native void nativeSendKey(int key, int scancode, int action, int mods);
    private static native void nativeSendCursorPos(float x, float y);
    private static native void nativeSendMouseButton(int button, int action, int mods);
    private static native void nativeSendScroll(double xoffset, double yoffset);
    private static native void nativeSendScreenSize(int width, int height);
    public static native void nativeSetWindowAttrib(int attrib, int value);
    private static native ByteBuffer nativeCreateGamepadButtonBuffer();
    private static native ByteBuffer nativeCreateGamepadAxisBuffer();

    static {
        System.loadLibrary("pojavexec");
        sGamepadButtonBuffer = nativeCreateGamepadButtonBuffer();
        sGamepadAxisBuffer = nativeCreateGamepadAxisBuffer().order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer();
    }
}
