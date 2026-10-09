import tkinter as tk
from tkinter import ttk

from . import theme as T


def round_rect(canvas, x1, y1, x2, y2, r, **kw):
    r = max(0, min(r, (x2 - x1) / 2, (y2 - y1) / 2))
    points = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
              x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
    return canvas.create_polygon(points, smooth=True, **kw)


# ---------------------------------------------------------------------- 标志

def logo_polygons(x, y, size):
    """M 与 X 融合：左右两根竖线 + 贯穿的 X。上半部分读作 M，整体是 X。"""
    s = size
    t = s * 0.2
    left, right, top, bottom = x, x + s, y, y + s
    bars = [
        [left, top, left + t, top, left + t, bottom, left, bottom],
        [right - t, top, right, top, right, bottom, right - t, bottom],
    ]
    cross = [
        [left, top, left + t * 1.15, top, right, bottom, right - t * 1.15, bottom],
        [right - t * 1.15, top, right, top, left + t * 1.15, bottom, left, bottom],
    ]
    return bars, cross


def draw_logo(canvas, x, y, size, bar_color=T.TEXT, cross_color=T.ACCENT):
    bars, cross = logo_polygons(x, y, size)
    for p in bars:
        canvas.create_polygon(p, fill=bar_color, outline="")
    for p in cross:
        canvas.create_polygon(p, fill=cross_color, outline="")


def _inside(poly, px, py):
    n = len(poly) // 2
    sign = 0
    for i in range(n):
        x1, y1 = poly[2 * i], poly[2 * i + 1]
        x2, y2 = poly[(2 * i + 2) % (2 * n)], poly[(2 * i + 3) % (2 * n)]
        cross = (x2 - x1) * (py - y1) - (y2 - y1) * (px - x1)
        if cross != 0:
            s = 1 if cross > 0 else -1
            if sign and s != sign:
                return False
            sign = s
    return True


