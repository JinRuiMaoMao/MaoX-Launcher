package io.github.jinruimaomao.maox;

import android.app.Activity;
import android.graphics.SurfaceTexture;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.system.Os;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;

import com.oracle.dalvik.VMLauncher;

import net.kdt.pojavlaunch.Logger;
import net.kdt.pojavlaunch.utils.JREUtils;

import org.json.JSONArray;
import org.json.JSONObject;
import org.lwjgl.glfw.CallbackBridge;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

/**
 * 游戏界面，运行在单独的 :game 进程里（纯 Java，不加载 .NET）。
 * 启动器把参数、环境变量写进 JSON，这里只负责建窗口、在本进程里启动 JVM、把按键和鼠标转给游戏；触屏操作见 ControlsView。
 */
public class GameActivity extends Activity implements CallbackBridge.GrabListener {
    private static final String TAG = "MaoXGame";
    public static final String EXTRA_CONFIG = "config";

    private static String sExitFile;

    private JSONObject mConfig;
    private TextureView mTextureView;
    private float mScale = 1f;
    private boolean mStarted;
    private final Handler mHandler = new Handler(Looper.getMainLooper());
    private ControlsView mControls;

    /** 游戏非正常退出时由 ExitActivity（原生代码回调）写入退出码，启动器据此显示崩溃信息。 */
    public static void writeExitCode(int code, boolean isSignal) {
        if (sExitFile == null) return;
        try (FileOutputStream out = new FileOutputStream(sExitFile)) {
            out.write(("{\"code\":" + code + ",\"signal\":" + isSignal + "}").getBytes(StandardCharsets.UTF_8));
        } catch (Exception e) {
            Log.e(TAG, "write exit code failed", e);
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null) {
            // 游戏进程退出后系统会按任务记录重建这个界面，不能因此再启动一次游戏
            finish();
            return;
        }
        // 游戏退出时进程直接结束，先关掉这个界面，否则系统会把它当成意外退出而重新打开
        Runtime.getRuntime().addShutdownHook(new Thread(this::finish));
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        try {
            String path = getIntent().getStringExtra(EXTRA_CONFIG);
            mConfig = new JSONObject(new String(Files.readAllBytes(new File(path).toPath()), StandardCharsets.UTF_8));
            mScale = (float) mConfig.optDouble("scale", 1.0);
            sExitFile = mConfig.optString("exitFile", null);
        } catch (Exception e) {
            Log.e(TAG, "read launch config failed", e);
            finish();
            return;
        }
        CallbackBridge.sContext = getApplicationContext();
        CallbackBridge.dpiScale = mScale;

        FrameLayout root = new FrameLayout(this);
        mTextureView = new TextureView(this);
        mTextureView.setOpaque(true);
        root.addView(mTextureView, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));
        mControls = new ControlsView(this, mScale, mConfig.optString("gameDir"), mConfig.optJSONObject("labels"));
        root.addView(mControls, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));
        mControls.setOnGenericMotionListener((v, e) -> onMouse(e));
        setContentView(root);
        mControls.requestFocus();

        mTextureView.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override
            public void onSurfaceTextureAvailable(SurfaceTexture texture, int width, int height) {
                refreshSize(width, height);
                Surface surface = new Surface(texture);
                JREUtils.setupBridgeWindow(surface);
                if (!mStarted) {
                    mStarted = true;
                    startGame();
                }
            }

            @Override
            public void onSurfaceTextureSizeChanged(SurfaceTexture texture, int width, int height) {
                refreshSize(width, height);
            }

            @Override
            public boolean onSurfaceTextureDestroyed(SurfaceTexture texture) {
                return true;
            }

            @Override
            public void onSurfaceTextureUpdated(SurfaceTexture texture) {
            }
        });
        CallbackBridge.addGrabListener(this);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                    | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        CallbackBridge.nativeSetWindowAttrib(0x00020001 /* GLFW_FOCUSED */, 1);
    }

    @Override
    protected void onPause() {
        CallbackBridge.nativeSetWindowAttrib(0x00020001 /* GLFW_FOCUSED */, 0);
        super.onPause();
    }

    private void refreshSize(int width, int height) {
        CallbackBridge.physicalWidth = width;
        CallbackBridge.physicalHeight = height;
        int w = Math.max(1, Math.round(width * mScale));
        int h = Math.max(1, Math.round(height * mScale));
        CallbackBridge.windowWidth = w;
        CallbackBridge.windowHeight = h;
        SurfaceTexture texture = mTextureView.getSurfaceTexture();
        if (texture != null) texture.setDefaultBufferSize(w, h);
        CallbackBridge.sendUpdateWindowSize(w, h);
    }

    private void startGame() {
        new Thread(() -> {
            int code;
            try {
                code = runJvm();
            } catch (Throwable e) {
                Log.e(TAG, "launch failed", e);
                try {
                    Logger.appendToLog("MaoX: " + Log.getStackTraceString(e));
                } catch (Throwable ignored) {
                }
                code = -1;
            }
            if (code != 0) writeExitCode(code, false);
            System.exit(code);
        }, "JVM Main thread").start();
    }

    private int runJvm() throws Exception {
        File logFile = new File(mConfig.getString("logFile"));
        logFile.getParentFile().mkdirs();
        logFile.createNewFile();
        Logger.begin(logFile.getAbsolutePath());

        JSONObject env = mConfig.getJSONObject("env");
        for (Iterator<String> it = env.keys(); it.hasNext(); ) {
            String key = it.next();
            Os.setenv(key, env.getString(key), true);
        }
        Os.setenv("AWTSTUB_WIDTH", String.valueOf(CallbackBridge.windowWidth), true);
        Os.setenv("AWTSTUB_HEIGHT", String.valueOf(CallbackBridge.windowHeight), true);
        JREUtils.setLdLibraryPath(mConfig.getString("ldLibraryPath"));

        JSONArray preload = mConfig.getJSONArray("preload");
        for (int i = 0; i < preload.length(); i++) {
            String lib = preload.getString(i);
            if (!JREUtils.dlopen(lib)) Logger.appendToLog("MaoX: dlopen failed: " + lib);
        }

        CallbackBridge.nativeSetUseInputStackQueue(mConfig.optBoolean("inputStackQueue", true));
        JREUtils.setupExitMethod(getApplication());
        JREUtils.initializeHooks();
        JREUtils.chdir(mConfig.getString("gameDir"));

        JSONArray args = mConfig.getJSONArray("args");
        List<String> argv = new ArrayList<>();
        argv.add("java");
        argv.add("-Dglfwstub.windowWidth=" + CallbackBridge.windowWidth);
        argv.add("-Dglfwstub.windowHeight=" + CallbackBridge.windowHeight);
        for (int i = 0; i < args.length(); i++) argv.add(args.getString(i));
        Logger.appendToLog("MaoX: launching JVM with " + argv.size() + " arguments");
        return VMLauncher.launchJVM(argv.toArray(new String[0]));
    }

    @Override
    public void onGrabState(boolean isGrabbing) {
        mHandler.post(() -> mControls.setGrabbing(isGrabbing));
    }

    @Override
    public void onBackPressed() {
        CallbackBridge.sendKeyPress(Keys.GLFW_KEY_ESCAPE);
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        int code = event.getKeyCode();
        if (code == KeyEvent.KEYCODE_VOLUME_UP || code == KeyEvent.KEYCODE_VOLUME_DOWN) return super.dispatchKeyEvent(event);
        if (code == KeyEvent.KEYCODE_BACK && (event.getSource() & InputDevice.SOURCE_MOUSE) == InputDevice.SOURCE_MOUSE) {
            CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_RIGHT, event.getAction() == KeyEvent.ACTION_DOWN);
            return true;
        }
        if (code == KeyEvent.KEYCODE_BACK) return super.dispatchKeyEvent(event);
        if (event.getAction() == KeyEvent.ACTION_MULTIPLE || event.getRepeatCount() != 0) return true;
        boolean down = event.getAction() == KeyEvent.ACTION_DOWN;
        int glfw = Keys.fromAndroid(code);
        Keys.updateModifier(glfw, down);
        CallbackBridge.sendKeycode(glfw, (char) event.getUnicodeChar(), 0, CallbackBridge.getCurrentMods(), down);
        return true;
    }

    /** 外接鼠标：移动、滚轮、按键（触屏由 ControlsView 处理）。 */
    private boolean onMouse(MotionEvent e) {
        if ((e.getSource() & InputDevice.SOURCE_CLASS_POINTER) == 0) return false;
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_HOVER_MOVE:
                if (!CallbackBridge.isGrabbing()) CallbackBridge.sendCursorPos(e.getX() * mScale, e.getY() * mScale);
                return true;
            case MotionEvent.ACTION_SCROLL:
                CallbackBridge.sendScroll(e.getAxisValue(MotionEvent.AXIS_HSCROLL), e.getAxisValue(MotionEvent.AXIS_VSCROLL));
                return true;
            case MotionEvent.ACTION_BUTTON_PRESS:
            case MotionEvent.ACTION_BUTTON_RELEASE: {
                boolean down = e.getActionMasked() == MotionEvent.ACTION_BUTTON_PRESS;
                int button = e.getActionButton() == MotionEvent.BUTTON_SECONDARY ? Keys.GLFW_MOUSE_BUTTON_RIGHT
                        : e.getActionButton() == MotionEvent.BUTTON_TERTIARY ? Keys.GLFW_MOUSE_BUTTON_MIDDLE
                        : Keys.GLFW_MOUSE_BUTTON_LEFT;
                CallbackBridge.sendMouseButton(button, down);
                return true;
            }
            default:
                return false;
        }
    }
}

