package io.github.jinruimaomao.maox;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.SparseArray;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.inputmethod.BaseInputConnection;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;

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
 * 菜单里：手指就是鼠标，双指上下滑动滚动。
 */
public class ControlsView extends View {
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

    // 摇杆
    private final RectF mStickZone = new RectF();
    private float mStickX, mStickY, mStickRadius;
    private float mKnobX, mKnobY;
    private boolean mUp, mDown, mLeft, mRight, mSprint;

    // 视角 / 鼠标（同一时间只有一根手指）
    private int mLookPointer = -1;
    private float mLastX, mLastY, mDownX, mDownY;
    private boolean mMoved, mHolding;
    private int mScrollPointer = -1;
    private float mScrollLastY;

    private final Runnable mLongPress = () -> {
        if (!mMoved && mLookPointer != -1 && mGrabbing) {
            mHolding = true;
            CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, true);
        }
    };

    private static final Object STICK = new Object();
    private static final Object LOOK = new Object();
    private static final Object HOTBAR = new Object();
    private static final Object NONE = new Object();

    private final class Button {
        final String label;
        final int key;
        final boolean toggle;
        final boolean inGame;
        final boolean inMenu;
        final Runnable action;
        final RectF rect = new RectF();
        boolean round;
        boolean pressed;
        boolean latched;

        Button(String label, int key, boolean toggle, boolean inGame, boolean inMenu, Runnable action) {
            this.label = label;
            this.key = key;
            this.toggle = toggle;
            this.inGame = inGame;
            this.inMenu = inMenu;
            this.action = action;
        }

        boolean visible() {
            return mGrabbing ? inGame : inMenu;
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
                CallbackBridge.sendKeyPress(key, CallbackBridge.getCurrentMods(), latched);
                return;
            }
            pressed = down;
            CallbackBridge.sendKeyPress(key, CallbackBridge.getCurrentMods(), down);
        }
    }

    private final Button mJump, mSneak, mInventory, mDrop, mChat, mView, mPause, mKeyboard;

    public ControlsView(Context context, float scale, String gameDir, JSONObject labels) {
        super(context);
        mDp = context.getResources().getDisplayMetrics().density;
        mScale = scale;
        mGameDir = gameDir;
        setFocusable(true);
        setFocusableInTouchMode(true);
        mStroke.setStyle(Paint.Style.STROKE);
        mStroke.setStrokeWidth(1.5f * mDp);
        mText.setTextAlign(Paint.Align.CENTER);
        mText.setColor(Color.WHITE);
        mText.setTextSize(13 * mDp);
        mText.setFakeBoldText(true);

        mJump = add(label(labels, "jump", "Jump"), 32, false, true, false, null);
        mJump.round = true;
        mSneak = add(label(labels, "sneak", "Sneak"), Keys.GLFW_KEY_LEFT_SHIFT, true, true, false, null);
        mSneak.round = true;
        mInventory = add(label(labels, "inventory", "Inventory"), 69, false, true, false, null);
        mDrop = add(label(labels, "drop", "Drop"), 81, false, true, false, null);
        mChat = add(label(labels, "chat", "Chat"), 0, false, true, false, () -> {
            CallbackBridge.sendKeyPress(84);
            mHandler.postDelayed(() -> showKeyboard(true), 150);
        });
        mView = add(label(labels, "view", "View"), 294, false, true, false, null);
        mPause = add(label(labels, "pause", "Pause"), Keys.GLFW_KEY_ESCAPE, false, true, false, null);
        mKeyboard = add(label(labels, "keyboard", "Keyboard"), 0, false, true, true, this::toggleKeyboard);
    }

    private static String label(JSONObject labels, String key, String fallback) {
        return labels == null ? fallback : labels.optString(key, fallback);
    }

    private Button add(String label, int key, boolean toggle, boolean inGame, boolean inMenu, Runnable action) {
        Button b = new Button(label, key, toggle, inGame, inMenu, action);
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
        box(mInventory, w - m - bw, jy - big - gap - bh - 24 * mDp, bw, bh);
        box(mDrop, w - m - bw * 2 - gap, jy - big - gap - bh - 24 * mDp, bw, bh);

        float top = m;
        box(mPause, w - m - bw, top, bw, bh);
        box(mChat, m, top, bw, bh);
        box(mView, m + bw + gap, top, bw, bh);
        box(mKeyboard, m + (bw + gap) * 2, top, bw, bh);
    }

    private static void circle(Button b, float cx, float cy, float r) {
        b.rect.set(cx - r, cy - r, cx + r, cy + r);
    }

    private static void box(Button b, float x, float y, float w, float h) {
        b.rect.set(x, y, x + w, y + h);
    }

    public void setGrabbing(boolean grabbing) {
        if (mGrabbing == grabbing) return;
        releaseAll();
        mGrabbing = grabbing;
        if (grabbing) showKeyboard(false);
        else CallbackBridge.sendCursorPos(CallbackBridge.windowWidth / 2f, CallbackBridge.windowHeight / 2f);
        invalidate();
    }

    private void releaseAll() {
        mHandler.removeCallbacks(mLongPress);
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
                b.press(true);
                return;
            }
        }
        if (mGrabbing) {
            int slot = hotbarSlot(x, y);
            if (slot >= 0) {
                mPointers.put(id, HOTBAR);
                CallbackBridge.sendKeyPress(49 + slot);
                return;
            }
            if (mStickZone.contains(x, y)) {
                mPointers.put(id, STICK);
                updateStick(x, y);
                return;
            }
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
            mHandler.postDelayed(mLongPress, LONG_PRESS_MS);
        } else {
            CallbackBridge.sendCursorPos(x * mScale, y * mScale);
            CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, true);
        }
    }

    private void pointerMove(int id, float x, float y) {
        Object target = mPointers.get(id);
        if (target == STICK) {
            updateStick(x, y);
        } else if (id == mScrollPointer) {
            float dy = y - mScrollLastY;
            if (Math.abs(dy) > 24 * mDp) {
                CallbackBridge.sendScroll(0, dy > 0 ? 1 : -1);
                mScrollLastY = y;
            }
        } else if (target == LOOK && id == mLookPointer) {
            if (Math.abs(x - mDownX) > 8 * mDp || Math.abs(y - mDownY) > 8 * mDp) mMoved = true;
            if (mGrabbing) {
                float sens = 1.2f * mScale;
                CallbackBridge.mouseX += (x - mLastX) * sens;
                CallbackBridge.mouseY += (y - mLastY) * sens;
                CallbackBridge.sendCursorPos(CallbackBridge.mouseX, CallbackBridge.mouseY);
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
            setStick(0, 0);
        } else if (id == mScrollPointer) {
            mScrollPointer = -1;
        } else if (target == LOOK && id == mLookPointer) {
            mHandler.removeCallbacks(mLongPress);
            if (mGrabbing) {
                if (mHolding) {
                    CallbackBridge.sendMouseButton(Keys.GLFW_MOUSE_BUTTON_LEFT, false);
                } else if (!mMoved && !cancel) {
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
            mFill.setColor(on ? 0x8800D9FF : 0x55000000);
            mStroke.setColor(on ? 0xDD00D9FF : 0x88FFFFFF);
            if (b.round) {
                canvas.drawCircle(b.rect.centerX(), b.rect.centerY(), b.rect.width() / 2, mFill);
                canvas.drawCircle(b.rect.centerX(), b.rect.centerY(), b.rect.width() / 2, mStroke);
            } else {
                float r = 8 * mDp;
                canvas.drawRoundRect(b.rect, r, r, mFill);
                canvas.drawRoundRect(b.rect, r, r, mStroke);
            }
            float ty = b.rect.centerY() - (mText.descent() + mText.ascent()) / 2;
            canvas.drawText(b.label, b.rect.centerX(), ty, mText);
        }
        if (mGrabbing) {
            mFill.setColor(0x33000000);
            mStroke.setColor(0x66FFFFFF);
            canvas.drawCircle(mStickX, mStickY, mStickRadius, mFill);
            canvas.drawCircle(mStickX, mStickY, mStickRadius, mStroke);
            mFill.setColor(mSprint ? 0x9900D9FF : 0x77FFFFFF);
            canvas.drawCircle(mKnobX, mKnobY, 26 * mDp, mFill);
        }
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
