package io.github.jinruimaomao.maox;

import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.SparseArray;
import android.view.HapticFeedbackConstants;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;

import org.json.JSONArray;
import org.json.JSONObject;
import org.lwjgl.glfw.CallbackBridge;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.util.ArrayList;
import java.util.List;

/**
 * 触屏操作层，盖在游戏画面上。
 * 游戏中：左下摇杆移动（推到边缘疾跑），右侧跳跃 / 潜行等按钮，点快捷栏切换物品，
 * 其余区域拖动转视角、轻点使用 / 放置、长按攻击 / 挖掘；支持多指同时操作。
 * 菜单里：手指就是鼠标，双指上下滑动滚动；按键和摇杆像 Pojav 一样常驻，方便关背包、点 GUI。
 */
public class ControlsView extends View implements SensorEventListener {
    private static final long LONG_PRESS_MS = 300;

    private final float mDp;
    private final float mScale;
    private final String mGameDir;
    private final Handler mHandler = new Handler(Looper.getMainLooper());
    private final Paint mFill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint mStroke = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint mText = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final List<Button> mButtons = new ArrayList<>();
    private final SparseArray<Object> mPointers = new SparseArray<>();
    private boolean mGrabbing;
    private boolean mGamepadActive;
    private float mCursorX = -1, mCursorY = -1;
    private final Path mArrow = new Path();

    // 摇杆 / 方向键
    private final RectF mStickZone = new RectF();
    private final RectF mDpadRect = new RectF();
    private final JSONObject mLayout;
    private final float mOpacity;
    private final float mSensitivity;
    /** 拆分控制：拖动只转视角，攻击 / 使用用单独的按钮；方向键代替摇杆（和基岩版一样） */
    private final boolean mSplit, mDpadMode, mGyro;
    private SensorManager mSensors;
    private boolean mStickHidden;
    private float mStickX, mStickY, mStickRadius, mKnobRadius;
    private float mKnobX, mKnobY;
    private boolean mUp, mDown, mLeft, mRight, mSprint;

    // 视角 / 鼠标（同一时间只有一根手指）
    private int mLookPointer = -1;
    private float mLastX, mLastY, mDownX, mDownY;
    private boolean mMoved, mHolding;
    private int mScrollPointer = -1;
    private float mScrollLastY;

    // 快捷栏：滑动换格，长按丢弃
    private int mHotbarPointer = -1;
    private int mHotbarSlot = -1;

