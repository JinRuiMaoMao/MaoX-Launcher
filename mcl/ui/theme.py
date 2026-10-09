import ctypes
import os
import tkinter.font as tkfont
from tkinter import ttk

BG = "#151820"
SIDEBAR = "#10131A"
CARD = "#1A1E28"
CARD_BORDER = "#252A36"
INPUT = "#1F2430"
INPUT_BORDER = "#2C3242"
INPUT_HOVER = "#3A4256"
HOVER = "#232836"
LOG_BG = "#10131A"

TEXT = "#E8ECF3"
MUTED = "#8A93A6"
DIM = "#596174"

ACCENT = "#00D9FF"
ACCENT_HOVER = "#5CE6FF"
ACCENT_PRESS = "#00B5D6"
ACCENT_DIM = "#0C2F3A"
ON_ACCENT = "#06141A"

ERROR = "#FF5C7A"
WARN = "#FFB547"
SUCCESS = "#3DDC97"

# Segoe Fluent Icons / Segoe MDL2 Assets 码位，后面是没有图标字体时的备用字符
ICONS = {
    "play": ("\uE768", "▶"),
    "download": ("\uE896", "↓"),
    "settings": ("\uE713", "⚙"),
    "folder": ("\uE838", "□"),
    "refresh": ("\uE72C", "↻"),
    "delete": ("\uE74D", "×"),
    "search": ("\uE721", "⌕"),
    "game": ("\uE7FC", "◆"),
    "java": ("\uE943", "◆"),
    "cloud": ("\uE753", "◆"),
    "tune": ("\uE9E9", "◆"),
    "info": ("\uE946", "i"),
    "error": ("\uE783", "!"),
    "warn": ("\uE7BA", "!"),
    "success": ("\uE930", "✓"),
    "puzzle": ("\uEA86", "◆"),
    "check": ("\uE73E", "✓"),
    "people": ("\uE716", "☺"),
    "copy": ("\uE8C8", "⧉"),
    "link": ("\uE71B", "∞"),
    "close": ("\uE711", "✕"),
}


