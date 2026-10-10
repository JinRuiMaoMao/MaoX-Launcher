// libbytehook.so（https://github.com/bytedance/bhook ，MIT）在 JNI_OnLoad 里给这个类注册原生方法，
// 类和方法签名必须存在。初始化由 libexithook.so 在原生层完成，Java 这边不调用。
package com.bytedance.android.bytehook;

@SuppressWarnings("unused")
public final class ByteHook {
    private ByteHook() {
    }

    private static native String nativeGetVersion();
    private static native int nativeInit(int mode, boolean debug);
    private static native int nativeAddIgnore(String callerPathName);
    private static native int nativeGetMode();
    private static native boolean nativeGetDebug();
    private static native void nativeSetDebug(boolean debug);
    private static native boolean nativeGetRecordable();
    private static native void nativeSetRecordable(boolean recordable);
    private static native String nativeGetRecords(int itemFlags);
    private static native String nativeGetArch();
}