def make_icon(size=48):
    """光栅化标志作为窗口图标（圆角深色底，3x3 超采样抗锯齿）。"""
    def rgb(h):
        return int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16)

    bg, bar, cross = rgb(T.BG), rgb(T.TEXT), rgb(T.ACCENT)
    pad = size * 0.22
    bars, crosses = logo_polygons(pad, pad, size - 2 * pad)
    layers = [(p, bar) for p in bars] + [(p, cross) for p in crosses]
    radius = size * 0.22
    img = tk.PhotoImage(width=size, height=size)
    rows, transparent = [], []
    offsets = (1 / 6, 1 / 2, 5 / 6)
    for py in range(size):
        row = []
        for px in range(size):
            cx = min(max(px + 0.5, radius), size - radius)
            cy = min(max(py + 0.5, radius), size - radius)
            if (px + 0.5 - cx) ** 2 + (py + 0.5 - cy) ** 2 > radius ** 2:
                transparent.append((px, py))
                row.append("#000000")
                continue
            r = g = b = 0
            for oy in offsets:
                for ox in offsets:
                    color = bg
                    for poly, c in layers:
                        if _inside(poly, px + ox, py + oy):
                            color = c
                    r += color[0]
                    g += color[1]
                    b += color[2]
            row.append("#{:02x}{:02x}{:02x}".format(r // 9, g // 9, b // 9))
        rows.append("{" + " ".join(row) + "}")
    img.put(" ".join(rows))
    for px, py in transparent:
        img.transparency_set(px, py, True)
    return img


class Logo(tk.Canvas):
    def __init__(self, parent, theme, size, bar_color=T.TEXT, cross_color=T.ACCENT, bg=None):
        super().__init__(parent, width=size, height=size, bg=bg or parent.cget("bg"),
                         highlightthickness=0, bd=0)
        draw_logo(self, 0, 0, size, bar_color, cross_color)


# ---------------------------------------------------------------------- 按钮

class Button(tk.Canvas):
    KINDS = {
        "primary": dict(fill=T.ACCENT, hover=T.ACCENT_HOVER, press=T.ACCENT_PRESS, fg=T.ON_ACCENT,
                        hover_fg=T.ON_ACCENT, outline=None),
        "secondary": dict(fill=T.HOVER, hover="#2B3142", press=T.INPUT, fg=T.TEXT, hover_fg=T.TEXT,
                          outline=T.INPUT_BORDER),
        "ghost": dict(fill=None, hover=T.HOVER, press=T.INPUT, fg=T.MUTED, hover_fg=T.TEXT, outline=None),
    }

    def __init__(self, parent, theme, text="", command=None, kind="primary", icon=None,
                 font=None, height=36, padx=16, width=None, radius=8):
        self.theme = theme
        self.parent_bg = parent.cget("bg")
        super().__init__(parent, bg=self.parent_bg, highlightthickness=0, bd=0,
                         height=theme.px(height), cursor="hand2")
        self.text = text
        self.command = command
        self.kind = self.KINDS[kind]
        self.icon = theme.icon(icon) if icon else None
        self.font = font or theme.f_body_bold
        self.padx = theme.px(padx)
        self.radius = theme.px(radius)
        self.fixed_width = theme.px(width) if width else None
        self.enabled = True
        self.hover = self.pressed = False
        self._resize()
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Enter>", lambda e: self._set(hover=True))
        self.bind("<Leave>", lambda e: self._set(hover=False, pressed=False))
        self.bind("<ButtonPress-1>", lambda e: self._set(pressed=True))
        self.bind("<ButtonRelease-1>", self._release)

    def _content_width(self):
        w = self.font.measure(self.text) if self.text else 0
        if self.icon:
            w += self.theme.f_icon.measure(self.icon) + (self.theme.px(8) if self.text else 0)
        return w

    def _resize(self):
        self.configure(width=self.fixed_width or self._content_width() + 2 * self.padx)
        self._draw()

    def _set(self, **kw):
        for k, v in kw.items():
            setattr(self, k, v)
        self._draw()

    def _release(self, event):
        fire = self.pressed and self.hover and self.enabled
        self._set(pressed=False)
        if fire and self.command:
            self.command()

    def set_enabled(self, enabled):
        self.enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._draw()

    def set_text(self, text):
        self.text = text
        self._resize()

    def _draw(self):
        self.delete("all")
        w = self.winfo_width() if self.winfo_width() > 1 else int(self.cget("width"))
        h = self.winfo_height() if self.winfo_height() > 1 else int(self.cget("height"))
        k = self.kind
        if not self.enabled:
            fill, fg, outline = (T.HOVER if k["fill"] else None), T.DIM, None
        else:
            fill = k["press"] if self.pressed else k["hover"] if self.hover else k["fill"]
            fg = k["hover_fg"] if self.hover else k["fg"]
            outline = k["outline"]
        if fill or outline:
            round_rect(self, 1, 1, w - 1, h - 1, self.radius, fill=fill or self.parent_bg,
                       outline=outline or "")
        x = (w - self._content_width()) / 2
        if self.icon:
            self.create_text(x, h / 2 + 1, text=self.icon, font=self.theme.f_icon, fill=fg, anchor="w")
            x += self.theme.f_icon.measure(self.icon) + self.theme.px(8)
        if self.text:
            self.create_text(x, h / 2, text=self.text, font=self.font, fill=fg, anchor="w")


class NavItem(tk.Canvas):
    def __init__(self, parent, theme, text, icon, command):
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0,
                         height=theme.px(44), cursor="hand2")
        self.theme, self.text, self.icon, self.command = theme, text, theme.icon(icon), command
        self.active = self.hover = False
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Enter>", lambda e: self._set_hover(True))
        self.bind("<Leave>", lambda e: self._set_hover(False))
        self.bind("<Button-1>", lambda e: command())

    def _set_hover(self, hover):
        self.hover = hover
        self._draw()

    def set_active(self, active):
        self.active = active
        self._draw()

    def _draw(self):
        self.delete("all")
        px = self.theme.px
        w, h = self.winfo_width(), self.winfo_height()
        if self.active or self.hover:
            round_rect(self, px(12), px(2), w - px(12), h - px(2), px(8),
                       fill="#1A1F2B" if self.active else "#161A23", outline="")
        if self.active:
            round_rect(self, px(12), px(12), px(15), h - px(12), px(2), fill=T.ACCENT, outline="")
        color = T.ACCENT if self.active else (T.TEXT if self.hover else T.MUTED)
        self.create_text(px(32), h / 2 + 1, text=self.icon, font=self.theme.f_icon, fill=color, anchor="w")
        self.create_text(px(62), h / 2, text=self.text, anchor="w",
                         font=self.theme.f_body_bold if self.active else self.theme.f_body,
                         fill=T.TEXT if (self.active or self.hover) else T.MUTED)


# ---------------------------------------------------------------------- 表单控件

class Switch(tk.Canvas):
    def __init__(self, parent, theme, variable, command=None):
        self.theme = theme
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0,
                         width=theme.px(42), height=theme.px(24), cursor="hand2")
        self.variable, self.command = variable, command
        self.enabled = True
        self.bind("<Button-1>", self._toggle)
        variable.trace_add("write", lambda *_: self._draw())
        self._draw()

    def set_enabled(self, enabled):
        self.enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._draw()

    def _toggle(self, _event):
        if not self.enabled:
            return
        self.variable.set(not self.variable.get())
        if self.command:
            self.command()

    def _draw(self):
        self.delete("all")
        px = self.theme.px
        w, h = px(42), px(24)
        on = bool(self.variable.get())
        if not self.enabled:
            track, knob = T.INPUT, T.DIM
        else:
            track, knob = (T.ACCENT, T.ON_ACCENT) if on else (T.INPUT_BORDER, T.MUTED)
        round_rect(self, 1, 1, w - 1, h - 1, h / 2, fill=track, outline="")
        d = h - px(8)
        x = w - px(4) - d if on else px(4)
        self.create_oval(x, px(4), x + d, px(4) + d, fill=knob, outline="")


