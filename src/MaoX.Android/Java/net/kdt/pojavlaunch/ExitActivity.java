// 名字被 libpojavexec.so 写死（Amethyst-Android，LGPL-3.0）。游戏异常退出时由原生代码调用。
package net.kdt.pojavlaunch;

import io.github.jinruimaomao.maox.GameActivity;

import android.content.Context;

public final class ExitActivity {
    private ExitActivity() {
    }

    @SuppressWarnings("unused")
    public static void showExitMessage(Context ctx, int code, boolean isSignal) {
        GameActivity.writeExitCode(code, isSignal);
    }
}
