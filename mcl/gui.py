import collections
import ctypes
import json
import locale
import os
import queue
import sys
import threading
import time
import tkinter as tk
import webbrowser
from tkinter import filedialog, ttk

from . import accounts as acc
from . import config as config_mod
from . import crash
from . import java as javautil
from .core import LAUNCHER_VERSION, LOADER_NAMES, GameLauncher, LogParser
from .loaders import LoaderInstaller
from .ui import theme as T
from .ui.account_dialog import AccountDialog, draw_avatar, face_image
from .ui.banner import Banner
from .ui.install_dialog import ask_install_options
from .ui.mods_page import ModsPage
from .ui.multiplayer_page import MultiplayerPage
from .ui.version_dialog import VersionDialog
from .ui.widgets import (Button, Chip, Field, Logo, NavItem, ProgressLine, ScrollFrame, Slider, Switch, Toast,
                         label, make_icon, scrollbar, show_dialog)

APP_NAME = "MaoX Launcher"
AUTO_JAVA = "自动选择（推荐）"
SOURCES = [("auto", "自动选择最快的源（推荐）"), ("bmclapi", "BMCLAPI 国内镜像"), ("official", "Mojang 官方源")]
VERSION_TYPES = {"release": "正式版", "snapshot": "快照版", "old_beta": "远古 Beta", "old_alpha": "远古 Alpha"}
AFTER_LAUNCH = [("keep", "保持不变"), ("minimize", "最小化启动器"), ("hide", "隐藏启动器，游戏退出后恢复")]
MAX_LOG_LINES = 5000
TOOLS_DIR = os.path.join(config_mod.BASE_DIR, "tools")


def _decode(raw):
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode(locale.getpreferredencoding(False), "replace")


def total_memory_mb():
    if os.name == "nt":
        class MemoryStatus(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong)] + \
                       [(n, ctypes.c_ulonglong) for n in ("total", "avail", "page", "apage", "virt", "avirt", "ext")]
        status = MemoryStatus()
        status.dwLength = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return status.total // (1024 * 1024)
    try:
        return os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES") // (1024 * 1024)
    except (ValueError, OSError, AttributeError):
        return 16384


def spaced(text):
    return " ".join(text)