class Chip(tk.Canvas):
    """胶囊标签。value 为 None 时是多选开关（BooleanVar），否则是单选（variable == value 时选中）。"""

    def __init__(self, parent, theme, text, variable, command=None, value=None):
        self.theme = theme
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0,
                         height=theme.px(32), cursor="hand2")
        self.variable, self.command, self.value = variable, command, value
        self.hover = False
        self.enabled = True
        self.set_text(text)
        self.bind("<Enter>", lambda e: self._set_hover(True))
        self.bind("<Leave>", lambda e: self._set_hover(False))
        self.bind("<Button-1>", self._toggle)
        variable.trace_add("write", lambda *_: self._draw())

    def set_text(self, text):
        self.text = text
        self.configure(width=self.theme.f_small_bold.measure(text) + self.theme.px(30))
        self._draw()

    def set_enabled(self, enabled):
        self.enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._draw()

    def _selected(self):
        if self.value is None:
            return bool(self.variable.get())
        return self.variable.get() == self.value

    def _set_hover(self, hover):
        self.hover = hover
        self._draw()

    def _toggle(self, _event):
        if not self.enabled:
            return
        if self.value is None:
            self.variable.set(not self.variable.get())
        else:
            self.variable.set(self.value)
        if self.command:
            self.command()

    def _draw(self):
        self.delete("all")
        w, h = int(self.cget("width")), int(self.cget("height"))
        if not self.enabled:
            fill, outline, fg = T.INPUT, T.INPUT_BORDER, T.DIM
        elif self._selected():
            fill, outline, fg = T.ACCENT_DIM, T.ACCENT, T.ACCENT
        else:
            fill, outline, fg = T.INPUT, (T.INPUT_HOVER if self.hover else T.INPUT_BORDER), \
                (T.TEXT if self.hover else T.MUTED)
        round_rect(self, 1, 1, w - 2, h - 2, h / 2, fill=fill, outline=outline)
        self.create_text(w / 2, h / 2, text=self.text, font=self.theme.f_small_bold, fill=fg)