    private final Runnable mLongPress = () -> {
        if (!mMoved && mLookPointer != -1 && mGrabbing) {
            mHolding = true;
            performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
            CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, true);
        }
    };

    private final Runnable mHotbarHold = () -> {
        if (mGrabbing && mHotbarPointer != -1 && mPointers.get(mHotbarPointer) == HOTBAR) {
            CallbackBridge.sendKeyPress(81);
            performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
        }
    };

    private static final Object STICK = new Object();
    private static final Object LOOK = new Object();
    private static final Object HOTBAR = new Object();
    private static final Object NONE = new Object();

    // 自定义按键的特殊键码（和启动器 ControlKeys、Pojav 一致）
    private static final int KEYBOARD = -1, MOUSE_LEFT = -3, MOUSE_RIGHT = -4, MOUSE_MIDDLE = -6, SCROLL_UP = -7,
            SCROLL_DOWN = -8;

    private final class Button {
        final String id;
        final String label;
        final int[] keys;
        final boolean toggle;
        final boolean inGame;
        final boolean inMenu;
        final Runnable action;
        final RectF rect = new RectF();
        boolean round;
        boolean pressed;
        boolean latched;
        boolean hidden;
        boolean disabled;
        /** 按住时手指拖动也转视角（攻击 / 使用按钮） */
        boolean look;
        float lastX, lastY;
        float size = 1;
        /** 用户添加的按键（null 是内置按键） */
        JSONObject custom;

        Button(String id, String label, int[] keys, boolean toggle, boolean inGame, boolean inMenu, Runnable action) {
            this.id = id;
            this.label = label;
            this.keys = keys;
            this.toggle = toggle;
            this.inGame = inGame;
            this.inMenu = inMenu;
            this.action = action;
        }

        boolean visible() {
            return !hidden && !disabled && !mGamepadActive && (mGrabbing ? inGame : inMenu);
        }

        void press(boolean down) {
            if (action != null) {
                if (down) action.run();
                pressed = down;
                return;
            }
            if (toggle) {
                if (!down) return;
                latched = !latched;
                pressed = latched;
                send(latched);
                return;
            }
            pressed = down;
            send(down);
        }

        private void send(boolean down) {
            for (int key : keys) sendCode(key, down);
        }
    }

    /** 按下 / 松开一个键：GLFW 键码，或鼠标、滚轮、打开键盘这些特殊键码。 */
    private void sendCode(int code, boolean down) {
        if (code >= 0) {
            CallbackBridge.sendKeyPress(code, CallbackBridge.getCurrentMods(), down);
            return;
        }
        switch (code) {
            case MOUSE_LEFT: CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, down); break;
            case MOUSE_RIGHT: CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_RIGHT, down); break;
            case MOUSE_MIDDLE: CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_MIDDLE, down); break;
            case SCROLL_UP: if (down) CallbackBridge.sendScroll(0, 1); break;
            case SCROLL_DOWN: if (down) CallbackBridge.sendScroll(0, -1); break;
            case KEYBOARD: if (down) toggleKeyboard(); break;
            default: break;
        }
    }

    private final Button mJump, mSneak, mInventory, mDrop, mChat, mView, mPause, mKeyboard, mAttack, mUse;

    public ControlsView(Context context, float scale, String gameDir, JSONObject labels, JSONObject layout) {
        super(context);
        mDp = context.getResources().getDisplayMetrics().density;
        mScale = scale;
        mGameDir = gameDir;
        mLayout = layout;
        mOpacity = layout == null ? 1f : (float) Math.max(0.2, Math.min(1, layout.optDouble("opacity", 1)));
        mSensitivity = layout == null ? 1f : (float) Math.max(0.3, Math.min(3, layout.optDouble("sensitivity", 1)));
        mSplit = layout != null && "split".equals(layout.optString("mode"));
        mDpadMode = layout != null && "dpad".equals(layout.optString("movement"));
        mGyro = layout != null && layout.optBoolean("gyro");
        setFocusable(true);
        setFocusableInTouchMode(true);
        mStroke.setStyle(Paint.Style.STROKE);
        mStroke.setStrokeWidth(1.5f * mDp);
        mText.setTextAlign(Paint.Align.CENTER);
        mText.setColor(Color.WHITE);
        mText.setTextSize(13 * mDp);
        mText.setFakeBoldText(true);

        // 内置键和摇杆全局常驻（和 Pojav 一样）：打开背包后指针会松开，不能把操作层藏掉
        mJump = add(labels, "jump", "Jump", 32, false, true, true, null);
        mJump.round = true;
        mSneak = add(labels, "sneak", "Sneak", Keys.GLFW_KEY_LEFT_SHIFT, true, true, true, null);
        mSneak.round = true;
        mInventory = add(labels, "inventory", "Inventory", 69, false, true, true, null);
        mDrop = add(labels, "drop", "Drop", 81, false, true, true, null);
        mChat = add(labels, "chat", "Chat", 0, false, true, true, () -> {
            if (mGrabbing) CallbackBridge.sendKeyPress(84);
            mHandler.postDelayed(() -> showKeyboard(true), 150);
        });
        mView = add(labels, "view", "View", 294, false, true, true, null);
        mPause = add(labels, "pause", "Pause", Keys.GLFW_KEY_ESCAPE, false, true, true, null);
        mKeyboard = add(labels, "keyboard", "Keyboard", 0, false, true, true, this::toggleKeyboard);
        // 拆分控制的攻击 / 使用按钮：按住的同时拖动可以继续转视角
        mAttack = add(labels, "attack", "Attack", MOUSE_LEFT, false, true, true, null);
        mUse = add(labels, "use", "Use", MOUSE_RIGHT, false, true, true, null);
        for (Button b : new Button[] {mAttack, mUse}) {
            b.round = true;
            b.look = true;
            b.disabled = !mSplit;
        }

        JSONArray custom = layout == null ? null : layout.optJSONArray("buttons");
        for (int i = 0; custom != null && i < custom.length(); i++) {
            JSONObject c = custom.optJSONObject(i);
            if (c == null) continue;
            JSONArray codes = c.optJSONArray("keys");
            int[] keys = new int[codes == null ? 0 : codes.length()];
            for (int k = 0; k < keys.length; k++) keys[k] = codes.optInt(k);
            Button b = new Button("custom" + i, c.optString("label", "?"), keys, c.optBoolean("toggle", false),
                    c.optBoolean("inGame", true), c.optBoolean("inMenu", true), null);
            b.round = c.optBoolean("round", false);
            b.custom = c;
            mButtons.add(b);
        }
    }

    private Button add(JSONObject labels, String id, String fallback, int key, boolean toggle, boolean inGame,
                       boolean inMenu, Runnable action) {
        String label = labels == null ? fallback : labels.optString(id, fallback);
        Button b = new Button(id, label, new int[] {key}, toggle, inGame, inMenu, action);
        mButtons.add(b);
        return b;
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        float m = 16 * mDp;
        float big = 34 * mDp;
        float small = 26 * mDp;
        mStickRadius = 62 * mDp;
        mStickX = m + mStickRadius + 24 * mDp;
        mStickY = h - m - mStickRadius - 16 * mDp;
        mKnobX = mStickX;
        mKnobY = mStickY;
        mStickZone.set(0, h * 0.4f, w * 0.3f, h);

        float jx = w - m - big - 20 * mDp, jy = h - m - big - 24 * mDp;
        circle(mJump, jx, jy, big);
        circle(mSneak, jx - big - small - 22 * mDp, jy + big - small, small);

        float bw = 76 * mDp, bh = 34 * mDp, gap = 8 * mDp;
        float rowTop = jy - big - gap - bh - 24 * mDp;
        box(mInventory, w - m - bw, rowTop, bw, bh);
        box(mDrop, w - m - bw * 2 - gap, rowTop, bw, bh);
        // 和 ControlLayout.DefaultCenter 一致
        float ar = 32 * mDp, ax = w - m - 36 * mDp, ay = rowTop - 14 * mDp - ar;
        circle(mAttack, ax, ay, ar);
        circle(mUse, ax - 78 * mDp, ay + 12 * mDp, ar);

        float top = m;
        box(mPause, w - m - bw, top, bw, bh);
        box(mChat, m, top, bw, bh);
        box(mView, m + bw + gap, top, bw, bh);
        box(mKeyboard, m + (bw + gap) * 2, top, bw, bh);
        applyLayout(w, h);
        mDpadRect.set(mStickX - mStickRadius, mStickY - mStickRadius, mStickX + mStickRadius, mStickY + mStickRadius);
        if (mDpadMode) {
            if (mStickHidden) mStickZone.setEmpty();
            else mStickZone.set(mDpadRect);
        }
    }

    /** 启动器里编辑过的布局：位置是中心点占屏幕宽高的比例，大小是默认大小的倍数。 */
    private void applyLayout(int w, int h) {
        mKnobRadius = 26 * mDp;
        for (Button b : mButtons) {
            if (b.custom == null) continue;
            float bw = side(b.custom, "w") * mDp, bh = side(b.custom, "h") * mDp;
            float cx = (float) b.custom.optDouble("x", 0.5) * w, cy = (float) b.custom.optDouble("y", 0.5) * h;
            b.rect.set(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2);
            b.size = Math.min(bw, bh) / mDp / 44;
        }
        JSONObject items = mLayout == null ? null : mLayout.optJSONObject("items");
        if (items == null) return;
        for (Button b : mButtons) {
            if (b.custom != null) continue;
            JSONObject item = items.optJSONObject(b.id);
            if (item == null) continue;
            b.size = layoutSize(item);
            b.hidden = item.optBoolean("hidden", false);
            float cx = (float) item.optDouble("x", 0.5) * w, cy = (float) item.optDouble("y", 0.5) * h;
            float hw = b.rect.width() / 2 * b.size, hh = b.rect.height() / 2 * b.size;
            b.rect.set(cx - hw, cy - hh, cx + hw, cy + hh);
        }
        JSONObject stick = items.optJSONObject("stick");
        if (stick == null) return;
        float size = layoutSize(stick);
        mStickRadius *= size;
        mKnobRadius *= size;
        mStickX = (float) stick.optDouble("x", 0.5) * w;
        mStickY = (float) stick.optDouble("y", 0.5) * h;
        mKnobX = mStickX;
        mKnobY = mStickY;
        mStickHidden = stick.optBoolean("hidden", false);
        float reach = mStickRadius * 1.8f;
        if (mStickHidden) mStickZone.setEmpty();
        else mStickZone.set(mStickX - reach, mStickY - reach, mStickX + reach, mStickY + reach);
    }

    private static float layoutSize(JSONObject item) {
        return (float) Math.max(0.5, Math.min(2, item.optDouble("size", 1)));
    }

    /** 自定义按键的宽 / 高（dp），范围和启动器 ControlButton 一致。 */
    private static float side(JSONObject button, String key) {
        return (float) Math.max(24, Math.min(200, button.optDouble(key, 56)));
    }

    /** 按布局的不透明度调整颜色的透明度。 */
    private int fade(int color) {
        return ((int) ((color >>> 24) * mOpacity) << 24) | (color & 0xFFFFFF);
    }

    private static void circle(Button b, float cx, float cy, float r) {
        b.rect.set(cx - r, cy - r, cx + r, cy + r);
    }

    private static void box(Button b, float x, float y, float w, float h) {
        b.rect.set(x, y, x + w, y + h);
    }

    public boolean isGrabbing() {
        return mGrabbing;
    }

    /** 正在用手柄时隐藏触屏按键，手指碰到屏幕就重新显示。 */
    public void setGamepadActive(boolean active) {
        if (mGamepadActive == active) return;
        mGamepadActive = active;
        invalidate();
    }

    /** 菜单里手柄光标的位置（界面像素），负数表示不画。 */
    public void setGamepadCursor(float x, float y) {
        if (mCursorX == x && mCursorY == y) return;
        mCursorX = x;
        mCursorY = y;
        invalidate();
    }

    public void setGrabbing(boolean grabbing) {
        if (mGrabbing == grabbing) return;
        releaseAll();
        mGrabbing = grabbing;
        mCursorX = mCursorY = -1;
        if (grabbing) showKeyboard(false);
        else CallbackBridge.sendCursorPos(CallbackBridge.windowWidth / 2f, CallbackBridge.windowHeight / 2f);
        invalidate();
    }

    private void releaseAll() {
        mHandler.removeCallbacks(mLongPress);
        mHandler.removeCallbacks(mHotbarHold);
        mHotbarPointer = -1;
        if (mHolding) CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, false);
        mHolding = false;
        mLookPointer = -1;
        mScrollPointer = -1;
        setStick(0, 0);
        for (Button b : mButtons) {
            if (b.pressed && !b.toggle) b.press(false);
            b.pressed = b.latched;
        }
        mPointers.clear();
    }

    // ------------------------------------------------------------------ 触摸

    @Override
    public boolean onTouchEvent(MotionEvent e) {
        int action = e.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) setGamepadActive(false);
        switch (action) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN: {
                int i = e.getActionIndex();
                pointerDown(e.getPointerId(i), e.getX(i), e.getY(i));
                break;
            }
            case MotionEvent.ACTION_MOVE:
                for (int i = 0; i < e.getPointerCount(); i++) pointerMove(e.getPointerId(i), e.getX(i), e.getY(i));
                break;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP: {
                int i = e.getActionIndex();
                pointerUp(e.getPointerId(i), e.getX(i), e.getY(i), false);
                break;
            }
            case MotionEvent.ACTION_CANCEL:
                for (int i = 0; i < e.getPointerCount(); i++) pointerUp(e.getPointerId(i), e.getX(i), e.getY(i), true);
                break;
            default:
                return false;
        }
        invalidate();
        return true;
    }

    private void pointerDown(int id, float x, float y) {
        for (Button b : mButtons) {
            if (b.visible() && b.rect.contains(x, y)) {
                mPointers.put(id, b);
                b.lastX = x;
                b.lastY = y;
                b.press(true);
                return;
            }
        }
        if (mGrabbing) {
            int slot = hotbarSlot(x, y);
            if (slot >= 0) {
                mPointers.put(id, HOTBAR);
                mHotbarPointer = id;
                selectSlot(slot);
                return;
            }
        }
        if (!mStickHidden && mStickZone.contains(x, y)) {
            mPointers.put(id, STICK);
            if (mDpadMode) updateDpad(x, y, true);
            else updateStick(x, y);
            return;
        }
        if (mLookPointer != -1) {
            // 菜单里第二根手指：上下滑动滚动
            if (!mGrabbing && mScrollPointer == -1) {
                CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, false);
                mScrollPointer = id;
                mScrollLastY = y;
                mPointers.put(id, NONE);
                return;
            }
            mPointers.put(id, NONE);
            return;
        }
        mPointers.put(id, LOOK);
        mLookPointer = id;
        mDownX = mLastX = x;
        mDownY = mLastY = y;
        mMoved = false;
        mHolding = false;
        if (mGrabbing) {
            if (!mSplit) mHandler.postDelayed(mLongPress, LONG_PRESS_MS);
        } else {
            CallbackBridge.sendCursorPos(x * mScale, y * mScale);
            CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, true);
        }
    }

    private void pointerMove(int id, float x, float y) {
        Object target = mPointers.get(id);
        if (target == STICK) {
            if (mDpadMode) updateDpad(x, y, false);
            else updateStick(x, y);
        } else if (target == HOTBAR) {
            int slot = hotbarSlot(x, y);
            if (slot >= 0 && slot != mHotbarSlot) selectSlot(slot);
        } else if (target instanceof Button && ((Button) target).look) {
            Button b = (Button) target;
            if (mGrabbing) turn(x - b.lastX, y - b.lastY);
            b.lastX = x;
            b.lastY = y;
        } else if (id == mScrollPointer) {
            float dy = y - mScrollLastY;
            if (Math.abs(dy) > 24 * mDp) {
                CallbackBridge.sendScroll(0, dy > 0 ? 1 : -1);
                mScrollLastY = y;
            }
        } else if (target == LOOK && id == mLookPointer) {
            if (Math.abs(x - mDownX) > 8 * mDp || Math.abs(y - mDownY) > 8 * mDp) mMoved = true;
            if (mGrabbing) {
                turn(x - mLastX, y - mLastY);
            } else {
                CallbackBridge.sendCursorPos(x * mScale, y * mScale);
            }
            mLastX = x;
            mLastY = y;
        }
    }

    private void pointerUp(int id, float x, float y, boolean cancel) {
        Object target = mPointers.get(id);
        mPointers.remove(id);
        if (target instanceof Button) {
            ((Button) target).press(false);
        } else if (target == STICK) {
            if (mDpadMode) {
                setKey(87, false, 0);
                setKey(83, false, 1);
                setKey(65, false, 2);
                setKey(68, false, 3);
                setKey(Keys.GLFW_KEY_LEFT_CONTROL, false, 4);
            } else {
                setStick(0, 0);
            }
        } else if (target == HOTBAR) {
            mHandler.removeCallbacks(mHotbarHold);
            mHotbarPointer = -1;
        } else if (id == mScrollPointer) {
            mScrollPointer = -1;
        } else if (target == LOOK && id == mLookPointer) {
            mHandler.removeCallbacks(mLongPress);
            if (mGrabbing) {
                if (mHolding) {
                    CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, false);
                } else if (!mMoved && !cancel && !mSplit) {
                    CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_RIGHT, true);
                    mHandler.postDelayed(() -> CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_RIGHT, false), 50);
                }
            } else {
                CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, false);
            }
            mHolding = false;
            mLookPointer = -1;
        }
    }

    private void updateStick(float x, float y) {
        float dx = (x - mStickX) / mStickRadius;
        float dy = (y - mStickY) / mStickRadius;
        float len = (float) Math.hypot(dx, dy);
        setStick(dx, dy);
        float clamp = Math.min(1f, len) / Math.max(len, 0.0001f);
        mKnobX = mStickX + dx * clamp * mStickRadius;
        mKnobY = mStickY + dy * clamp * mStickRadius;
    }

    /**
     * 基岩版方向键：3×3 格，上下左右是 W / S / A / D，中间切换潜行；按住前进时左上、右上两格变成斜向前进。
     * 手指可以在格子之间滑动；双击前进疾跑由游戏本身处理。
     */
    private void updateDpad(float x, float y, boolean begin) {
        float cell = mDpadRect.width() / 3;
        int col = (int) Math.floor((x - mDpadRect.left) / cell), row = (int) Math.floor((y - mDpadRect.top) / cell);
        boolean inside = col >= 0 && col <= 2 && row >= 0 && row <= 2;
        if (inside && begin && col == 1 && row == 1) mSneak.press(true);
        boolean forward = inside && row == 0 && (col == 1 || mUp);
        setKey(87, forward, 0);
        setKey(83, inside && row == 2 && col == 1, 1);
        setKey(65, inside && col == 0 && (row == 1 || forward), 2);
        setKey(68, inside && col == 2 && (row == 1 || forward), 3);
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (!mGyro)
            return;
        mSensors = (SensorManager) getContext().getSystemService(Context.SENSOR_SERVICE);
        Sensor gyro = mSensors == null ? null : mSensors.getDefaultSensor(Sensor.TYPE_GYROSCOPE);
        if (gyro != null)
            mSensors.registerListener(this, gyro, SensorManager.SENSOR_DELAY_GAME);
    }

    @Override
    protected void onDetachedFromWindow() {
        if (mSensors != null)
            mSensors.unregisterListener(this);
        super.onDetachedFromWindow();
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        if (!mGrabbing || event.sensor.getType() != Sensor.TYPE_GYROSCOPE)
            return;
        turn(event.values[1] * 12f, event.values[0] * 12f);
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy) {
    }

    /** 手指在界面上移动 (dx, dy) 像素时转动视角。 */
    private void turn(float dx, float dy) {
        float sens = 1.2f * mScale * mSensitivity;
        CallbackBridge.mouseX += dx * sens;
        CallbackBridge.mouseY += dy * sens;
        CallbackBridge.sendCursorPos(CallbackBridge.mouseX, CallbackBridge.mouseY);
    }

    /** 切换快捷栏格子；按住不放 0.5 秒丢出一个物品（和基岩版一样）。 */
    private void selectSlot(int slot) {
        mHotbarSlot = slot;
        CallbackBridge.sendKeyPress(49 + slot);
        performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
        mHandler.removeCallbacks(mHotbarHold);
        mHandler.postDelayed(mHotbarHold, 500);
    }

    private void setStick(float dx, float dy) {
        float len = (float) Math.hypot(dx, dy);
        boolean active = len > 0.25f;
        setKey(87, active && dy < -0.38f * len, 0);
        setKey(83, active && dy > 0.38f * len, 1);
        setKey(65, active && dx < -0.38f * len, 2);
        setKey(68, active && dx > 0.38f * len, 3);
        setKey(Keys.GLFW_KEY_LEFT_CONTROL, mUp && len > 1.15f, 4);
        if (!active) {
            mKnobX = mStickX;
            mKnobY = mStickY;
        }
    }

    private void setKey(int key, boolean down, int which) {
        boolean old;
        switch (which) {
            case 0: old = mUp; mUp = down; break;
            case 1: old = mDown; mDown = down; break;
            case 2: old = mLeft; mLeft = down; break;
            case 3: old = mRight; mRight = down; break;
            default: old = mSprint; mSprint = down; break;
        }
        if (old != down) CallbackBridge.sendKeyPress(key, CallbackBridge.getCurrentMods(), down);
    }

    /** 快捷栏宽 182、高 22 个界面像素，居中贴底；界面缩放按 options.txt 的 guiScale（0 为自动）计算。 */
    private int hotbarSlot(float x, float y) {
        int w = CallbackBridge.windowWidth, h = CallbackBridge.windowHeight;
        int gui = guiScale(w, h);
        float px = x * mScale, py = y * mScale;
        float left = (w - 182f * gui) / 2f;
        if (py < h - 22f * gui || px < left || px > left + 182f * gui) return -1;
        return Math.max(0, Math.min(8, (int) ((px - left) / (20f * gui))));
    }

    private int mGuiScaleSetting = -1;
    private long mOptionsModified = -1;

    private int guiScale(int w, int h) {
        File options = new File(mGameDir, "options.txt");
        if (options.lastModified() != mOptionsModified) {
            mOptionsModified = options.lastModified();
            mGuiScaleSetting = 0;
            try (BufferedReader reader = new BufferedReader(new FileReader(options))) {
                String line;
                while ((line = reader.readLine()) != null) {
                    if (line.startsWith("guiScale:")) mGuiScaleSetting = Integer.parseInt(line.substring(9).trim());
                }
            } catch (Exception ignored) {
            }
        }
        int scale = 1;
        int max = mGuiScaleSetting > 0 ? mGuiScaleSetting : Integer.MAX_VALUE;
        while (scale < max && w / (scale + 1) >= 320 && h / (scale + 1) >= 240) scale++;
        return scale;
    }

    // ------------------------------------------------------------------ 绘制

    @Override
    protected void onDraw(Canvas canvas) {
        for (Button b : mButtons) {
            if (!b.visible()) continue;
            boolean on = b.pressed || b.latched;
            mFill.setColor(fade(on ? 0x8800D9FF : 0x55000000));
            mStroke.setColor(fade(on ? 0xDD00D9FF : 0x88FFFFFF));
            mText.setColor(fade(Color.WHITE));
            float textSize = 13 * mDp * Math.max(0.7f, Math.min(1.4f, b.size));
            mText.setTextSize(textSize);
            float textWidth = mText.measureText(b.label), room = b.rect.width() - 6 * mDp;
            if (textWidth > room) mText.setTextSize(Math.max(7 * mDp, textSize * room / textWidth));
            float r = b.round ? Math.min(b.rect.width(), b.rect.height()) / 2 : 8 * mDp;
            canvas.drawRoundRect(b.rect, r, r, mFill);
            canvas.drawRoundRect(b.rect, r, r, mStroke);
            float ty = b.rect.centerY() - (mText.descent() + mText.ascent()) / 2;
            canvas.drawText(b.label, b.rect.centerX(), ty, mText);
        }
        if (!mGrabbing && mGamepadActive && mCursorX >= 0) {
            float x = mCursorX, y = mCursorY, u = mDp;
            mArrow.reset();
            mArrow.moveTo(x, y);
            mArrow.lineTo(x, y + 20 * u);
            mArrow.lineTo(x + 5 * u, y + 15 * u);
            mArrow.lineTo(x + 9 * u, y + 23 * u);
            mArrow.lineTo(x + 12 * u, y + 21.5f * u);
            mArrow.lineTo(x + 8 * u, y + 13.5f * u);
            mArrow.lineTo(x + 14 * u, y + 13.5f * u);
            mArrow.close();
            mFill.setColor(Color.WHITE);
            canvas.drawPath(mArrow, mFill);
            mStroke.setColor(Color.BLACK);
            canvas.drawPath(mArrow, mStroke);
        }
        if (!mGamepadActive && !mStickHidden && mDpadMode) {
            drawDpad(canvas);
        } else if (!mGamepadActive && !mStickHidden) {
            mFill.setColor(fade(0x33000000));
            mStroke.setColor(fade(0x66FFFFFF));
            canvas.drawCircle(mStickX, mStickY, mStickRadius, mFill);
            canvas.drawCircle(mStickX, mStickY, mStickRadius, mStroke);
            mFill.setColor(fade(mSprint ? 0x9900D9FF : 0x77FFFFFF));
            canvas.drawCircle(mKnobX, mKnobY, mKnobRadius, mFill);
        }
    }

    private final RectF mCell = new RectF();

    private void drawDpad(Canvas canvas) {
        float cell = mDpadRect.width() / 3;
        drawCell(canvas, cell, 1, 0, mUp && !mLeft && !mRight, 0);
        drawCell(canvas, cell, 0, 1, mLeft && !mUp, -90);
        drawCell(canvas, cell, 2, 1, mRight && !mUp, 90);
        drawCell(canvas, cell, 1, 2, mDown, 180);
        if (mUp) {
            drawCell(canvas, cell, 0, 0, mLeft, -45);
            drawCell(canvas, cell, 2, 0, mRight, 45);
        }
        boolean sneak = mSneak.latched;
        mFill.setColor(fade(sneak ? 0x8800D9FF : 0x55000000));
        mStroke.setColor(fade(sneak ? 0xDD00D9FF : 0x88FFFFFF));
        canvas.drawCircle(mDpadRect.centerX(), mDpadRect.centerY(), cell * 0.32f, mFill);
        canvas.drawCircle(mDpadRect.centerX(), mDpadRect.centerY(), cell * 0.32f, mStroke);
    }

    /** 方向键的一格，angle 是箭头方向（0 是上，顺时针，单位度）。 */
    private void drawCell(Canvas canvas, float cell, int col, int row, boolean on, float angle) {
        float inset = 2 * mDp;
        mCell.set(mDpadRect.left + col * cell + inset, mDpadRect.top + row * cell + inset,
                mDpadRect.left + (col + 1) * cell - inset, mDpadRect.top + (row + 1) * cell - inset);
        float r = 8 * mDp;
        mFill.setColor(fade(on ? 0x8800D9FF : 0x55000000));
        mStroke.setColor(fade(on ? 0xDD00D9FF : 0x88FFFFFF));
        canvas.drawRoundRect(mCell, r, r, mFill);
        canvas.drawRoundRect(mCell, r, r, mStroke);
        float cx = mCell.centerX(), cy = mCell.centerY(), s = cell * 0.18f;
        mArrow.reset();
        for (int i = 0; i < 3; i++) {
            double a = Math.toRadians(angle) + i * 2 * Math.PI / 3;
            float px = cx + (float) Math.sin(a) * s, py = cy - (float) Math.cos(a) * s;
            if (i == 0) mArrow.moveTo(px, py);
            else mArrow.lineTo(px, py);
        }
        mArrow.close();
        mFill.setColor(fade(Color.WHITE));
        canvas.drawPath(mArrow, mFill);
    }

    // ------------------------------------------------------------------ 输入法

    private boolean mKeyboardShown;

    private void toggleKeyboard() {
        showKeyboard(!mKeyboardShown);
    }

    public void showKeyboard(boolean show) {
        InputMethodManager imm = (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
        if (imm == null) return;
        if (mKeyboardShown == show) return;
        mKeyboardShown = show;
        requestFocus();
        imm.restartInput(this);
        if (show) imm.showSoftInput(this, InputMethodManager.SHOW_FORCED);
        else imm.hideSoftInputFromWindow(getWindowToken(), 0);
    }

    // 只在打开软键盘时接输入法，否则外接键盘的按键会先被输入法（比如拼音）吃掉
    @Override
    public boolean onCheckIsTextEditor() {
        return mKeyboardShown;
    }

    /** 输入法提交的文字逐字发给游戏；删除、回车转成对应按键。 */
    @Override
    public InputConnection onCreateInputConnection(EditorInfo outAttrs) {
        if (!mKeyboardShown) return null;
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT;
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_FULLSCREEN | EditorInfo.IME_FLAG_NO_EXTRACT_UI
                | EditorInfo.IME_ACTION_DONE;
        return new BaseInputConnection(this, false) {
            @Override
            public boolean commitText(CharSequence text, int newCursorPosition) {
                for (int i = 0; i < text.length(); i++) {
                    char c = text.charAt(i);
                    if (c == '\n') CallbackBridge.sendKeyPress(Keys.GLFW_KEY_ENTER);
                    else CallbackBridge.sendChar(c, CallbackBridge.getCurrentMods());
                }
                return true;
            }

            @Override
            public boolean deleteSurroundingText(int beforeLength, int afterLength) {
                for (int i = 0; i < Math.max(1, beforeLength); i++) CallbackBridge.sendKeyPress(Keys.GLFW_KEY_BACKSPACE);
                return true;
            }

            @Override
            public boolean performEditorAction(int actionCode) {
                CallbackBridge.sendKeyPress(Keys.GLFW_KEY_ENTER);
                return true;
            }

            @Override
            public boolean sendKeyEvent(KeyEvent event) {
                int code = Keys.fromAndroid(event.getKeyCode());
                if (code == 0) return super.sendKeyEvent(event);
                CallbackBridge.sendKeyPress(code, CallbackBridge.getCurrentMods(), event.getAction() == KeyEvent.ACTION_DOWN);
                return true;
            }
        };
    }
}
