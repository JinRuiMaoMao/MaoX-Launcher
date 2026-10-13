package io.github.jinruimaomao.maox;

import android.content.Context;
import android.hardware.input.InputManager;
import android.view.Choreographer;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;

import org.lwjgl.glfw.CallbackBridge;

import java.util.HashSet;
import java.util.Set;

/**
 * 手柄转成键盘鼠标，不需要游戏里装手柄模组（和 iOS 版 GamepadInput 的按键对应一致）。
 * 游戏中：左摇杆移动、按下疾跑，右摇杆转视角，A 跳跃、B 潜行、X 丢弃、Y 背包，LT 使用、RT 攻击，
 * LB / RB 和十字键左右切换快捷栏，按下右摇杆切换视角，开始键暂停，选择键玩家列表。
 * 菜单里：左摇杆移动光标（ControlsView 画出来），A 左键、X 右键、按住左摇杆是 Shift，右摇杆滚动，B 返回、Y 关背包。
 */
final class Gamepad implements Choreographer.FrameCallback, InputManager.InputDeviceListener {
    private static final int A = 1, B = 2, X = 4, Y = 8, LB = 16, RB = 32, LT = 64, RT = 128, L3 = 256, R3 = 512,
            START = 1024, SELECT = 2048, UP = 4096, DOWN = 8192, LEFT = 16384, RIGHT = 32768;
    private static final float STICK_DEAD_ZONE = 0.3f, LOOK_DEAD_ZONE = 0.12f;
    /** 右摇杆推到底时每秒转动的鼠标距离（游戏像素），默认灵敏度下约 165 度 */
    private static final float LOOK_SPEED = 1100, CURSOR_SPEED = 1000;

    private final ControlsView mControls;
    private final float mScale;
    private final Set<Integer> mKeys = new HashSet<>();
    private final Set<Integer> mMouse = new HashSet<>();
    private int mKeyButtons, mAxisButtons, mPrevious;
    private float mLx, mLy, mRx, mRy;
    private boolean mRunning;
    private long mLastFrame;
    private int mGrabbing = -1;
    private float mCursorX, mCursorY, mScroll;

    Gamepad(Context context, ControlsView controls, float scale) {
        mControls = controls;
        mScale = scale;
        InputManager input = (InputManager) context.getSystemService(Context.INPUT_SERVICE);
        if (input != null) input.registerInputDeviceListener(this, null);
    }

