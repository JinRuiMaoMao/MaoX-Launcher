// 名字被 libpojavexec.so / libexithook.so 写死（Amethyst-Android，LGPL-3.0）。
package net.kdt.pojavlaunch.utils;

import android.content.Context;

public final class JREUtils {
    private JREUtils() {
    }

    public static native int chdir(String path);
    public static native boolean dlopen(String libPath);
    public static native void setLdLibraryPath(String ldLibraryPath);
    public static native void setupBridgeWindow(Object surface);
    public static native void releaseBridgeWindow();
    public static native void initializeHooks();
    public static native void setupExitMethod(Context context);

    static {
        System.loadLibrary("exithook");
        System.loadLibrary("pojavexec");
    }
}
