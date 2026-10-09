import os
import time
import tkinter as tk
from tkinter import ttk

from .. import instance
from . import theme as T
from .widgets import Button, Field, ScrollFrame, Slider, Switch, ask_string, create_modal, label, present_modal

FOLLOW_GLOBAL = "跟随全局设置"
FOLDERS = (("游戏目录", ""), ("存档", "saves"), ("模组", "mods"), ("资源包", "resourcepacks"),
           ("光影包", "shaderpacks"), ("截图", "screenshots"))


def open_path(app, path):
    os.makedirs(path, exist_ok=True)
    if os.name == "nt":
        os.startfile(path)
    else:
        app.dialog("文件夹", path)


class VersionDialog:
    def __init__(self, app, version):
        self.app, self.version = app, version
        self.th, self.px = th, px = app.theme, app.px
        self.gl = app.make_launcher()
        self.settings = self.gl.version_settings(version)
        self.icons = []
        self.top = top = create_modal(app.root, "版本管理")

        head = tk.Frame(top, bg=T.BG)
        head.pack(fill="x", padx=px(28), pady=(px(24), px(14)))
        label(head, version, th.f_h3).pack(anchor="w")
        label(head, app._describe_version(version), th.f_body, T.MUTED).pack(anchor="w", pady=(px(2), 0))

        grid = tk.Frame(top, bg=T.BG)
        grid.pack(fill="x", padx=px(28))
        grid.columnconfigure(0, weight=3, uniform="c")
        grid.columnconfigure(1, weight=2, uniform="c")
        self._build_settings(self._card(grid, 0, "版本设置", "tune"))
        self._build_folders(self._card(grid, 1, "文件夹", "folder"))

        worlds = tk.Frame(top, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        worlds.pack(fill="x", padx=px(28), pady=(px(14), 0))
        whead = tk.Frame(worlds, bg=T.CARD)
        whead.pack(fill="x", padx=px(18), pady=(px(14), px(8)))
        label(whead, th.icon("game"), th.f_icon, T.ACCENT).pack(side="left")
        label(whead, "存档", th.f_body_bold).pack(side="left", padx=(px(8), 0))
        self.world_count = label(whead, "", th.f_body_bold, T.ACCENT)
        self.world_count.pack(side="left", padx=(px(6), 0))
        self.world_list = ScrollFrame(worlds, th, bg=T.CARD, scroll_style="Dark.Vertical.TScrollbar")
        self.world_list.configure(height=px(170))
        self.world_list.pack_propagate(False)
        self.world_list.pack(fill="x", padx=(px(18), px(8)), pady=(0, px(12)))

        bar = tk.Frame(top, bg=T.BG)
        bar.pack(fill="x", padx=px(28), pady=(px(18), px(22)))
        Button(bar, th, "完成", self.close, height=34, padx=22).pack(side="right")
        Button(bar, th, "导出整合包", self.export, kind="secondary", icon="download", height=34, padx=14).pack(
            side="right", padx=(0, px(8)))
        Button(bar, th, "删除", self.delete, kind="ghost", icon="delete", height=34, padx=12).pack(side="left")
        Button(bar, th, "重命名", self.rename, kind="secondary", height=34, padx=14).pack(side="left", padx=(px(8), 0))
        Button(bar, th, "复制", self.duplicate, kind="secondary", height=34, padx=14).pack(side="left", padx=(px(8), 0))

        top.bind("<Escape>", lambda e: self.close())
        top.protocol("WM_DELETE_WINDOW", self.close)
        self._load_worlds()
        present_modal(app.root, top, px(760))

    def _card(self, grid, col, title, icon):
        px, th = self.px, self.th
        card = tk.Frame(grid, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        card.grid(row=0, column=col, sticky="nsew", padx=(0, px(7)) if col == 0 else (px(7), 0))
        head = tk.Frame(card, bg=T.CARD)
        head.pack(fill="x", padx=px(18), pady=(px(14), px(10)))
        label(head, th.icon(icon), th.f_icon, T.ACCENT).pack(side="left")
        label(head, title, th.f_body_bold).pack(side="left", padx=(px(8), 0))
        inner = tk.Frame(card, bg=T.CARD)
        inner.pack(fill="both", expand=True, padx=px(18), pady=(0, px(14)))
        return inner

    # ------------------------------------------------------------------ 设置

    def _build_settings(self, parent):
        px, th, s, cfg = self.px, self.th, self.settings, self.app.cfg
        row = tk.Frame(parent, bg=T.CARD)
        row.pack(fill="x")
        label(row, "版本隔离", th.f_body).pack(side="left")
        label(row, "独立的存档、模组与设置", th.f_small, T.DIM).pack(side="left", padx=(px(8), 0))
        isolation = s.get("isolation")
        self.isolation_var = tk.BooleanVar(value=cfg["version_isolation"] if isolation is None else isolation)
        self.isolation_initial = self.isolation_var.get()
        Switch(row, th, self.isolation_var).pack(side="right")

        row = tk.Frame(parent, bg=T.CARD)
        row.pack(fill="x", pady=(px(12), 0))
        label(row, "单独设置内存与 Java", th.f_body).pack(side="left")
        self.custom_var = tk.BooleanVar(value=bool(s.get("custom")))
        Switch(row, th, self.custom_var, command=self._toggle_custom).pack(side="right")

        self.custom_box = tk.Frame(parent, bg=T.CARD)
        self.custom_box.pack(fill="x", pady=(px(10), 0))
        max_mem = max(2048, self.app.ram_mb // 512 * 512)
        self.mem_var = tk.IntVar(value=min(int(s.get("max_memory", cfg["max_memory"])), max_mem))
        mem_row = tk.Frame(self.custom_box, bg=T.CARD)
        mem_row.pack(fill="x")
        label(mem_row, "最大内存", th.f_small, T.MUTED).pack(side="left")
        mem_label = label(mem_row, "", th.f_small_bold, T.ACCENT)
        mem_label.pack(side="right")
        self.mem_var.trace_add("write", lambda *_: mem_label.configure(text="{} MB".format(self.mem_var.get())))
        mem_label.configure(text="{} MB".format(self.mem_var.get()))
        Slider(self.custom_box, th, self.mem_var, 512, max_mem, step=256, width=360).pack(fill="x", pady=(px(4), px(8)))
        label(self.custom_box, "Java", th.f_small, T.MUTED).pack(anchor="w", pady=(0, px(4)))
        java = s.get("java_path") or ""
        reverse = {v: k for k, v in self.app.java_map.items()}
        self.java_var = tk.StringVar(value=reverse.get(java, java) or FOLLOW_GLOBAL)
        ttk.Combobox(self.custom_box, textvariable=self.java_var, font=th.f_body,
                     values=[FOLLOW_GLOBAL] + list(self.app.java_map)).pack(fill="x", pady=(0, px(8)))
        label(self.custom_box, "额外 JVM 参数", th.f_small, T.MUTED).pack(anchor="w", pady=(0, px(4)))
        self.jvm_var = tk.StringVar(value=s.get("jvm_args", cfg["jvm_args"]))
        Field(self.custom_box, th, self.jvm_var).pack(fill="x")
        self._toggle_custom()

    def _toggle_custom(self):
        if self.custom_var.get():
            self.custom_box.pack(fill="x", pady=(self.px(10), 0))
        else:
            self.custom_box.pack_forget()

    def _save(self):
        s = dict(self.settings)
        if self.isolation_var.get() != self.isolation_initial or "isolation" in s:
            s["isolation"] = bool(self.isolation_var.get())
        s["custom"] = bool(self.custom_var.get())
        if s["custom"]:
            java = self.java_var.get().strip()
            s["max_memory"] = int(self.mem_var.get())
            s["java_path"] = "" if java in ("", FOLLOW_GLOBAL) else self.app.java_map.get(java, java)
            s["jvm_args"] = self.jvm_var.get().strip()
        if s != self.settings and os.path.isdir(self.gl.path("versions", self.version)):
            self.gl.save_version_settings(self.version, s)
            self.settings = s

    def close(self):
        self._save()
        self.top.grab_release()
        self.top.destroy()
        self.app.mods_page.ctx = None

    # ------------------------------------------------------------------ 文件夹与存档

    def _build_folders(self, parent):
        px, th = self.px, self.th
        for i, (text, sub) in enumerate(FOLDERS):
            Button(parent, th, text, lambda s=sub: self.open_folder(s), kind="secondary", icon="folder",
                   height=32, padx=10).grid(row=i // 2, column=i % 2, sticky="ew", padx=(0, px(6)) if i % 2 == 0
                                            else (px(6), 0), pady=(0, px(8)))
        parent.columnconfigure(0, weight=1, uniform="f")
        parent.columnconfigure(1, weight=1, uniform="f")

    def game_dir(self):
        self._save()
        return self.gl.game_dir_for(self.version)

    def open_folder(self, sub):
        game_dir = self.game_dir()
        open_path(self.app, os.path.join(game_dir, sub) if sub else game_dir)

    def _load_worlds(self):
        game_dir = self.gl.game_dir_for(self.version)
        self.app.spawn(lambda: instance.list_worlds(game_dir), self._render_worlds)

    def _render_worlds(self, worlds):
        if not self.top.winfo_exists():
            return
        px, th = self.px, self.th
        self.world_list.clear()
        self.world_count.configure(text=str(len(worlds)) if worlds else "")
        if not worlds:
            label(self.world_list.inner, "还没有存档，进入游戏创建一个世界后会显示在这里", th.f_body, T.MUTED).pack(
                anchor="w", pady=px(10))
            return
        for world in worlds:
            row = tk.Frame(self.world_list.inner, bg=T.CARD)
            row.pack(fill="x", pady=(0, px(8)), padx=(0, px(8)))
            size = px(36)
            icon = tk.Canvas(row, width=size, height=size, bg=T.CARD, highlightthickness=0)
            icon.pack(side="left")
            image = None
            if world["icon"]:
                try:
                    image = tk.PhotoImage(file=world["icon"])
                    factor = max(1, round(image.width() / size))
                    image = image.subsample(factor) if factor > 1 else image
                except tk.TclError:
                    image = None
            if image is not None:
                self.icons.append(image)
                icon.create_image(size // 2, size // 2, image=image)
            else:
                icon.create_rectangle(0, 0, size, size, fill=T.ACCENT_DIM, outline="")
                icon.create_text(size // 2, size // 2, text=th.icon("game"), font=th.f_icon, fill=T.ACCENT)
            text = tk.Frame(row, bg=T.CARD)
            text.pack(side="left", padx=(px(12), 0), fill="x", expand=True)
            label(text, world["name"], th.f_body_bold).pack(anchor="w")
            meta = [m for m in ("极限" if world["hardcore"] else world["mode"], world["version"],
                                time.strftime("%Y-%m-%d %H:%M", time.localtime(world["last_played"]))) if m]
            label(text, "  ·  ".join(meta), th.f_small, T.DIM).pack(anchor="w")
            Button(row, th, "备份", lambda w=world: self.backup(w), kind="ghost", height=30, padx=10).pack(side="right")
            Button(row, th, "打开", lambda w=world: open_path(self.app, w["path"]), kind="ghost", icon="folder",
                   height=30, padx=10).pack(side="right")

    def backup(self, world):
        game_dir = self.gl.game_dir_for(self.version)
        self.app.run_task("备份存档 " + world["name"], lambda: instance.backup_world(game_dir, world, self.app.progress),
                          lambda path: self.app.toast("已备份到 backups\\{}".format(os.path.basename(path))))

    # ------------------------------------------------------------------ 版本操作

    def _check_not_running(self):
        if self.app.running_version == self.version and self.app.game_proc and self.app.game_proc.poll() is None:
            self.app.dialog("游戏正在运行", "请先关闭正在运行的 {}。".format(self.version), "warn")
            return False
        return True

    def _validator(self, current):
        def validate(text):
            if text == current:
                raise ValueError("名称没有变化")
            return instance.check_name(self.gl, text)
        return validate

    def rename(self):
        if not self._check_not_running():
            return
        new = ask_string(self.app.root, self.th, "重命名版本", "新的版本名称：", self.version, self._validator(self.version),
                         "重命名")
        if not new:
            return
        self._save()
        try:
            instance.rename(self.gl, self.version, new)
        except OSError as e:
            self.app.dialog("重命名失败", "无法重命名（文件可能正被占用）：{}".format(e), "error")
            return
        self.app.toast("已重命名为 {}".format(new))
        self.top.grab_release()
        self.top.destroy()
        self.app.mods_page.ctx = None
        self.app.refresh_installed(select=new)

    def duplicate(self):
        new = ask_string(self.app.root, self.th, "复制版本", "新版本的名称（会复制版本文件夹中的全部内容，包括存档和模组）：",
                         self.version + " 副本", self._validator(""), "复制")
        if not new:
            return
        self._save()
        self.close()
        self.app.run_task("复制版本", lambda: instance.duplicate(self.gl, self.version, new, self.app.progress),
                          lambda v: (self.app.refresh_installed(select=v), self.app.toast("已复制为 {}".format(v))))

    def delete(self):
        if not self._check_not_running():
            return
        children = instance.dependents(self.gl, self.version)
        message = "确定要删除 {} 吗？版本文件夹会被永久删除".format(self.version)
        if self.gl.game_dir_for(self.version) == self.gl.path("versions", self.version):
            message += "，其中的存档和模组也会一起删除"
        message += "。"
        if children:
            message += "\n\n以下版本依赖它，删除后它们会在启动时自动重新下载所需文件：\n" + "、".join(children)
        if not self.app.dialog("删除版本", message, "warn", [("删除", True, "primary"), ("取消", False, "secondary")]):
            return
        try:
            instance.delete(self.gl, self.version)
        except OSError as e:
            self.app.dialog("删除失败", "部分文件无法删除（可能正被占用）：{}".format(e), "error")
        self.top.grab_release()
        self.top.destroy()
        self.app.mods_page.ctx = None
        self.app.refresh_installed()
        self.app.toast("已删除 {}".format(self.version))

    def export(self):
        from .modpack_dialog import export_modpack
        self._save()
        self.close()
        export_modpack(self.app, self.version)
