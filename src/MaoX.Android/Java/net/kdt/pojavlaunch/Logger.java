// 名字被 libpojavexec.so 写死（Amethyst-Android，LGPL-3.0）。
package net.kdt.pojavlaunch;

public final class Logger {
    private Logger() {
    }

    /** 把游戏的标准输出和错误输出转到这个文件（文件必须已存在）。 */
    public static native void begin(String logFilePath);

    public static native void appendToLog(String text);

    public interface eventLogListener {
        void onEventLogged(String text);
    }

    @SuppressWarnings("unused")
    private static native void setLogListener(eventLogListener logListener);
}