    private static boolean fromGamepad(int source) {
        return (source & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD
                || (source & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK;
    }

    /** 手柄按键返回 true（已处理），其他按键交给键盘处理。 */
    boolean onKey(KeyEvent event) {
        int code = event.getKeyCode();
        boolean dpad = code == KeyEvent.KEYCODE_DPAD_UP || code == KeyEvent.KEYCODE_DPAD_DOWN
                || code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_DPAD_RIGHT;
        if (!KeyEvent.isGamepadButton(code) && !(dpad && fromGamepad(event.getSource()))) return false;
        int flag;
        switch (code) {
            case KeyEvent.KEYCODE_BUTTON_A: flag = A; break;
            case KeyEvent.KEYCODE_BUTTON_B: flag = B; break;
            case KeyEvent.KEYCODE_BUTTON_X: flag = X; break;
            case KeyEvent.KEYCODE_BUTTON_Y: flag = Y; break;
            case KeyEvent.KEYCODE_BUTTON_L1: flag = LB; break;
            case KeyEvent.KEYCODE_BUTTON_R1: flag = RB; break;
            case KeyEvent.KEYCODE_BUTTON_L2: flag = LT; break;
            case KeyEvent.KEYCODE_BUTTON_R2: flag = RT; break;
            case KeyEvent.KEYCODE_BUTTON_THUMBL: flag = L3; break;
            case KeyEvent.KEYCODE_BUTTON_THUMBR: flag = R3; break;
            case KeyEvent.KEYCODE_BUTTON_START: flag = START; break;
            case KeyEvent.KEYCODE_BUTTON_SELECT: flag = SELECT; break;
            case KeyEvent.KEYCODE_DPAD_UP: flag = UP; break;
            case KeyEvent.KEYCODE_DPAD_DOWN: flag = DOWN; break;
            case KeyEvent.KEYCODE_DPAD_LEFT: flag = LEFT; break;
            case KeyEvent.KEYCODE_DPAD_RIGHT: flag = RIGHT; break;
            default: return true;
        }
        if (event.getAction() == KeyEvent.ACTION_DOWN) mKeyButtons |= flag;
        else if (event.getAction() == KeyEvent.ACTION_UP) mKeyButtons &= ~flag;
        activate();
        return true;
    }

    /** 摇杆、扳机、十字键（有的手柄十字键是 HAT 轴）。 */
    boolean onMotion(MotionEvent e) {
        if ((e.getSource() & InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK
                || e.getActionMasked() != MotionEvent.ACTION_MOVE) return false;
        InputDevice device = e.getDevice();
        mLx = e.getAxisValue(MotionEvent.AXIS_X);
        mLy = e.getAxisValue(MotionEvent.AXIS_Y);
        boolean z = device == null || device.getMotionRange(MotionEvent.AXIS_Z, e.getSource()) != null;
        mRx = e.getAxisValue(z ? MotionEvent.AXIS_Z : MotionEvent.AXIS_RX);
        mRy = e.getAxisValue(z ? MotionEvent.AXIS_RZ : MotionEvent.AXIS_RY);
        float lt = Math.max(e.getAxisValue(MotionEvent.AXIS_LTRIGGER), e.getAxisValue(MotionEvent.AXIS_BRAKE));
        float rt = Math.max(e.getAxisValue(MotionEvent.AXIS_RTRIGGER), e.getAxisValue(MotionEvent.AXIS_GAS));
        float hx = e.getAxisValue(MotionEvent.AXIS_HAT_X), hy = e.getAxisValue(MotionEvent.AXIS_HAT_Y);
        int b = 0;
        if (lt > 0.5f) b |= LT;
        if (rt > 0.5f) b |= RT;
        if (hx < -0.5f) b |= LEFT;
        if (hx > 0.5f) b |= RIGHT;
        if (hy < -0.5f) b |= UP;
        if (hy > 0.5f) b |= DOWN;
        mAxisButtons = b;
        activate();
        return true;
    }

    private void activate() {
        if (mRunning) return;
        mRunning = true;
        mLastFrame = 0;
        Choreographer.getInstance().postFrameCallback(this);
    }

    @Override
    public void doFrame(long frameTimeNanos) {
        float dt = mLastFrame == 0 ? 1 / 60f : Math.min(0.1f, Math.max(0, (frameTimeNanos - mLastFrame) / 1e9f));
        mLastFrame = frameTimeNanos;
        int buttons = mKeyButtons | mAxisButtons;
        boolean moving = Math.abs(mLx) > STICK_DEAD_ZONE || Math.abs(mLy) > STICK_DEAD_ZONE
                || Math.abs(mRx) > STICK_DEAD_ZONE || Math.abs(mRy) > STICK_DEAD_ZONE;
        if (buttons != 0 || moving) mControls.setGamepadActive(true);

        boolean grabbing = mControls.isGrabbing();
        if ((grabbing ? 1 : 0) != mGrabbing) {
            releaseAll();
            mGrabbing = grabbing ? 1 : 0;
            mCursorX = CallbackBridge.windowWidth / 2f;
            mCursorY = CallbackBridge.windowHeight / 2f;
            mPrevious = buttons; // 切换界面时还按着的键不算新按下
        }
        int pressed = buttons & ~mPrevious;
        mPrevious = buttons;
        if (grabbing) inGame(buttons, pressed, dt);
        else inMenu(buttons, pressed, dt);

        // 摇杆回中、按键全部松开后停下，下次有手柄输入再开始
        if (buttons == 0 && !moving && Math.abs(mRx) <= LOOK_DEAD_ZONE && Math.abs(mRy) <= LOOK_DEAD_ZONE) {
            mRunning = false;
            return;
        }
        Choreographer.getInstance().postFrameCallback(this);
    }

    private void inGame(int b, int pressed, float dt) {
        float len = (float) Math.hypot(mLx, mLy);
        boolean active = len > STICK_DEAD_ZONE;
        hold(87, active && mLy < -0.38f * len);
        hold(83, active && mLy > 0.38f * len);
        hold(65, active && mLx < -0.38f * len);
        hold(68, active && mLx > 0.38f * len);
        hold(Keys.GLFW_KEY_LEFT_CONTROL, (b & L3) != 0);
        hold(32, (b & A) != 0);
        hold(Keys.GLFW_KEY_LEFT_SHIFT, (b & B) != 0);
        hold(81, (b & (X | DOWN)) != 0);
        hold(69, (b & Y) != 0);
        hold(Keys.GLFW_KEY_TAB, (b & SELECT) != 0);
        holdMouse(Keys.GLFW_MOUSE_BUTTON_RIGHT, (b & LT) != 0);
        holdMouse(Keys.GLFW_MOUSE_BUTTON_LEFT, (b & RT) != 0);
        if ((pressed & (LB | LEFT)) != 0) CallbackBridge.sendScroll(0, 1);
        if ((pressed & (RB | RIGHT)) != 0) CallbackBridge.sendScroll(0, -1);
        if ((pressed & (R3 | UP)) != 0) CallbackBridge.sendKeyPress(294);
        if ((pressed & START) != 0) CallbackBridge.sendKeyPress(Keys.GLFW_KEY_ESCAPE);

        float dx = curve(mRx, LOOK_DEAD_ZONE) * LOOK_SPEED * dt, dy = curve(mRy, LOOK_DEAD_ZONE) * LOOK_SPEED * dt;
        if (dx != 0 || dy != 0) {
            CallbackBridge.mouseX += dx;
            CallbackBridge.mouseY += dy;
            CallbackBridge.sendCursorPos(CallbackBridge.mouseX, CallbackBridge.mouseY);
        }
    }

    private void inMenu(int b, int pressed, float dt) {
        float dx = curve(mLx, LOOK_DEAD_ZONE) * CURSOR_SPEED * dt, dy = curve(mLy, LOOK_DEAD_ZONE) * CURSOR_SPEED * dt;
        if (dx != 0 || dy != 0) {
            mCursorX = Math.max(0, Math.min(CallbackBridge.windowWidth - 1, mCursorX + dx));
            mCursorY = Math.max(0, Math.min(CallbackBridge.windowHeight - 1, mCursorY + dy));
            CallbackBridge.sendCursorPos(mCursorX, mCursorY);
        }
        mControls.setGamepadCursor(mCursorX / mScale, mCursorY / mScale);

        hold(Keys.GLFW_KEY_LEFT_SHIFT, (b & L3) != 0);
        holdMouse(Keys.GLFW_MOUSE_BUTTON_LEFT, (b & A) != 0);
        holdMouse(Keys.GLFW_MOUSE_BUTTON_RIGHT, (b & X) != 0);
        if ((pressed & (B | START)) != 0) CallbackBridge.sendKeyPress(Keys.GLFW_KEY_ESCAPE);
        if ((pressed & Y) != 0) CallbackBridge.sendKeyPress(69);
        if ((pressed & (LB | UP)) != 0) CallbackBridge.sendScroll(0, 1);
        if ((pressed & (RB | DOWN)) != 0) CallbackBridge.sendScroll(0, -1);

        mScroll -= curve(mRy, STICK_DEAD_ZONE) * 12 * dt;
        while (Math.abs(mScroll) >= 1) {
            int step = mScroll > 0 ? 1 : -1;
            CallbackBridge.sendScroll(0, step);
            mScroll -= step;
        }
    }

    /** 死区之外按平方曲线放大：轻推精细，推到底最快。 */
    private static float curve(float value, float deadZone) {
        float magnitude = Math.abs(value);
        if (magnitude <= deadZone) return 0;
        float t = Math.min(1, (magnitude - deadZone) / (1 - deadZone));
        return Math.signum(value) * t * t;
    }

    private void hold(int key, boolean down) {
        if (down == mKeys.contains(key)) return;
        if (down) mKeys.add(key);
        else mKeys.remove(key);
        CallbackBridge.sendKeyPress(key, CallbackBridge.getCurrentMods(), down);
    }

    private void holdMouse(int button, boolean down) {
        if (down == mMouse.contains(button)) return;
        if (down) mMouse.add(button);
        else mMouse.remove(button);
        CallbackBridge.sendMouseButton(button, down);
    }

    private void releaseAll() {
        for (int key : mKeys) CallbackBridge.sendKeyPress(key, CallbackBridge.getCurrentMods(), false);
        for (int button : mMouse) CallbackBridge.sendMouseButton(button, false);
        mKeys.clear();
        mMouse.clear();
        mScroll = 0;
        mControls.setGamepadCursor(-1, -1);
    }

    @Override
    public void onInputDeviceAdded(int deviceId) {
    }

    @Override
    public void onInputDeviceChanged(int deviceId) {
    }

    /** 手柄断开时松开所有按键，免得一直往前走。 */
    @Override
    public void onInputDeviceRemoved(int deviceId) {
        mKeyButtons = 0;
        mAxisButtons = 0;
        mLx = mLy = mRx = mRy = 0;
        releaseAll();
        mPrevious = 0;
    }
}
