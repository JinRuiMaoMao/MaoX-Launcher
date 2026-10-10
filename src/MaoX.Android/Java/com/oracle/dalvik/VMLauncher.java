// 名字被 libpojavexec.so 写死（Amethyst-Android，LGPL-3.0）。
package com.oracle.dalvik;

public final class VMLauncher {
    private VMLauncher() {
    }

    public static native int launchJVM(String[] args);
}
