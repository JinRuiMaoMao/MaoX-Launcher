import base64
import math
import os
import shutil
import tkinter as tk
import webbrowser
from concurrent.futures import ThreadPoolExecutor
from tkinter import ttk

from .. import instance
from ..core import LOADER_NAMES
from ..mods import (KIND_FOLDERS, KIND_NAMES, KINDS, MOD_LOADERS, SORTS, CurseForgeClient, ModrinthClient,
                    apply_update, check_updates, list_local_files, list_local_mods, set_mod_enabled)
from . import theme as T
from .imaging import icon_png
from .widgets import Button, Chip, Field, ScrollFrame, Switch, label, round_rect

PAGE_SIZE = 20
SOURCES = (("modrinth", "Modrinth"), ("curseforge", "CurseForge"))


def format_count(n):
    if n >= 100000000:
        return "{:.1f} 亿".format(n / 100000000)
    if n >= 10000:
        return "{:.1f} 万".format(n / 10000)
    return str(n)


class ModsPage:
    def __init__(self, app, page):
        self.app = app
        self.theme = app.theme
        self.px = app.px
        self.ctx = None
        self.clients = {}
        self.kind = "mod"
        self.source = SOURCES[0][0]
        self.installed_projects = {key: set() for key, _ in SOURCES}
        self.local_items = []
        self.updates = {}
        self.worlds = []
        self.offset = 0
        self.total = 0
        self.search_gen = 0
        self._debounce = None
        self.icons = {}
        self.icon_pool = ThreadPoolExecutor(max_workers=6)
        self.card_buttons = {}
        self.installing = set()
        self._build(page)

    # ------------------------------------------------------------------ 布局

    def _build(self, page):
        px, th = self.px, self.theme
        header = self.app._page_header(page, "资源", "模组、资源包、光影、数据包和整合包")
        target = tk.Frame(header, bg=T.BG)
        target.pack(side="right", anchor="s")
        label(target, "目标版本", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
        self.version_var = tk.StringVar()
        self.version_box = ttk.Combobox(target, textvariable=self.version_var, state="readonly", width=32,
                                        font=th.f_body)
        self.version_box.pack()
        self.version_box.bind("<<ComboboxSelected>>", lambda e: self.set_target())
        self.world_col = tk.Frame(header, bg=T.BG)
        label(self.world_col, "目标存档", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
        self.world_var = tk.StringVar()
        self.world_box = ttk.Combobox(self.world_col, textvariable=self.world_var, state="readonly", width=18,
                                      font=th.f_body)
        self.world_box.pack()
        self.world_box.bind("<<ComboboxSelected>>", lambda e: self._world_changed())

        kinds = tk.Frame(page, bg=T.BG)
        kinds.pack(fill="x", pady=(0, px(12)))
        self.kind_var = tk.StringVar(value=self.kind)
        for i, (key, text, _) in enumerate(KINDS):
            Chip(kinds, th, text, self.kind_var, self.set_kind, value=key).pack(side="left", padx=(px(8) if i else 0, 0))
        Button(kinds, th, "导入整合包文件", self.import_modpack, kind="secondary", icon="folder", height=34,
               padx=12).pack(side="right")

        toolbar = tk.Frame(page, bg=T.BG)
        toolbar.pack(fill="x", pady=(0, px(14)))
        self.tab_var = tk.StringVar(value=self.source)
        self.source_chips = []
        for i, (key, text) in enumerate(SOURCES):
            chip = Chip(toolbar, th, text, self.tab_var, self.show_tab, value=key)
            chip.pack(side="left", padx=(px(8) if i else 0, 0))
            self.source_chips.append(chip)
        self.local_chip = Chip(toolbar, th, "已安装", self.tab_var, self.show_tab, value="local")
        self.local_chip.pack(side="left", padx=(px(8), 0))
        self.info_loader = label(toolbar, "", th.f_body_bold, T.ACCENT)
        self.info_loader.pack(side="left", padx=(px(20), 0))
        self.info_rest = label(toolbar, "", th.f_body, T.MUTED)
        self.info_rest.pack(side="left", padx=(px(6), 0))

        self.browse_tools = tk.Frame(toolbar, bg=T.BG)
        self.search_var = tk.StringVar()
        search = Field(self.browse_tools, th, self.search_var, width=22, icon="search")
        search.pack(side="right")
        search.entry.bind("<Return>", lambda e: self.search(reset=True))
        self.search_var.trace_add("write", lambda *_: self._schedule_search())
        self.sort_var = tk.StringVar(value=SORTS[0][1])
        sort_box = ttk.Combobox(self.browse_tools, textvariable=self.sort_var, values=[s[1] for s in SORTS],
                                state="readonly", width=9, font=th.f_body)
        sort_box.pack(side="right", padx=(0, px(8)))
        sort_box.bind("<<ComboboxSelected>>", lambda e: self.search(reset=True))

        self.local_tools = tk.Frame(toolbar, bg=T.BG)
        Button(self.local_tools, th, "", self.reload_local, kind="secondary", icon="refresh",
               height=34, padx=10).pack(side="right")
        Button(self.local_tools, th, "打开文件夹", self.open_folder, kind="secondary", icon="folder",
               height=34, padx=12).pack(side="right", padx=(0, px(8)))
        self.update_btn = Button(self.local_tools, th, "检查更新", self.check_updates, kind="secondary",
                                 icon="download", height=34, padx=12)
        self.update_all_btn = Button(self.local_tools, th, "全部更新", self.update_all, icon="download",
                                     height=34, padx=12)

        stack = tk.Frame(page, bg=T.BG)
        stack.pack(fill="both", expand=True)
        stack.rowconfigure(0, weight=1)
        stack.columnconfigure(0, weight=1)
        self.browse_view = ScrollFrame(stack, th)
        self.local_view = ScrollFrame(stack, th)
        self.empty_view = tk.Frame(stack, bg=T.BG)
        for view in (self.browse_view, self.local_view, self.empty_view):
            view.grid(row=0, column=0, sticky="nsew")

        center = tk.Frame(self.empty_view, bg=T.BG)
        center.place(relx=0.5, rely=0.42, anchor="center")
        label(center, th.icon("puzzle"), th.f_icon_big, T.DIM).pack()
        self.empty_title = label(center, "", th.f_h3)
        self.empty_title.pack(pady=(px(12), px(4)))
        self.empty_text = label(center, "", th.f_body, T.MUTED, justify="center")
        self.empty_text.pack()
        self.empty_btn = Button(center, th, "前往下载页", lambda: self.app.show_page("download"), icon="download",
                                height=38, padx=18)
        self.empty_btn.pack(pady=(px(18), 0))

    # ------------------------------------------------------------------ 目标与类型

    def on_show(self):
        launcher = self.app.make_launcher()
        versions = launcher.installed_versions()
        modded = [v for v in versions if launcher.detect_loader(v)[0] in MOD_LOADERS]
        ordered = modded + [v for v in versions if v not in modded]
        self.version_box["values"] = ordered
        current = self.version_var.get()
        if current not in versions:
            preferred = self.app.version_var.get()
            current = preferred if preferred in modded else (modded[0] if modded else (ordered[0] if ordered else ""))
            self.version_var.set(current)
        if not self.ctx or self.ctx["version"] != current or self.ctx["game_dir"] != self._game_dir(launcher, current):
            self.set_target()

    @staticmethod
    def _game_dir(launcher, version):
        return launcher.game_dir_for(version) if version else ""

    def set_target(self):
        launcher = self.app.make_launcher()
        version = self.version_var.get()
        loader, loader_version, game = launcher.detect_loader(version) if version else (None, None, None)
        self.ctx = {"version": version, "loader": loader, "loader_version": loader_version, "game": game,
                    "game_dir": self._game_dir(launcher, version)}
        self.clients = {"modrinth": ModrinthClient(launcher.dl), "curseforge": CurseForgeClient(launcher.dl)}
        self.worlds = instance.list_worlds(self.ctx["game_dir"]) if version else []
        names = [w["name"] for w in self.worlds]
        self.world_box["values"] = names
        if self.world_var.get() not in names:
            self.world_var.set(names[0] if names else "")
        self._reset()

    def set_kind(self):
        self.kind = self.kind_var.get()
        self._reset()

    def _world_changed(self):
        self.installed_projects = {key: set() for key, _ in SOURCES}
        self.reload_local()
        self.search(reset=True)

    def _reset(self):
        self.installed_projects = {key: set() for key, _ in SOURCES}
        self.local_items = []
        self.updates = {}
        self.local_chip.set_text("已安装")
        if self.kind == "modpack" and self.tab_var.get() == "local":
            self.tab_var.set(self.source)
        self._update_info()
        self.show_tab()
        if self.available():
            if self.kind != "modpack":
                self.reload_local()
            self.search(reset=True)

    def target_dir(self):
        ctx, kind = self.ctx, self.kind
        if kind == "datapack":
            world = self.current_world()
            return os.path.join(world["path"], "datapacks") if world else ""
        folder = KIND_FOLDERS.get(kind)
        return os.path.join(ctx["game_dir"], folder) if ctx and ctx["version"] and folder else ""

    def current_world(self):
        return next((w for w in self.worlds if w["name"] == self.world_var.get()), None)

    def search_version(self):
        return None if self.kind == "modpack" else self.ctx["game"]

    def available(self):
        if self.kind == "modpack":
            return True
        if not self.ctx or not self.ctx["version"]:
            return False
        if self.kind == "mod":
            return self.ctx["loader"] in MOD_LOADERS
        if self.kind == "datapack":
            return bool(self.worlds)
        return True

    def _update_info(self):
        ctx, kind = self.ctx, self.kind
        self.world_col.pack_forget()
        if kind == "modpack":
            self.local_chip.pack_forget()
        else:
            self.local_chip.pack(side="left", padx=(self.px(8), 0), after=self.source_chips[-1])
        if kind == "modpack":
            self.info_loader.configure(text="")
            self.info_rest.configure(text="安装整合包会创建一个新的游戏版本")
        elif ctx and ctx["version"] and self.available():
            if kind == "mod":
                self.info_loader.configure(text="{} {}".format(LOADER_NAMES[ctx["loader"]], ctx["loader_version"] or ""))
                self.info_rest.configure(text="·  Minecraft {}".format(ctx["game"]))
            elif kind == "datapack":
                self.info_loader.configure(text="Minecraft {}".format(ctx["game"]))
                self.info_rest.configure(text="")
                self.world_col.pack(side="right", anchor="s", padx=(0, self.px(14)))
            else:
                self.info_loader.configure(text="Minecraft {}".format(ctx["game"]))
                self.info_rest.configure(text="")
        else:
            self.info_loader.configure(text="")
            self.info_rest.configure(text="")
            self._empty_message()

    def _empty_message(self):
        ctx, kind = self.ctx, self.kind
        version = ctx["version"] if ctx else ""
        self.empty_btn.pack(pady=(self.px(18), 0))
        if not version:
            title, text = "还没有安装任何版本", "先在「下载」页安装一个游戏版本。"
        elif kind == "datapack":
            title = "{} 还没有存档".format(version)
            text = "数据包需要安装到存档中，先进入游戏创建一个世界。"
            self.empty_btn.pack_forget()
        else:
            hint = "在「下载」页安装版本时选择 Forge、NeoForge、Fabric 或 Quilt，\n再回到这里为它下载模组。"
            if ctx["loader"] == "optifine":
                title = "{} 是 OptiFine 独立版本，无法加载模组".format(version)
                text = hint + "\n想同时使用 OptiFine 和模组，可以安装 Forge 并勾选 OptiFine。"
            else:
                title, text = "{} 是原版，无法加载模组".format(version), hint
        self.empty_title.configure(text=title)
        self.empty_text.configure(text=text)

    def show_tab(self):
        if not self.available():
            self.empty_view.tkraise()
            self.browse_tools.pack_forget()
            self.local_tools.pack_forget()
            return
        tab = self.tab_var.get()
        browse = tab != "local"
        (self.browse_view if browse else self.local_view).tkraise()
        (self.local_tools if browse else self.browse_tools).pack_forget()
        # 先于左侧标签打包，窗口较窄时优先压缩说明文字而不是按钮
        (self.browse_tools if browse else self.local_tools).pack(side="right", before=self.source_chips[0])
        if browse:
            self.info_rest.pack(side="left", padx=(self.px(6), 0), after=self.info_loader)
        else:
            self.info_rest.pack_forget()
        if self.kind == "mod":
            self.update_btn.pack(side="right", padx=(0, self.px(8)))
        else:
            self.update_btn.pack_forget()
            self.update_all_btn.pack_forget()
        if browse and tab != self.source:
            self.source = tab
            self.search(reset=True)

    # ------------------------------------------------------------------ 浏览与搜索

    def _schedule_search(self):
        if self._debounce:
            self.app.root.after_cancel(self._debounce)
        self._debounce = self.app.root.after(450, lambda: self.search(reset=True))

    def _status(self, text, color=T.MUTED):
        frame = tk.Frame(self.browse_view.inner, bg=T.BG)
        frame.pack(fill="x", pady=self.px(30))
        label(frame, text, self.theme.f_body, color).pack()
        return frame

    def search(self, reset=False):
        if not self.available():
            return
        self._debounce = None
        self.search_gen += 1
        gen = self.search_gen
        if reset:
            self.offset = 0
            self.card_buttons = {}
            self.browse_view.clear()
        source, kind = self.source, self.kind
        loading = self._status("正在搜索 {}…".format(dict(SOURCES)[source]))
        ctx, client = self.ctx, self.clients[source]
        query = self.search_var.get().strip()
        index = next((k for k, v in SORTS if v == self.sort_var.get()), "relevance")
        offset, game = self.offset, self.search_version()

        def done(result):
            if gen != self.search_gen:
                return
            loading.destroy()
            hits, total = result
            self.total = total
            if not hits and offset == 0:
                self._status("没有找到适用于 {} 的{}".format(game, KIND_NAMES[kind]) if game
                             else "没有找到{}".format(KIND_NAMES[kind]))
                return
            for hit in hits:
                self._card(hit, source)
            self.offset = offset + len(hits)
            if self.offset < total:
                more = tk.Frame(self.browse_view.inner, bg=T.BG)
                more.pack(fill="x", pady=self.px(12))
                Button(more, self.theme, "加载更多（{} / {}）".format(self.offset, total),
                       lambda: (more.destroy(), self.search()), kind="secondary", height=36, padx=18).pack()

        def failed(error):
            if gen == self.search_gen:
                loading.destroy()
                self._status("搜索失败：{}".format(error), T.ERROR)

        loader = ctx["loader"] if ctx else None
        self.app.spawn(lambda: client.search(query, game, loader, offset, PAGE_SIZE, index, kind), done, failed)

    def _card(self, hit, source):
        px, th = self.px, self.theme
        card = tk.Frame(self.browse_view.inner, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        card.pack(fill="x", pady=(0, px(10)))
        size = px(52)
        icon = tk.Canvas(card, width=size, height=size, bg=T.CARD, highlightthickness=0)
        icon.pack(side="left", padx=(px(16), px(14)), pady=px(14), anchor="n")
        round_rect(icon, 0, 0, size, size, px(10), fill=T.ACCENT_DIM, outline="")
        icon.create_text(size / 2, size / 2, text=(hit.get("title") or "?")[0].upper(), font=th.f_h3, fill=T.ACCENT)
        if hit.get("icon_url"):
            self._load_icon(hit["icon_url"], icon, size)

        pid = hit["id"]
        installed = pid in self.installed_projects[source]
        btn = Button(card, th, "已安装" if installed else "安装", lambda: self.install(hit, source, btn),
                     icon="check" if installed else "download", height=34, width=96)
        btn.pack(side="right", padx=px(16))
        if installed or pid in self.installing:
            btn.set_enabled(False)
            if pid in self.installing:
                btn.set_text("安装中…")
        self.card_buttons[pid] = btn

        text = tk.Frame(card, bg=T.CARD)
        text.pack(side="left", fill="both", expand=True, pady=px(12))
        title_row = tk.Frame(text, bg=T.CARD)
        title_row.pack(fill="x")
        title = label(title_row, hit.get("title", ""), th.f_body_bold)
        title.pack(side="left")
        if hit.get("url"):
            title.configure(cursor="hand2")
            title.bind("<Enter>", lambda e: title.configure(fg=T.ACCENT))
            title.bind("<Leave>", lambda e: title.configure(fg=T.TEXT))
            title.bind("<Button-1>", lambda e: webbrowser.open(hit["url"]))
        if hit.get("author"):
            label(title_row, "by " + hit["author"], th.f_small, T.DIM).pack(side="left", padx=(px(8), 0))
        desc = label(text, hit.get("description", ""), th.f_body, T.MUTED, justify="left", anchor="w")
        desc.pack(fill="x", pady=(px(2), px(4)))
        card.bind("<Configure>", lambda e: desc.configure(wraplength=max(px(200), e.width - px(260))))
        meta = tk.Frame(text, bg=T.CARD)
        meta.pack(fill="x")
        label(meta, th.icon("download"), th.f_icon, T.DIM).pack(side="left")
        label(meta, format_count(hit.get("downloads", 0)), th.f_small, T.DIM).pack(side="left", padx=(px(4), px(14)))
        categories = hit.get("categories", [])[:4]
        if categories:
            label(meta, "  ·  ".join(categories), th.f_small, T.DIM).pack(side="left")

    def _load_icon(self, url, canvas, size):
        if url in self.icons:
            self._apply_icon(canvas, self.icons[url])
            return
        dl, radius = self.clients[self.source].dl, self.px(10)

        def fetch():
            data = dl.fetch(url)
            return icon_png(data, size, T.CARD, radius), data

        future = self.icon_pool.submit(fetch)
        future.add_done_callback(lambda f: self.app.ui(self._icon_ready, url, f, canvas, size))

    def _icon_ready(self, url, future, canvas, size):
        image = None
        try:
            png, data = future.result()
            if png:
                image = tk.PhotoImage(data=base64.b64encode(png))
            elif data[:8] == b"\x89PNG\r\n\x1a\n" or data[:4] == b"GIF8":
                image = tk.PhotoImage(data=base64.b64encode(data))
                factor = math.ceil(max(image.width(), image.height()) / size)
                if factor > 1:
                    image = image.subsample(factor)
        except Exception:
            image = None
        self.icons[url] = image
        self._apply_icon(canvas, image)

    @staticmethod
    def _apply_icon(canvas, image):
        if image is not None and canvas.winfo_exists():
            w = int(canvas.cget("width"))
            canvas.delete("all")
            canvas.create_image(w / 2, w / 2, image=image)

    def install(self, hit, source, btn):
        if self.kind == "modpack":
            from .modpack_dialog import install_modpack_from_hit
            install_modpack_from_hit(self.app, self.clients[source], hit)
            return
        ctx, client, projects, kind = self.ctx, self.clients[source], self.installed_projects[source], self.kind
        target = self.target_dir()
        pid = hit["id"]
        self.installing.add(pid)
        btn.set_enabled(False)
        btn.set_text("安装中…")
        self.app.status.set("正在安装 {}…".format(hit["title"]))

        def job():
            return client.install(pid, ctx["game"], ctx["loader"], target, projects, log=self.app.log, kind=kind)

        def done(files):
            self.installing.discard(pid)
            self.app.status.set("就绪")
            extra = len(files) - 1
            message = "已安装 {}{}".format(hit["title"], "（含 {} 个前置）".format(extra) if extra > 0 else "")
            if kind == "shader" and ctx["loader"] != "optifine":
                message += "，需要 OptiFine 或 Iris 才能使用"
            self.app.toast(message)
            self._mark_installed()
            self.reload_local(identify=False)

        def failed(error):
            self.installing.discard(pid)
            self.app.status.set("就绪")
            if btn.winfo_exists():
                btn.set_text("安装")
                btn.set_enabled(True)
            self.app.dialog("安装 {} 失败".format(hit["title"]), str(error), "error")

        self.app.spawn(job, done, failed)

    def _mark_installed(self):
        projects = self.installed_projects[self.source]
        for pid, btn in self.card_buttons.items():
            if pid in projects and btn.winfo_exists() and pid not in self.installing:
                btn.set_text("已安装")
                btn.set_enabled(False)

    # ------------------------------------------------------------------ 已安装

    def reload_local(self, identify=True):
        if not self.available() or self.kind == "modpack":
            return
        ctx, kind, clients, folder = self.ctx, self.kind, dict(self.clients), self.target_dir()

        def done(items):
            if ctx is not self.ctx or kind != self.kind:
                return
            self.local_items = items
            self.local_chip.set_text("已安装 · {}".format(len(items)))
            self._render_local()
            if identify:
                files = [i for i in items if not i.get("is_dir")]
                for key, client in clients.items():
                    self.app.spawn(lambda c=client: c.installed_ids(files), lambda ids, k=key: identified(k, ids))

        def identified(source, projects):
            if ctx is self.ctx and kind == self.kind:
                self.installed_projects[source].update(projects)
                self._mark_installed()

        self.app.spawn(lambda: list_local_mods(folder) if kind == "mod" else list_local_files(folder), done)

    def _render_local(self):
        px, th = self.px, self.theme
        view = self.local_view
        view.clear()
        if not self.local_items:
            frame = tk.Frame(view.inner, bg=T.BG)
            frame.pack(fill="x", pady=px(30))
            label(frame, "还没有安装{}，去 Modrinth 或 CurseForge 装一些吧".format(KIND_NAMES[self.kind]), th.f_body,
                  T.MUTED).pack()
            return
        for item in self.local_items:
            row = tk.Frame(view.inner, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
            row.pack(fill="x", pady=(0, px(8)))
            if self.kind == "mod":
                enabled = tk.BooleanVar(value=item["enabled"])
                Switch(row, th, enabled, command=lambda m=item, v=enabled: self.toggle(m, v)).pack(
                    side="left", padx=(px(18), px(16)), pady=px(14))
            else:
                label(row, th.icon("folder" if item.get("is_dir") else "puzzle"), th.f_icon, T.ACCENT).pack(
                    side="left", padx=(px(20), px(16)), pady=px(14))
            text = tk.Frame(row, bg=T.CARD)
            text.pack(side="left", fill="x", expand=True, pady=px(8))
            line = tk.Frame(text, bg=T.CARD)
            line.pack(fill="x")
            label(line, item["name"], th.f_body_bold, T.TEXT if item["enabled"] else T.DIM).pack(side="left")
            if item["version"]:
                label(line, item["version"], th.f_small, T.MUTED).pack(side="left", padx=(px(10), 0))
            update = self.updates.get(item["path"])
            if update:
                label(line, "可更新 → {}".format(update["version"]), th.f_small_bold, T.SUCCESS).pack(
                    side="left", padx=(px(10), 0))
            label(text, item["filename"], th.f_small, T.DIM).pack(anchor="w")
            Button(row, th, "", lambda m=item: self.delete(m), kind="ghost", icon="delete", height=32,
                   padx=10).pack(side="right", padx=px(12))
            if update:
                Button(row, th, "更新", lambda u=update: self.update_one(u), kind="secondary", icon="download",
                       height=32, padx=12).pack(side="right")

    def toggle(self, mod, var):
        try:
            mod["path"] = set_mod_enabled(mod, var.get())
        except OSError as e:
            var.set(not var.get())
            self.app.dialog("操作失败", "无法重命名模组文件（游戏是否正在运行？）：{}".format(e), "error")
            return
        self.updates = {}
        self.update_all_btn.pack_forget()
        self.reload_local(identify=False)

    def delete(self, item):
        if not self.app.dialog("删除", "确定要删除 {} 吗？此操作无法撤销。".format(item["filename"]), "warn",
                               [("删除", True, "primary"), ("取消", False, "secondary")]):
            return
        try:
            if item.get("is_dir"):
                shutil.rmtree(item["path"])
            else:
                os.remove(item["path"])
        except OSError as e:
            self.app.dialog("删除失败", str(e), "error")
            return
        self.updates.pop(item["path"], None)
        self.installed_projects = {key: set() for key, _ in SOURCES}
        self.reload_local()
        self.search(reset=True)

    def open_folder(self):
        folder = self.target_dir()
        if folder:
            os.makedirs(folder, exist_ok=True)
            if os.name == "nt":
                os.startfile(folder)

    # ------------------------------------------------------------------ 模组更新

    def check_updates(self):
        mods = [m for m in self.local_items if self.kind == "mod"]
        if not mods:
            self.app.toast("没有可以检查的模组", "warn")
            return
        ctx, clients = self.ctx, dict(self.clients)
        self.update_btn.set_enabled(False)
        self.update_btn.set_text("检查中…")

        def done(updates):
            self.update_btn.set_enabled(True)
            self.update_btn.set_text("检查更新")
            if ctx is not self.ctx:
                return
            self.updates = {u["mod"]["path"]: u for u in updates}
            self._render_local()
            if updates:
                self.update_all_btn.set_text("全部更新（{}）".format(len(updates)))
                self.update_all_btn.pack(side="right", padx=(0, self.px(8)))
                self.app.toast("有 {} 个模组可以更新".format(len(updates)))
            else:
                self.update_all_btn.pack_forget()
                self.app.toast("所有模组都是最新版本")

        def failed(error):
            self.update_btn.set_enabled(True)
            self.update_btn.set_text("检查更新")
            self.app.dialog("检查更新失败", str(error), "error")

        self.app.spawn(lambda: check_updates(clients, mods, ctx["game"], ctx["loader"]), done, failed)

    def update_one(self, update):
        self._run_updates([update])

    def update_all(self):
        self._run_updates(list(self.updates.values()))

    def _run_updates(self, updates):
        dl = self.clients["modrinth"].dl

        def job():
            failed = []
            for i, update in enumerate(updates, 1):
                self.app.progress(i - 1, len(updates), "更新模组")
                try:
                    apply_update(dl, update)
                except Exception as e:
                    failed.append("{}：{}".format(update["mod"]["name"], e))
            return failed

        def done(failed):
            for u in updates:
                self.updates.pop(u["mod"]["path"], None)
            if self.updates:
                self.update_all_btn.set_text("全部更新（{}）".format(len(self.updates)))
            else:
                self.update_all_btn.pack_forget()
            self.reload_local(identify=False)
            if failed:
                self.app.dialog("部分模组更新失败", "\n".join(failed[:8]), "error")
            else:
                self.app.toast("已更新 {} 个模组".format(len(updates)))

        self.app.run_task("更新模组", job, done)

    # ------------------------------------------------------------------ 整合包

    def import_modpack(self):
        from .modpack_dialog import import_modpack_file
        import_modpack_file(self.app)