class ScrollFrame(tk.Frame):
    """可滚动容器：内容放进 .inner，鼠标滚轮在区域内即可滚动。"""
    _wheel_installed = False

    def __init__(self, parent, theme, bg=T.BG, scroll_style="Page.Vertical.TScrollbar"):
        super().__init__(parent, bg=bg)
        self.canvas = tk.Canvas(self, bg=bg, highlightthickness=0, bd=0, yscrollincrement=theme.px(24))
        self.inner = tk.Frame(self.canvas, bg=bg)
        self._window = self.canvas.create_window(0, 0, window=self.inner, anchor="nw")
        self.inner.bind("<Configure>", lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.bind("<Configure>", lambda e: self.canvas.itemconfigure(self._window, width=e.width))
        scrollbar(self, self.canvas, scroll_style).pack(side="right", fill="y", padx=(theme.px(6), 0))
        self.canvas.pack(side="left", fill="both", expand=True)
        if not ScrollFrame._wheel_installed:
            self.bind_all("<MouseWheel>", ScrollFrame._on_wheel, add="+")
            ScrollFrame._wheel_installed = True

    @staticmethod
    def _on_wheel(event):
        if not isinstance(event.widget, tk.Misc):
            return
        try:
            widget = event.widget.winfo_containing(event.x_root, event.y_root)
        except (KeyError, tk.TclError):
            return
        while widget is not None:
            if isinstance(widget, ScrollFrame):
                widget.scroll(event.delta)
                return
            widget = widget.master

    def scroll(self, delta):
        if self.inner.winfo_height() > self.canvas.winfo_height():
            self.canvas.yview_scroll(int(-delta / 120) * 3, "units")

    def scroll_top(self):
        self.canvas.yview_moveto(0)

    def clear(self):
        for child in self.inner.winfo_children():
            child.destroy()
        self.scroll_top()


class Slider(tk.Canvas):
    def __init__(self, parent, theme, variable, from_, to, step=1, width=280, command=None):
        self.theme = theme
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0,
                         width=theme.px(width), height=theme.px(26), cursor="hand2")
        self.variable, self.from_, self.to, self.step, self.command = variable, from_, to, step, command
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Button-1>", self._drag)
        self.bind("<B1-Motion>", self._drag)
        self.bind("<ButtonRelease-1>", lambda e: self.command and self.command())
        variable.trace_add("write", lambda *_: self._draw())

    def set_range(self, from_, to):
        self.from_, self.to = from_, to
        self._draw()

    def _pad(self):
        return self.theme.px(9)

    def _drag(self, event):
        w = self.winfo_width()
        ratio = min(1.0, max(0.0, (event.x - self._pad()) / max(1, w - 2 * self._pad())))
        value = self.from_ + ratio * (self.to - self.from_)
        value = int(round(value / self.step) * self.step)
        self.variable.set(min(self.to, max(self.from_, value)))

    def _draw(self):
        self.delete("all")
        px, pad = self.theme.px, self._pad()
        w, h = self.winfo_width(), self.winfo_height()
        if w <= 1:
            return
        try:
            value = float(self.variable.get())
        except (tk.TclError, ValueError):
            value = self.from_
        ratio = (value - self.from_) / max(1, self.to - self.from_)
        ratio = min(1.0, max(0.0, ratio))
        x = pad + ratio * (w - 2 * pad)
        y = h / 2
        self.create_line(pad, y, w - pad, y, fill=T.INPUT_BORDER, width=px(4), capstyle="round")
        self.create_line(pad, y, x, y, fill=T.ACCENT, width=px(4), capstyle="round")
        r = px(8)
        self.create_oval(x - r, y - r, x + r, y + r, fill=T.TEXT, outline=T.ACCENT, width=px(3))


