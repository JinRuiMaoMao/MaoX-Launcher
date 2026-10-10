package io.github.jinruimaomao.maox;

import android.app.Service;
import android.content.Intent;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import net.kdt.pojavlaunch.utils.JREUtils;

import org.json.JSONObject;
import org.lwjgl.glfw.CallbackBridge;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;

/**
 * 在 :game 进程里不带界面地运行一次 Java 程序（Forge 安装处理器等），参数格式和 GameActivity 相同。
 * JVM 结束时整个进程退出，启动器根据进程消失和退出码文件判断结果。
 */
public class ToolService extends Service {
    private boolean mStarted;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (mStarted) return START_NOT_STICKY;
        mStarted = true;
        File configFile = intent == null ? null : new File(intent.getStringExtra(GameActivity.EXTRA_CONFIG));
        if (configFile == null || !configFile.isFile()) {
            // 进程退出后系统可能重新拉起服务：没有新任务就停掉服务再结束进程，避免被当成崩溃反复重启
            stopAndExit(0);
            return START_NOT_STICKY;
        }
        GameActivity.sExitFile = intent.getStringExtra("exitFile");
        try {
            JSONObject config = new JSONObject(new String(Files.readAllBytes(configFile.toPath()), StandardCharsets.UTF_8));
            configFile.delete();
            // 原生库加载时会用到 CallbackBridge，它的静态初始化要在主线程上做
            CallbackBridge.sContext = getApplicationContext();
            JREUtils.load();
            GameActivity.startJvmThread(getApplication(), config, new ArrayList<>(), () -> {
                stopSelf();
                try {
                    Thread.sleep(300);
                } catch (InterruptedException ignored) {
                }
            });
        } catch (Throwable e) {
            Log.e("MaoXTool", "start tool failed", e);
            GameActivity.writeExitCode(-1, false);
            stopAndExit(-1);
        }
        return START_NOT_STICKY;
    }

    private void stopAndExit(int code) {
        stopSelf();
        new Handler(Looper.getMainLooper()).postDelayed(() -> System.exit(code), 300);
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