class Theme:
    def __init__(self, root):
        self.root = root
        self.scale = root.winfo_fpixels("1i") / 96
        families = set(tkfont.families(root))

        def pick(*names, default):
            return next((n for n in names if n in families), default)

        ui = pick("HarmonyOS Sans SC", "MiSans", "Microsoft YaHei UI", "Microsoft YaHei",
                  "PingFang SC", "Noto Sans CJK SC", default="TkDefaultFont")
        # 粗体统一用 Segoe（中文由系统字体链接回退），微软雅黑粗体的西文在部分系统上会渲染成点阵
        display = pick("Segoe UI Variable Display", "Segoe UI", default=ui)
        mono = pick("Cascadia Mono", "JetBrains Mono", "Consolas", default="TkFixedFont")
        self.icon_family = pick("Segoe Fluent Icons", "Segoe MDL2 Assets", default=None)

        def font(family, size, weight="normal"):
            return tkfont.Font(root=root, family=family, size=size, weight=weight)

        self.f_body = font(ui, 10)
        self.f_body_bold = font(display, 10, "bold")
        self.f_small = font(ui, 9)
        self.f_small_bold = font(display, 9, "bold")
        self.f_h1 = font(display, 20, "bold")
        self.f_h3 = font(display, 12, "bold")
        self.f_hero = font(display, 32, "bold")
        self.f_overline = font(display, 8, "bold")
        self.f_logo = font(display, 16, "bold")
        self.f_button_big = font(display, 13, "bold")
        self.f_mono = font(mono, 9)
        self.f_icon = font(self.icon_family or ui, 11)
        self.f_icon_big = font(self.icon_family or ui, 20)

        for name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont"):
            tkfont.nametofont(name).configure(family=ui, size=10)

    def px(self, n):
        return max(1, int(round(n * self.scale)))

    def icon(self, name):
        glyph, fallback = ICONS[name]
        return glyph if self.icon_family else fallback

    def apply_ttk(self):
        root, px = self.root, self.px
        style = ttk.Style(root)
        style.theme_use("clam")
        style.configure(".", background=BG, foreground=TEXT, font=self.f_body)

        style.element_create("Flat.Combobox.downarrow", "from", "default")
        style.layout("TCombobox", [("Combobox.field", {"sticky": "nswe", "children": [
            ("Flat.Combobox.downarrow", {"side": "right", "sticky": ""}),
            ("Combobox.padding", {"expand": "1", "sticky": "nswe", "children": [
                ("Combobox.textarea", {"sticky": "nswe"})]})]})])
        style.configure("TCombobox", fieldbackground=INPUT, relief="flat", borderwidth=1, background=INPUT, foreground=TEXT,
                        arrowcolor=MUTED, bordercolor=INPUT_BORDER, lightcolor=INPUT, darkcolor=INPUT,
                        insertcolor=ACCENT, selectbackground=INPUT, selectforeground=TEXT,
                        padding=(px(10), px(7)), arrowsize=px(12))
        style.map("TCombobox",
                  fieldbackground=[("readonly", INPUT), ("disabled", INPUT)],
                  background=[("readonly", INPUT), ("active", INPUT)],
                  foreground=[("disabled", DIM), ("readonly", TEXT)],
                  bordercolor=[("focus", ACCENT), ("hover", INPUT_HOVER)],
                  lightcolor=[("focus", INPUT)], darkcolor=[("focus", INPUT)],
                  arrowcolor=[("hover", ACCENT), ("focus", ACCENT)],
                  selectbackground=[("readonly", INPUT)], selectforeground=[("readonly", TEXT)])
        root.option_add("*TCombobox*Listbox.background", CARD)
        root.option_add("*TCombobox*Listbox.foreground", TEXT)
        root.option_add("*TCombobox*Listbox.selectBackground", ACCENT_DIM)
        root.option_add("*TCombobox*Listbox.selectForeground", ACCENT)
        root.option_add("*TCombobox*Listbox.font", self.f_body)
        root.option_add("*TCombobox*Listbox.borderWidth", 0)
        root.option_add("*TCombobox*Listbox.relief", "flat")

        style.layout("Dark.Vertical.TScrollbar", [
            ("Vertical.Scrollbar.trough", {"sticky": "ns", "children": [
                ("Vertical.Scrollbar.thumb", {"expand": "1", "sticky": "nswe"})]})])
        style.configure("Dark.Vertical.TScrollbar", troughcolor=CARD, background=INPUT_BORDER,
                        bordercolor=CARD, lightcolor=INPUT_BORDER, darkcolor=INPUT_BORDER,
                        gripcount=0, width=px(8))
        style.map("Dark.Vertical.TScrollbar", background=[("active", INPUT_HOVER)],
                  lightcolor=[("active", INPUT_HOVER)], darkcolor=[("active", INPUT_HOVER)])
        style.layout("Page.Vertical.TScrollbar", style.layout("Dark.Vertical.TScrollbar"))
        style.configure("Page.Vertical.TScrollbar", troughcolor=BG, bordercolor=BG, background=INPUT_BORDER,
                        lightcolor=INPUT_BORDER, darkcolor=INPUT_BORDER, gripcount=0, width=px(8))
        style.map("Page.Vertical.TScrollbar", background=[("active", INPUT_HOVER)],
                  lightcolor=[("active", INPUT_HOVER)], darkcolor=[("active", INPUT_HOVER)])
        style.configure("Log.Vertical.TScrollbar", troughcolor=LOG_BG, bordercolor=LOG_BG)
        style.layout("Log.Vertical.TScrollbar", style.layout("Dark.Vertical.TScrollbar"))
        style.configure("Log.Vertical.TScrollbar", background=INPUT_BORDER, lightcolor=INPUT_BORDER,
                        darkcolor=INPUT_BORDER, gripcount=0, width=px(8))
        style.map("Log.Vertical.TScrollbar", background=[("active", INPUT_HOVER)],
                  lightcolor=[("active", INPUT_HOVER)], darkcolor=[("active", INPUT_HOVER)])

        style.layout("Treeview", [("Treeview.treearea", {"sticky": "nswe"})])
        style.configure("Treeview", background=CARD, fieldbackground=CARD, foreground=TEXT,
                        bordercolor=CARD, lightcolor=CARD, darkcolor=CARD,
                        rowheight=int(self.f_body.metrics("linespace") * 2.1), font=self.f_body)
        style.map("Treeview", background=[("selected", ACCENT_DIM)], foreground=[("selected", ACCENT)])
        style.configure("Treeview.Heading", background=CARD, foreground=DIM, relief="flat",
                        bordercolor=CARD_BORDER, lightcolor=CARD, darkcolor=CARD,
                        font=self.f_small_bold, padding=(0, px(10)))
        style.map("Treeview.Heading", background=[("active", CARD)], foreground=[("active", MUTED)])


def _colorref(hex_color):
    r, g, b = int(hex_color[1:3], 16), int(hex_color[3:5], 16), int(hex_color[5:7], 16)
    return (b << 16) | (g << 8) | r


def dark_titlebar(window, caption=BG):
    """Windows 10/11：深色标题栏；Windows 11 还会把标题栏涂成主色。"""
    if os.name != "nt":
        return
    try:
        window.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(window.winfo_id())
        dwm = ctypes.windll.dwmapi
        on = ctypes.c_int(1)
        if dwm.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(on), ctypes.sizeof(on)) != 0:
            dwm.DwmSetWindowAttribute(hwnd, 19, ctypes.byref(on), ctypes.sizeof(on))
        for attr, color in ((35, caption), (36, TEXT), (34, caption)):
            value = ctypes.c_int(_colorref(color))
            dwm.DwmSetWindowAttribute(hwnd, attr, ctypes.byref(value), ctypes.sizeof(value))
    except Exception:
        pass