class Field(tk.Frame):
    """带焦点高亮边框的输入框。"""

    def __init__(self, parent, theme, textvariable, width=20, icon=None):
        super().__init__(parent, bg=T.INPUT, highlightthickness=1,
                         highlightbackground=T.INPUT_BORDER, highlightcolor=T.INPUT_BORDER)
        px = theme.px
        if icon:
            tk.Label(self, text=theme.icon(icon), font=theme.f_icon, bg=T.INPUT, fg=T.DIM).pack(
                side="left", padx=(px(10), 0))
        self.entry = tk.Entry(self, textvariable=textvariable, width=width, relief="flat", bd=0,
                              bg=T.INPUT, fg=T.TEXT, insertbackground=T.ACCENT, font=theme.f_body,
                              selectbackground=T.ACCENT_DIM, selectforeground=T.TEXT,
                              disabledbackground=T.INPUT, disabledforeground=T.DIM)
        self.entry.pack(side="left", fill="x", expand=True, padx=px(10), pady=px(7))
        self.entry.bind("<FocusIn>", lambda e: self.configure(highlightbackground=T.ACCENT, highlightcolor=T.ACCENT))
        self.entry.bind("<FocusOut>", lambda e: self.configure(highlightbackground=T.INPUT_BORDER,
                                                               highlightcolor=T.INPUT_BORDER))
        for w in (self, *self.winfo_children()):
            w.bind("<Button-1>", lambda e: self.entry.focus_set(), add="+")


class ProgressLine(tk.Canvas):
    def __init__(self, parent, theme):
        self.theme = theme
        super().__init__(parent, bg=parent.cget("bg"), highlightthickness=0, bd=0, height=theme.px(3))
        self.fraction = 0.0
        self.running = False
        self.offset = 0.0
        self.bind("<Configure>", lambda e: self._draw())

    def set(self, fraction):
        self.running = False
        self.fraction = fraction
        self._draw()

    def start(self):
        if not self.running:
            self.running = True
            self._animate()

    def _animate(self):
        if not self.running or not self.winfo_exists():
            return
        self.offset = (self.offset + 0.012) % 1.4
        self._draw()
        self.after(16, self._animate)

    def _draw(self):
        self.delete("all")
        w, h = self.winfo_width(), self.winfo_height()
        if self.running:
            start = (self.offset - 0.4) * w
            self.create_rectangle(max(0, start), 0, min(w, start + 0.4 * w), h, fill=T.ACCENT, outline="")
        elif self.fraction > 0:
            self.create_rectangle(0, 0, w * self.fraction, h, fill=T.ACCENT, outline="")


def scrollbar(parent, target, style="Dark.Vertical.TScrollbar"):
    bar = ttk.Scrollbar(parent, orient="vertical", command=target.yview, style=style)
    target.configure(yscrollcommand=bar.set)
    return bar


def label(parent, text="", font=None, fg=T.TEXT, **kw):
    return tk.Label(parent, text=text, font=font, fg=fg, bg=parent.cget("bg"), **kw)


# ---------------------------------------------------------------------- 提示与弹窗