class App:
    def __init__(self, root):
        self.root = root
        root.withdraw()
        self.cfg = config_mod.load_config()
        self.theme = T.Theme(root)
        self.theme.apply_ttk()
        self.px = self.theme.px
        self.queue = queue.Queue()
        self.busy = False
        self.manifest = None
        self.installed = set()
        self.java_map = {}
        self.game_proc = None
        self.running_version = None
        self.hidden_for_game = False
        self.skins = {}
        self.avatar_images = {}
        self._skin_loading = set()
        self._init_accounts()
        self.pages = {}
        self.nav = {}
        self.current_page = None
        self.ram_mb = total_memory_mb()

        root.title(APP_NAME)
        root.configure(bg=T.BG)
        root.geometry("{}x{}".format(self.px(1140), self.px(740)))
        root.minsize(self.px(1000), self.px(660))
        self.icon = make_icon(48)
        root.iconphoto(True, self.icon)

        self._build()
        self.refresh_installed()
        self.show_page("launch")
        self._poll_queue()
        threading.Thread(target=self._detect_java_background, args=(self._runtime_dir(),), daemon=True).start()
        root.protocol("WM_DELETE_WINDOW", self.on_close)
        T.dark_titlebar(root)
        root.deiconify()

    # ------------------------------------------------------------------ 线程与界面通信

    def ui(self, fn, *args):
        self.queue.put((fn, args))

    def _poll_queue(self):
        # 先排好下一轮：回调里可能弹出模态窗口（嵌套事件循环）或抛出异常
        self.root.after(50, self._poll_queue)
        while True:
            try:
                fn, args = self.queue.get_nowait()
            except queue.Empty:
                return
            try:
                fn(*args)
            except Exception:
                self.root.report_callback_exception(*sys.exc_info())

    def log(self, text):
        self.ui(self._append_log, text, "launcher")

    def progress(self, done, total, text=""):
        self.ui(self._set_progress, done, total, text)

    def make_launcher(self):
        launcher = GameLauncher(dict(self.cfg), log=self.log, progress=self.progress)
        launcher.manifest = self.manifest
        return launcher

    def dialog(self, title, message, kind="info", buttons=None):
        return show_dialog(self.root, self.theme, title, message, kind, buttons)

    def toast(self, text, kind="success"):
        Toast.show(self.main, self.theme, text, kind)

    def spawn(self, fn, on_done=None, on_error=None):
        """后台执行 fn，不占用全局任务锁；回调在界面线程中执行。"""
        def worker():
            try:
                result = fn()
            except Exception as e:
                if on_error:
                    self.ui(on_error, e)
                else:
                    self.log("[错误] {}".format(e))
                return
            if on_done:
                self.ui(on_done, result)

        threading.Thread(target=worker, daemon=True).start()

    def run_task(self, name, fn, on_done=None):
        if self.busy:
            self.toast("当前有任务正在进行，请稍候", "warn")
            return
        if not self.save_settings():
            return
        self._set_busy(True, name)

        def worker():
            try:
                result, error = fn(), None
            except Exception as e:
                result, error = None, e
            self.ui(self._task_finished, name, result, error, on_done)

        threading.Thread(target=worker, daemon=True).start()

    def _task_finished(self, name, result, error, on_done):
        self._set_busy(False)
        if error is not None:
            self._append_log("[错误] {}失败：{}".format(name, error), "error")
            self.dialog(name + "失败", str(error), "error")
        elif on_done:
            on_done(result)

    def _set_busy(self, busy, text=""):
        self.busy = busy
        for btn in (self.launch_btn, self.install_btn, self.refresh_btn):
            btn.set_enabled(not busy)
        self.launch_btn.set_text("请稍候…" if busy else "启动游戏")
        if busy:
            self.status.set(text + "…")
            self.progress_line.start()
        else:
            self.status.set("就绪")
            self.progress_text.set("")
            self.progress_line.set(0)

    def _set_progress(self, done, total, text):
        if not total:
            return
        self.progress_line.set(done / total)
        self.status.set(text)
        self.progress_text.set("{} / {}   {:.0f}%".format(done, total, done * 100 / total))

    def _append_log(self, text, tag=None):
        if tag is None:
            upper = text.upper()
            if "/ERROR]" in upper or "/FATAL]" in upper or "EXCEPTION" in upper:
                tag = "error"
            elif "/WARN]" in upper:
                tag = "warn"
        self.log_text.configure(state="normal")
        self.log_text.insert("end", text + "\n", tag)
        lines = int(self.log_text.index("end-1c").split(".")[0])
        if lines > MAX_LOG_LINES:
            self.log_text.delete("1.0", "{}.0".format(lines - MAX_LOG_LINES))
        self.log_text.configure(state="disabled")
        self.log_text.see("end")

    # ------------------------------------------------------------------ 框架布局

    def _build(self):
        px, th = self.px, self.theme

        sidebar = tk.Frame(self.root, bg=T.SIDEBAR, width=px(236))
        sidebar.pack(side="left", fill="y")
        sidebar.pack_propagate(False)
        tk.Frame(self.root, bg=T.CARD_BORDER, width=1).pack(side="left", fill="y")

        brand = tk.Frame(sidebar, bg=T.SIDEBAR)
        brand.pack(fill="x", padx=px(26), pady=(px(30), px(34)))
        Logo(brand, th, px(34)).pack(side="left")
        names = tk.Frame(brand, bg=T.SIDEBAR)
        names.pack(side="left", padx=(px(14), 0))
        label(names, "MaoX", th.f_logo).pack(anchor="w")
        label(names, spaced("LAUNCHER"), th.f_overline, T.ACCENT).pack(anchor="w")

        for key, text, icon in (("launch", "启动", "play"), ("download", "下载", "download"),
                                ("mods", "资源", "puzzle"), ("multiplayer", "联机", "people"),
                                ("settings", "设置", "settings")):
            item = NavItem(sidebar, th, text, icon, lambda k=key: self.show_page(k))
            item.pack(fill="x", pady=px(2))
            self.nav[key] = item

        label(sidebar, "v" + LAUNCHER_VERSION, th.f_small, T.DIM).pack(side="bottom", anchor="w",
                                                                        padx=px(26), pady=(0, px(20)))
        account = tk.Frame(sidebar, bg="#151922", highlightthickness=1, highlightbackground=T.CARD_BORDER,
                           cursor="hand2")
        account.pack(side="bottom", fill="x", padx=px(14), pady=px(12))
        self.avatar = tk.Canvas(account, width=px(38), height=px(38), bg="#151922", highlightthickness=0)
        self.avatar.pack(side="left", padx=px(12), pady=px(12))
        info = tk.Frame(account, bg="#151922")
        info.pack(side="left", fill="x", expand=True)
        self.account_name = label(info, "", th.f_body_bold)
        self.account_name.pack(anchor="w")
        self.account_type = label(info, "", th.f_small, T.MUTED)
        self.account_type.pack(anchor="w")
        for w in (account, self.avatar, info, *info.winfo_children()):
            w.bind("<Button-1>", lambda e: self.manage_accounts())

        self.main = tk.Frame(self.root, bg=T.BG)
        self.main.pack(side="left", fill="both", expand=True)

        footer = tk.Frame(self.main, bg=T.BG)
        footer.pack(side="bottom", fill="x")
        self.progress_line = ProgressLine(footer, th)
        self.progress_line.pack(fill="x")
        status_row = tk.Frame(footer, bg=T.BG)
        status_row.pack(fill="x", padx=px(36), pady=(px(8), px(12)))
        self.status = tk.StringVar(value="就绪")
        self.progress_text = tk.StringVar()
        label(status_row, font=th.f_small, fg=T.MUTED, textvariable=self.status).pack(side="left")
        label(status_row, font=th.f_small, fg=T.MUTED, textvariable=self.progress_text).pack(side="right")

        container = tk.Frame(self.main, bg=T.BG)
        container.pack(fill="both", expand=True, padx=px(36), pady=(px(30), px(10)))
        container.rowconfigure(0, weight=1)
        container.columnconfigure(0, weight=1)
        for key, builder in (("launch", self._build_launch_page), ("download", self._build_download_page),
                             ("mods", self._build_mods_page), ("multiplayer", self._build_multiplayer_page),
                             ("settings", self._build_settings_page)):
            page = tk.Frame(container, bg=T.BG)
            page.grid(row=0, column=0, sticky="nsew")
            builder(page)
            self.pages[key] = page

        self.username_var.trace_add("write", lambda *_: self._update_account())
        self._update_account()

    def show_page(self, key):
        if self.current_page == "settings" and key != "settings":
            self.save_settings()
        self.current_page = key
        self.pages[key].tkraise()
        for k, item in self.nav.items():
            item.set_active(k == key)
        if key == "download" and self.manifest is None and not self.busy:
            self.load_manifest()
        if key == "mods":
            self.mods_page.on_show()
        if key == "multiplayer":
            self.multiplayer_page.on_show()

    def _page_header(self, page, title, subtitle):
        header = tk.Frame(page, bg=T.BG)
        header.pack(fill="x", pady=(0, self.px(22)))
        text = tk.Frame(header, bg=T.BG)
        text.pack(side="left")
        label(text, title, self.theme.f_h1).pack(anchor="w")
        label(text, subtitle, self.theme.f_body, T.MUTED).pack(anchor="w", pady=(self.px(2), 0))
        return header

    def _card(self, parent, **pack):
        card = tk.Frame(parent, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        if pack:
            card.pack(**pack)
        return card

    def _field_label(self, parent, text):
        label(parent, text, self.theme.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, self.px(6)))

    # ------------------------------------------------------------------ 账号

    def _init_accounts(self):
        accounts = [a for a in self.cfg.get("accounts") or [] if isinstance(a, dict) and a.get("type") and a.get("name")]
        if not accounts:
            accounts = [acc.offline_account(self.cfg.get("username") or "Steve")]
        self.accounts = accounts
        self.account_index = min(max(int(self.cfg.get("account_index") or 0), 0), len(accounts) - 1)

    def current_account(self):
        return self.accounts[self.account_index] if self.accounts else None

    def _accounts_changed(self):
        self.cfg["accounts"] = self.accounts
        self.cfg["account_index"] = self.account_index
        account = self.current_account()
        self.cfg["username"] = account["name"] if account else ""
        self.username_var.set(self.cfg["username"])
        config_mod.save_config(self.cfg)
        self._update_account()

    def set_account(self, index):
        self.account_index = index
        self._accounts_changed()

    def add_account(self, account):
        same = next((i for i, a in enumerate(self.accounts) if a["type"] == account["type"]
                     and a["uuid"] == account["uuid"] and a.get("api") == account.get("api")), None)
        if same is None:
            self.accounts.append(account)
            same = len(self.accounts) - 1
        else:
            self.accounts[same] = account
        self.skins.pop(account["uuid"], None)
        self.account_index = same
        self._accounts_changed()

    def replace_account(self, index, account):
        self.accounts[index] = account
        self._accounts_changed()

    def remove_account(self, index):
        del self.accounts[index]
        if self.account_index >= len(self.accounts):
            self.account_index = max(0, len(self.accounts) - 1)
        elif index < self.account_index:
            self.account_index -= 1
        self._accounts_changed()

    def manage_accounts(self):
        AccountDialog(self)

    def avatar_for(self, account, size, on_ready=None):
        """正版 / 外置账号的皮肤头像，尚未加载时返回 None 并在后台获取。"""
        if not account or account["type"] == "offline":
            return None
        key, zoom = account["uuid"], max(1, size // 8)
        if key in self.skins:
            png = self.skins[key]
            if png is None:
                return None
            if (key, zoom) not in self.avatar_images:
                try:
                    self.avatar_images[(key, zoom)] = face_image(png, zoom)
                except tk.TclError:
                    self.skins[key] = None
                    return None
            return self.avatar_images[(key, zoom)]
        if key not in self._skin_loading:
            self._skin_loading.add(key)

            def loaded(png):
                self.skins[key] = png
                self._skin_loading.discard(key)
                self._update_account()
                if on_ready and png:
                    on_ready()

            self.spawn(lambda: acc.fetch_skin(account), loaded, lambda e: loaded(None))
        return None

    def _update_account(self):
        account = self.current_account()
        name = account["name"] if account else "未设置"
        kind = acc.describe(account) if account else "点击添加账号"
        self.account_name.configure(text=name)
        self.account_type.configure(text=kind)
        px = self.px
        draw_avatar(self.avatar, px(38), account, self.avatar_for(account, px(38)), "#151922")
        if hasattr(self, "hero_account_name"):
            self.hero_account_name.configure(text=name)
            self.hero_account_type.configure(text=kind)
            draw_avatar(self.hero_avatar, px(24), account, self.avatar_for(account, px(24)), T.INPUT)

    # ------------------------------------------------------------------ 启动页

    def _build_launch_page(self, page):
        px, th = self.px, self.theme

        hero = self._card(page, fill="x")
        watermark = Logo(hero, th, px(250), bar_color="#1E2330", cross_color="#0F2D38", bg=T.CARD)
        watermark.place(relx=1.0, rely=0.5, x=-px(40), anchor="e")
        title_box = tk.Frame(hero, bg=T.CARD)
        title_box.pack(anchor="w", padx=px(36), pady=(px(32), 0))
        label(title_box, spaced("READY TO PLAY"), th.f_overline, T.ACCENT).pack(anchor="w")
        self.hero_title = label(title_box, "", th.f_hero)
        self.hero_title.pack(anchor="w", pady=(px(4), 0))
        self.hero_meta = label(title_box, "", th.f_body, T.MUTED)
        self.hero_meta.pack(anchor="w")

        controls = tk.Frame(hero, bg=T.CARD)
        controls.pack(anchor="w", padx=px(36), pady=(px(28), px(34)))
        self.banner = Banner(self, hero, watermark, title_box, controls)
        name_col = tk.Frame(controls, bg=T.CARD)
        name_col.pack(side="left", anchor="s")
        self._field_label(name_col, "账号")
        account = self.current_account()
        self.username_var = tk.StringVar(value=account["name"] if account else "")
        picker = tk.Frame(name_col, bg=T.INPUT, highlightthickness=1, highlightbackground=T.INPUT_BORDER,
                          cursor="hand2", width=px(190), height=px(36))
        picker.pack(anchor="w")
        picker.pack_propagate(False)
        self.hero_avatar = tk.Canvas(picker, width=px(24), height=px(24), bg=T.INPUT, highlightthickness=0)
        self.hero_avatar.pack(side="left", padx=(px(8), px(8)))
        names = tk.Frame(picker, bg=T.INPUT)
        names.pack(side="left", fill="x", expand=True)
        self.hero_account_name = label(names, "", th.f_body_bold)
        self.hero_account_name.pack(side="left")
        self.hero_account_type = label(names, "", th.f_small, T.DIM)
        self.hero_account_type.pack(side="left", padx=(px(6), 0))
        switch = label(picker, "切换", th.f_small, T.ACCENT)
        switch.pack(side="right", padx=(0, px(10)))
        for w in (picker, self.hero_avatar, names, switch, self.hero_account_name, self.hero_account_type):
            w.bind("<Button-1>", lambda e: self.manage_accounts())
        picker.bind("<Enter>", lambda e: picker.configure(highlightbackground=T.INPUT_HOVER))
        picker.bind("<Leave>", lambda e: picker.configure(highlightbackground=T.INPUT_BORDER))

        version_col = tk.Frame(controls, bg=T.CARD)
        version_col.pack(side="left", anchor="s", padx=(px(14), 0))
        self._field_label(version_col, "游戏版本")
        version_row = tk.Frame(version_col, bg=T.CARD)
        version_row.pack(anchor="w")
        self.version_var = tk.StringVar()
        self.version_box = ttk.Combobox(version_row, textvariable=self.version_var, state="readonly",
                                        width=24, font=th.f_body)
        self.version_box.pack(side="left", fill="y")
        Button(version_row, th, "", self.manage_version, kind="secondary", icon="settings", height=36,
               padx=10).pack(side="left", padx=(px(6), 0))
        self.version_var.trace_add("write", lambda *_: self._update_hero())

        self.launch_btn = Button(controls, th, "启动游戏", self.launch, icon="play", font=th.f_button_big,
                                 height=46, width=180)
        self.launch_btn.pack(side="left", anchor="s", padx=(px(18), 0))

        log_card = self._card(page, fill="both", expand=True, pady=(px(20), 0))
        head = tk.Frame(log_card, bg=T.CARD)
        head.pack(fill="x", padx=px(20), pady=(px(12), px(10)))
        label(head, "游戏日志", th.f_body_bold).pack(side="left")
        Button(head, th, "清空", self._clear_log, kind="ghost", icon="delete", height=30, padx=10).pack(side="right")
        Button(head, th, "打开游戏目录", self.open_game_dir, kind="ghost", icon="folder", height=30,
               padx=10).pack(side="right", padx=(0, px(4)))

        body = tk.Frame(log_card, bg=T.LOG_BG)
        body.pack(fill="both", expand=True, padx=1, pady=(0, 1))
        self.log_text = tk.Text(body, wrap="char", state="disabled", font=th.f_mono, bg=T.LOG_BG, fg="#AEB6C6",
                                relief="flat", bd=0, highlightthickness=0, padx=px(18), pady=px(12),
                                selectbackground=T.ACCENT_DIM, insertbackground=T.ACCENT, spacing1=px(2))
        self.log_text.tag_configure("launcher", foreground=T.ACCENT)
        self.log_text.tag_configure("success", foreground=T.SUCCESS)
        self.log_text.tag_configure("error", foreground=T.ERROR)
        self.log_text.tag_configure("warn", foreground=T.WARN)
        scrollbar(body, self.log_text, "Log.Vertical.TScrollbar").pack(side="right", fill="y", padx=(0, px(4)),
                                                                        pady=px(6))
        self.log_text.pack(side="left", fill="both", expand=True)

    def _describe_version(self, version):
        launcher = GameLauncher(self.cfg)
        loader, loader_version, game = launcher.detect_loader(version)
        if loader:
            return "{} {}  ·  Minecraft {}".format(LOADER_NAMES[loader], loader_version or "", game)
        try:
            with open(launcher.version_json_path(version), encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            return "Minecraft"
        return "原版  ·  {}".format(VERSION_TYPES.get(data.get("type"), "Minecraft"))

    def _update_hero(self):
        version = self.version_var.get()
        if version:
            self.hero_title.configure(text=version)
            self.hero_meta.configure(text=self._describe_version(version))
        else:
            self.hero_title.configure(text="还没有游戏版本")
            self.hero_meta.configure(text="前往「下载」安装一个 Minecraft 版本")

    # ------------------------------------------------------------------ 下载页

    def _build_download_page(self, page):
        px, th = self.px, self.theme
        header = self._page_header(page, "下载游戏", "选择一个 Minecraft 版本进行安装，已安装的版本会自动补全缺失文件")
        self.refresh_btn = Button(header, th, "刷新列表", self.load_manifest, kind="secondary", icon="refresh",
                                  height=36)
        self.refresh_btn.pack(side="right", anchor="s")

        toolbar = tk.Frame(page, bg=T.BG)
        toolbar.pack(fill="x", pady=(0, px(14)))
        self.show_release = tk.BooleanVar(value=True)
        self.show_snapshot = tk.BooleanVar(value=False)
        self.show_old = tk.BooleanVar(value=False)
        for text, var in (("正式版", self.show_release), ("快照版", self.show_snapshot), ("远古版本", self.show_old)):
            Chip(toolbar, th, text, var, self._fill_version_tree).pack(side="left", padx=(0, px(8)))
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", lambda *_: self._fill_version_tree())
        Field(toolbar, th, self.search_var, width=22, icon="search").pack(side="right")

        actions = tk.Frame(page, bg=T.BG)
        actions.pack(side="bottom", fill="x", pady=(px(16), 0))
        label(actions, th.icon("puzzle"), th.f_icon, T.ACCENT).pack(side="left")
        loader_text = tk.Frame(actions, bg=T.BG)
        loader_text.pack(side="left", padx=(px(12), 0))
        label(loader_text, "支持全部模组加载器", th.f_body_bold).pack(anchor="w")
        label(loader_text, "点击安装后可选择 Forge、NeoForge、Fabric 或 Quilt", th.f_small, T.MUTED).pack(anchor="w")
        self.install_btn = Button(actions, th, "安装", self.install_selected, icon="download", height=42, width=150)
        self.install_btn.pack(side="right")
        self.selected_label = label(actions, "未选择版本", th.f_body, T.MUTED)
        self.selected_label.pack(side="right", padx=(0, px(18)))

        card = self._card(page, fill="both", expand=True)
        columns = ("id", "type", "time", "state")
        self.tree = ttk.Treeview(card, columns=columns, show="headings", selectmode="browse")
        for col, text, width in zip(columns, ("版本", "类型", "发布日期", "状态"), (260, 140, 160, 120)):
            self.tree.heading(col, text="    " + text, anchor="w")
            self.tree.column(col, width=px(width), anchor="w")
        scrollbar(card, self.tree).pack(side="right", fill="y", padx=(0, px(4)), pady=px(6))
        self.tree.pack(side="left", fill="both", expand=True, padx=(px(6), 0), pady=px(4))
        self.tree.bind("<Double-1>", lambda e: self.install_selected())
        self.tree.bind("<<TreeviewSelect>>", lambda e: self._on_tree_select())
        self.tree_hint = tk.Label(card, text="正在获取版本列表…", font=th.f_body, fg=T.MUTED, bg=T.CARD)
        self.tree_hint.place(relx=0.5, rely=0.5, anchor="center")

    def _on_tree_select(self):
        selection = self.tree.selection()
        if selection:
            self.selected_label.configure(text="已选择  " + selection[0], fg=T.TEXT)
        else:
            self.selected_label.configure(text="未选择版本", fg=T.MUTED)

    # ------------------------------------------------------------------ 模组页

    def _build_mods_page(self, page):
        self.mods_page = ModsPage(self, page)

    def _build_multiplayer_page(self, page):
        self.multiplayer_page = MultiplayerPage(self, page)

    # ------------------------------------------------------------------ 设置页

    def _setting_row(self, card, title, desc=None):
        px = self.px
        row = tk.Frame(card, bg=T.CARD)
        row.pack(fill="x", padx=px(22), pady=(0, px(16)))
        text = tk.Frame(row, bg=T.CARD)
        text.pack(fill="x")
        label(text, title, self.theme.f_body).pack(side="left")
        if desc:
            label(text, desc, self.theme.f_small, T.DIM).pack(side="left", padx=(px(10), 0))
        control = tk.Frame(row, bg=T.CARD)
        control.pack(fill="x", pady=(px(8), 0))
        return control, text

    def _settings_card(self, parent, title, icon, row, col):
        px = self.px
        card = self._card(parent)
        card.grid(row=row, column=col, sticky="nsew", padx=(0 if col == 0 else px(10), px(10) if col == 0 else 0),
                  pady=(0, px(20)))
        head = tk.Frame(card, bg=T.CARD)
        head.pack(fill="x", padx=px(22), pady=(px(18), px(16)))
        label(head, self.theme.icon(icon), self.theme.f_icon, T.ACCENT).pack(side="left")
        label(head, title, self.theme.f_h3).pack(side="left", padx=(px(10), 0))
        return card

    def _build_settings_page(self, page):
        px, th, cfg = self.px, self.theme, self.cfg
        self._page_header(page, "设置", "所有修改会自动保存")
        scroll = ScrollFrame(page, th)
        scroll.pack(fill="both", expand=True)
        grid = tk.Frame(scroll.inner, bg=T.BG)
        grid.pack(fill="both", expand=True, padx=(0, px(8)))
        grid.columnconfigure(0, weight=1, uniform="col")
        grid.columnconfigure(1, weight=1, uniform="col")

        game = self._settings_card(grid, "游戏", "game", 0, 0)
        control, _ = self._setting_row(game, "游戏目录")
        self.mc_dir_var = tk.StringVar(value=cfg["minecraft_dir"])
        Button(control, th, "浏览", self._browse_mc_dir, kind="secondary", height=34, padx=14).pack(side="right")
        Field(control, th, self.mc_dir_var).pack(side="left", fill="x", expand=True, padx=(0, px(8)))
        control, text = self._setting_row(game, "版本隔离", "每个版本独立存档、模组和设置")
        self.isolation_var = tk.BooleanVar(value=cfg["version_isolation"])
        control.destroy()
        Switch(text, th, self.isolation_var).pack(side="right")

        java = self._settings_card(grid, "Java", "java", 0, 1)
        control, _ = self._setting_row(java, "Java 路径")
        self.java_var = tk.StringVar(value=cfg["java_path"] or AUTO_JAVA)
        Button(control, th, "检测", self.detect_java, kind="secondary", height=34, padx=14).pack(side="right")
        Button(control, th, "浏览", self._browse_java, kind="secondary", height=34, padx=14).pack(
            side="right", padx=(0, px(8)))
        self.java_box = ttk.Combobox(control, textvariable=self.java_var, values=[AUTO_JAVA], font=th.f_body)
        self.java_box.pack(side="left", fill="x", expand=True, padx=(0, px(8)))

        max_mem = max(2048, self.ram_mb // 512 * 512)
        control, text = self._setting_row(java, "最大内存", "系统内存 {:.0f} GB".format(self.ram_mb / 1024))
        self.mem_var = tk.IntVar(value=min(int(cfg["max_memory"]), max_mem))
        mem_label = label(text, "", th.f_body_bold, T.ACCENT)
        mem_label.pack(side="right")
        self.mem_var.trace_add("write", lambda *_: mem_label.configure(text="{} MB".format(self.mem_var.get())))
        mem_label.configure(text="{} MB".format(self.mem_var.get()))
        Slider(control, th, self.mem_var, 512, max_mem, step=256).pack(fill="x")

        download = self._settings_card(grid, "下载", "cloud", 1, 0)
        control, _ = self._setting_row(download, "下载源")
        self.source_var = tk.StringVar(value=dict(SOURCES).get(cfg["download_source"], SOURCES[0][1]))
        ttk.Combobox(control, textvariable=self.source_var, values=[s[1] for s in SOURCES], state="readonly",
                     font=th.f_body).pack(fill="x")
        control, text = self._setting_row(download, "下载线程数")
        self.threads_var = tk.IntVar(value=int(cfg["download_threads"]))
        threads_label = label(text, str(self.threads_var.get()), th.f_body_bold, T.ACCENT)
        threads_label.pack(side="right")
        self.threads_var.trace_add("write", lambda *_: threads_label.configure(text=str(self.threads_var.get())))
        Slider(control, th, self.threads_var, 1, 64).pack(fill="x")

        advanced = self._settings_card(grid, "高级", "tune", 1, 1)
        control, _ = self._setting_row(advanced, "游戏窗口大小")
        self.width_var = tk.StringVar(value=str(cfg["window_width"]))
        self.height_var = tk.StringVar(value=str(cfg["window_height"]))
        Field(control, th, self.width_var, width=7).pack(side="left")
        label(control, "×", th.f_body, T.MUTED).pack(side="left", padx=px(10))
        Field(control, th, self.height_var, width=7).pack(side="left")
        control, _ = self._setting_row(advanced, "额外 JVM 参数", "以空格分隔")
        self.jvm_var = tk.StringVar(value=cfg["jvm_args"])
        Field(control, th, self.jvm_var).pack(fill="x")

        account = self._settings_card(grid, "账号", "people", 2, 0)
        control, _ = self._setting_row(account, "账号管理", "离线、外置登录与微软账号")
        Button(control, th, "管理账号", self.manage_accounts, kind="secondary", icon="people", height=34,
               padx=14).pack(side="left")
        control, text = self._setting_row(account, "微软登录 Client ID", "在 Azure 注册的应用 ID")
        self.client_id_var = tk.StringVar(value=cfg.get("msa_client_id", ""))
        guide = label(text, "如何获取？", th.f_small, T.ACCENT, cursor="hand2")
        guide.pack(side="right")
        guide.bind("<Button-1>", lambda e: webbrowser.open(acc.MSA_APP_GUIDE))
        Field(control, th, self.client_id_var).pack(fill="x")

        style = self._settings_card(grid, "个性化", "tune", 2, 1)
        control, _ = self._setting_row(style, "启动游戏后")
        self.after_launch_var = tk.StringVar(value=dict(AFTER_LAUNCH).get(cfg.get("after_launch"), AFTER_LAUNCH[0][1]))
        ttk.Combobox(control, textvariable=self.after_launch_var, values=[v for _, v in AFTER_LAUNCH],
                     state="readonly", font=th.f_body).pack(fill="x")
        control, _ = self._setting_row(style, "主页背景图片", "支持 PNG、JPG、WebP")
        self.background_var = tk.StringVar(value=cfg.get("background", ""))
        Button(control, th, "清除", lambda: self._set_background(""), kind="secondary", height=34,
               padx=12).pack(side="right")
        Button(control, th, "浏览", self._browse_background, kind="secondary", height=34, padx=12).pack(
            side="right", padx=(0, px(8)))
        Field(control, th, self.background_var).pack(side="left", fill="x", expand=True, padx=(0, px(8)))
        self.banner.set_image(cfg.get("background", ""))

    def _browse_background(self):
        path = filedialog.askopenfilename(title="选择背景图片", filetypes=[
            ("图片", "*.png *.jpg *.jpeg *.webp *.bmp *.gif"), ("所有文件", "*.*")])
        if path:
            self._set_background(os.path.normpath(path))

    def _set_background(self, path):
        self.background_var.set(path)
        self.save_settings()

    # ------------------------------------------------------------------ 设置读写

    def save_settings(self):
        try:
            width = int(self.width_var.get())
            height = int(self.height_var.get())
        except ValueError:
            self.show_page("settings")
            self.dialog("设置有误", "游戏窗口的宽度和高度必须是整数。", "error")
            return False

        java_sel = self.java_var.get().strip()
        source_label = self.source_var.get()
        background = self.background_var.get().strip()
        if background != self.cfg.get("background"):
            self.banner.set_image(background)
        self.cfg.update({
            "username": self.username_var.get().strip(),
            "accounts": self.accounts,
            "account_index": self.account_index,
            "msa_client_id": self.client_id_var.get().strip(),
            "after_launch": next((k for k, v in AFTER_LAUNCH if v == self.after_launch_var.get()), "keep"),
            "background": background,
            "minecraft_dir": self.mc_dir_var.get().strip() or config_mod.DEFAULTS["minecraft_dir"],
            "java_path": "" if java_sel in ("", AUTO_JAVA) else self.java_map.get(java_sel, java_sel),
            "max_memory": int(self.mem_var.get()),
            "window_width": width,
            "window_height": height,
            "download_source": next((k for k, v in SOURCES if v == source_label), "auto"),
            "download_threads": int(self.threads_var.get()),
            "version_isolation": bool(self.isolation_var.get()),
            "jvm_args": self.jvm_var.get().strip(),
        })
        config_mod.save_config(self.cfg)
        return True

    def _browse_mc_dir(self):
        path = filedialog.askdirectory(initialdir=self.mc_dir_var.get() or os.getcwd())
        if path:
            self.mc_dir_var.set(os.path.normpath(path))
            self.save_settings()
            self.refresh_installed()

    def _browse_java(self):
        types = [("Java", "javaw.exe java.exe"), ("所有文件", "*.*")] if os.name == "nt" else [("所有文件", "*")]
        path = filedialog.askopenfilename(title="选择 java 可执行文件", filetypes=types)
        if path:
            self.java_var.set(os.path.normpath(path))

    def _runtime_dir(self):
        return os.path.join(self.mc_dir_var.get().strip(), "runtime")

    def _detect_java_background(self, runtime_dir):
        self.ui(self._on_java_found, javautil.find_java([runtime_dir]), False)

    def detect_java(self):
        runtime_dir = self._runtime_dir()
        self.run_task("检测 Java", lambda: javautil.find_java([runtime_dir]), lambda r: self._on_java_found(r, True))

    def _on_java_found(self, javas, verbose):
        self.java_map = {"Java {}  —  {}".format(v, p): p for p, v in javas}
        self.java_box["values"] = [AUTO_JAVA] + list(self.java_map)
        if verbose:
            self.toast("检测到 {} 个 Java".format(len(javas)))

    # ------------------------------------------------------------------ 启动游戏

    def refresh_installed(self, select=None):
        self.cfg["minecraft_dir"] = self.mc_dir_var.get().strip() or self.cfg["minecraft_dir"]
        versions = GameLauncher(self.cfg).installed_versions()
        self.installed = set(versions)
        self.version_box["values"] = versions
        target = select or self.version_var.get() or self.cfg.get("last_version")
        if target in self.installed:
            self.version_var.set(target)
        else:
            self.version_var.set(versions[0] if versions else "")
        self._update_hero()

    def launch(self, server=None):
        version = self.version_var.get()
        if not version:
            self.show_page("download")
            self.toast("请先安装一个游戏版本", "warn")
            return
        account = self.current_account()
        if not account:
            self.manage_accounts()
            return
        if self.game_proc and self.game_proc.poll() is None:
            if not self.dialog("游戏正在运行", "已有一个游戏实例在运行，确定要再启动一个吗？", "warn",
                               [("再启动一个", True, "primary"), ("取消", False, "secondary")]):
                return
        self.cfg["last_version"] = version
        self._append_log("─" * 48, "launcher")
        if server:
            self._append_log("启动后将自动进入联机房间 {}".format(server), "launcher")
        if not self.save_settings():
            return
        launcher = self.make_launcher()
        ctx = {"version": version, "game_dir": launcher.game_dir_for(version), "since": time.time()}
        cfg = dict(self.cfg)

        def job():
            auth, changed = acc.prepare_launch(account, cfg, launcher.dl, TOOLS_DIR, self.log)
            if changed:
                self.ui(self._accounts_changed)
            return launcher.launch(version, server=server, auth=auth)

        self.run_task("启动 " + version, job, lambda proc: self._on_game_started(proc, ctx))

    def _on_game_started(self, proc, ctx):
        self.game_proc = proc
        self.running_version = ctx["version"]
        self.status.set("游戏运行中")
        self.toast("游戏已启动")
        threading.Thread(target=self._watch_game, args=(proc, ctx), daemon=True).start()
        mode = self.cfg.get("after_launch")
        if mode == "minimize":
            self.root.iconify()
        elif mode == "hide":
            self.hidden_for_game = True
            self.root.withdraw()

    def _watch_game(self, proc, ctx):
        parser = LogParser()
        recent = collections.deque(maxlen=4000)
        for raw in proc.stdout:
            for line in parser.feed(_decode(raw).rstrip("\r\n")):
                recent.append(line)
                self.ui(self._append_log, line)
        code = proc.wait()
        try:
            result = crash.analyze(list(recent), ctx["game_dir"], ctx["since"], code)
        except Exception as e:
            result = {"reasons": [], "detail": "崩溃分析失败：{}".format(e), "files": []}
        self.ui(self._on_game_exit, code, result)

    def _on_game_exit(self, code, result):
        if self.hidden_for_game:
            self.hidden_for_game = False
            self.root.deiconify()
        self._append_log("游戏已退出（退出码 {}）".format(code), "launcher" if code == 0 else "error")
        if not self.busy:
            self.status.set("就绪")
        reasons = [text for kind, text in result["reasons"] if code != 0 or kind == crash.MOD]
        if code == 0 and not reasons:
            return
        if self.root.state() == "iconic":
            self.root.deiconify()
        for text in reasons:
            self._append_log("[崩溃分析] " + text, "warn")
        if reasons:
            message = "可能的原因：\n\n" + "\n".join("•  " + r for r in reasons[:8])
        else:
            message = "没有找到已知的崩溃原因（退出码 {}）。".format(code)
            if result["detail"]:
                message += "\n\n错误信息：" + result["detail"]
            message += "\n\n可以在「游戏日志」中查看完整输出。"
        buttons = [("确定", None, "primary")]
        if result["files"]:
            buttons.append(("查看崩溃报告", "report", "secondary"))
        title = "游戏崩溃了" if code != 0 else "模组加载失败"
        if self.dialog(title, message, "error", buttons) == "report" and os.name == "nt":
            os.startfile(result["files"][0])

    def manage_version(self):
        version = self.version_var.get()
        if not version:
            self.show_page("download")
            self.toast("请先安装一个游戏版本", "warn")
            return
        VersionDialog(self, version)
        self._update_hero()

    def open_game_dir(self):
        version = self.version_var.get()
        launcher = GameLauncher(self.cfg)
        path = launcher.game_dir_for(version) if version else launcher.mc_dir
        os.makedirs(path, exist_ok=True)
        if os.name == "nt":
            os.startfile(path)
        else:
            self.dialog("游戏目录", path)

    def _clear_log(self):
        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", "end")
        self.log_text.configure(state="disabled")

    # ------------------------------------------------------------------ 下载

    def load_manifest(self):
        launcher = self.make_launcher()
        launcher.manifest = None
        self.tree_hint.configure(text="正在获取版本列表…")
        self.tree_hint.place(relx=0.5, rely=0.5, anchor="center")
        self.run_task("获取版本列表", launcher.get_manifest, self._on_manifest)

    def _on_manifest(self, manifest):
        self.manifest = manifest
        self._fill_version_tree()
        self.status.set("共 {} 个版本".format(len(manifest["versions"])))

    def _fill_version_tree(self):
        self.tree.delete(*self.tree.get_children())
        if not self.manifest:
            return
        shown = set()
        if self.show_release.get():
            shown.add("release")
        if self.show_snapshot.get():
            shown.add("snapshot")
        if self.show_old.get():
            shown.update(("old_beta", "old_alpha"))
        keyword = self.search_var.get().strip().lower()
        count = 0
        for v in self.manifest["versions"]:
            if v["type"] not in shown or (keyword and keyword not in v["id"].lower()):
                continue
            state = "●  已安装" if v["id"] in self.installed else ""
            self.tree.insert("", "end", iid=v["id"], values=tuple("    " + s for s in (
                v["id"], VERSION_TYPES.get(v["type"], v["type"]), v["releaseTime"][:10], state)))
            count += 1
        if count:
            self.tree_hint.place_forget()
        else:
            self.tree_hint.configure(text="没有符合条件的版本")
            self.tree_hint.place(relx=0.5, rely=0.5, anchor="center")
        self._on_tree_select()

    def install_selected(self):
        selection = self.tree.selection()
        if not selection:
            self.toast("请先在列表中选择一个版本", "warn")
            return
        version = selection[0]
        if self.busy:
            self.toast("当前有任务正在进行，请稍候", "warn")
            return
        choice = ask_install_options(self, version)
        if choice is None:
            return
        loader, item, optifine = choice
        launcher = self.make_launcher()

        def job():
            installer = LoaderInstaller(launcher)
            launcher.prepare(version)
            installed = version
            if loader:
                installed = installer.install(loader, version, item)
                launcher.prepare(installed)
            if optifine:
                installed = installer.install_optifine(version, optifine, forge_version=installed if loader else None)
                launcher.prepare(installed)
            return installed

        parts = [LOADER_NAMES[loader]] if loader else []
        if optifine:
            parts.append("OptiFine")
        name = " + ".join(parts + [version])
        self._append_log("─" * 48, "launcher")
        self.run_task("安装 " + name, job, self._on_installed)

    def _on_installed(self, version):
        self._append_log("{} 安装完成".format(version), "success")
        self.refresh_installed(select=version)
        self._fill_version_tree()
        self.show_page("launch")
        self.toast("{} 安装完成".format(version))

    def on_close(self):
        mp = self.multiplayer_page
        if mp.in_room() and not self.dialog("正在联机", "关闭启动器会同时关闭联机房间，确定要退出吗？", "warn",
                                            [("退出", True, "primary"), ("取消", False, "secondary")]):
            return
        self.save_settings()
        if mp.phase != "init":
            mp.shutdown()
        self.root.destroy()


def main():
    if os.name == "nt":
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass
    root = tk.Tk()
    App(root)
    root.mainloop()