class Toast:
    current = None

    @classmethod
    def show(cls, host, theme, text, kind="success", duration=2800):
        if cls.current is not None and cls.current.winfo_exists():
            cls.current.destroy()
        px = theme.px
        color = {"success": T.SUCCESS, "error": T.ERROR, "warn": T.WARN}.get(kind, T.ACCENT)
        frame = tk.Frame(host, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        tk.Frame(frame, bg=color, width=px(3)).pack(side="left", fill="y")
        tk.Label(frame, text=theme.icon("success" if kind == "success" else "info"), font=theme.f_icon,
                 fg=color, bg=T.CARD).pack(side="left", padx=(px(14), px(8)), pady=px(12))
        tk.Label(frame, text=text, font=theme.f_body, fg=T.TEXT, bg=T.CARD).pack(side="left", padx=(0, px(18)))
        frame.place(relx=1, rely=1, x=-px(24), y=-px(36), anchor="se")
        frame.lift()
        cls.current = frame
        frame.after(duration, lambda: frame.winfo_exists() and frame.destroy())


def create_modal(root, title):
    top = tk.Toplevel(root)
    top.withdraw()
    top.title(title)
    top.configure(bg=T.BG)
    top.resizable(False, False)
    top.transient(root)
    return top


def present_modal(root, top, min_width):
    """居中显示模态窗口并阻塞到它关闭（期间界面事件照常处理）。"""
    top.minsize(min_width, 1)
    top.update_idletasks()
    w = max(top.winfo_reqwidth(), min_width)
    h = top.winfo_reqheight()
    x = root.winfo_rootx() + (root.winfo_width() - w) // 2
    y = root.winfo_rooty() + (root.winfo_height() - h) // 3
    # 只设置位置，窗口大小随内容变化
    top.geometry("+{}+{}".format(max(0, x), max(0, y)))
    T.dark_titlebar(top)
    top.deiconify()
    top.grab_set()
    top.focus_set()
    root.wait_window(top)


def ask_string(root, theme, title, prompt, initial="", validate=None, confirm="确定"):
    """单行输入对话框。validate(text) 抛出 ValueError 时在对话框内显示错误；取消返回 None。"""
    px = theme.px
    top = create_modal(root, title)
    result = [None]
    var = tk.StringVar(value=initial)

    body = tk.Frame(top, bg=T.BG)
    body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(4)))
    tk.Label(body, text=title, font=theme.f_h3, fg=T.TEXT, bg=T.BG, anchor="w").pack(fill="x")
    tk.Label(body, text=prompt, font=theme.f_body, fg=T.MUTED, bg=T.BG, anchor="w", justify="left",
             wraplength=px(420)).pack(fill="x", pady=(px(6), px(12)))
    field = Field(body, theme, var)
    field.pack(fill="x")
    error = tk.Label(body, text="", font=theme.f_small, fg=T.ERROR, bg=T.BG, anchor="w")
    error.pack(fill="x", pady=(px(6), 0))

    def finish(ok):
        if ok:
            text = var.get().strip()
            try:
                result[0] = validate(text) if validate else text
            except ValueError as e:
                error.configure(text=str(e))
                return
        top.grab_release()
        top.destroy()

    bar = tk.Frame(top, bg=T.BG)
    bar.pack(fill="x", padx=px(24), pady=(px(8), px(20)))
    Button(bar, theme, confirm, lambda: finish(True), height=34, padx=20).pack(side="right", padx=(px(8), 0))
    Button(bar, theme, "取消", lambda: finish(False), kind="secondary", height=34, padx=20).pack(side="right")
    field.entry.bind("<Return>", lambda e: finish(True))
    top.bind("<Escape>", lambda e: finish(False))
    top.protocol("WM_DELETE_WINDOW", lambda: finish(False))
    top.after(80, lambda: (field.entry.focus_set(), field.entry.select_range(0, "end")))
    present_modal(root, top, px(460))
    return result[0]


def show_dialog(root, theme, title, message, kind="info", buttons=None):
    """深色模态对话框，返回被点击按钮对应的值（关闭窗口返回 None）。"""
    buttons = buttons or [("确定", True, "primary")]
    px = theme.px
    color = {"error": T.ERROR, "warn": T.WARN, "success": T.SUCCESS}.get(kind, T.ACCENT)

    top = create_modal(root, title)
    result = [None]

    def finish(value):
        result[0] = value
        top.grab_release()
        top.destroy()

    body = tk.Frame(top, bg=T.BG)
    body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(8)))
    tk.Label(body, text=theme.icon(kind if kind in ("error", "warn", "success") else "info"),
             font=theme.f_icon_big, fg=color, bg=T.BG).pack(side="left", anchor="n", padx=(0, px(16)))
    text = tk.Frame(body, bg=T.BG)
    text.pack(side="left", fill="both", expand=True)
    tk.Label(text, text=title, font=theme.f_h3, fg=T.TEXT, bg=T.BG, anchor="w").pack(fill="x")
    tk.Label(text, text=message, font=theme.f_body, fg=T.MUTED, bg=T.BG, anchor="w", justify="left",
             wraplength=px(400)).pack(fill="x", pady=(px(6), 0))

    bar = tk.Frame(top, bg=T.BG)
    bar.pack(fill="x", padx=px(24), pady=(px(12), px(20)))
    default = None
    for caption, value, kind_ in buttons:
        Button(bar, theme, caption, command=lambda v=value: finish(v), kind=kind_,
               height=34, padx=20).pack(side="right", padx=(px(8), 0))
        if kind_ == "primary" and default is None:
            default = value
    top.bind("<Return>", lambda e: finish(default))
    top.bind("<Escape>", lambda e: finish(None))
    top.protocol("WM_DELETE_WINDOW", lambda: finish(None))
    present_modal(root, top, px(440))
    return result[0]
