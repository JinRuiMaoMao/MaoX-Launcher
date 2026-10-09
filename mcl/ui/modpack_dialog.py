import os
import re
import tkinter as tk
from tkinter import filedialog, ttk

from .. import instance, modpack
from ..core import LOADER_NAMES
from ..download import DownloadTask
from . import theme as T
from .widgets import Button, Field, Switch, create_modal, label, present_modal

EXPORT_OPTIONS = (("config", "配置文件", "config、defaultconfigs、kubejs 等", True),
                  ("options", "游戏设置", "options.txt（按键、视频设置）", False),
                  ("resourcepacks", "资源包", "resourcepacks 文件夹", True),
                  ("shaderpacks", "光影包", "shaderpacks 文件夹", True),
                  ("saves", "存档", "saves 文件夹，体积可能很大", False))


def _unique_name(gl, base):
    base = instance.INVALID_NAME.sub("", base).strip().rstrip(".") or "整合包"
    name, n = base, 2
    while os.path.exists(gl.path("versions", name)):
        name, n = "{} ({})".format(base, n), n + 1
    return name


def _form(app, title, subtitle, build, confirm, min_width=520):
    """通用表单对话框：build(body) 返回取值函数（抛 ValueError 表示输入有误）。"""
    th, px = app.theme, app.px
    top = create_modal(app.root, title)
    result = [None]
    body = tk.Frame(top, bg=T.BG)
    body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(4)))
    label(body, title, th.f_h3).pack(anchor="w")
    if subtitle:
        label(body, subtitle, th.f_body, T.MUTED, wraplength=px(min_width - 60), justify="left").pack(
            anchor="w", pady=(px(4), px(16)))
    getter = build(body)
    error = label(body, "", th.f_small, T.ERROR)
    error.pack(anchor="w", pady=(px(6), 0))

    def finish(ok):
        if ok:
            try:
                result[0] = getter()
            except ValueError as e:
                error.configure(text=str(e))
                return
        top.grab_release()
        top.destroy()

    bar = tk.Frame(top, bg=T.BG)
    bar.pack(fill="x", padx=px(24), pady=(px(10), px(20)))
    Button(bar, th, confirm, lambda: finish(True), icon="download", height=34, padx=20).pack(side="right", padx=(px(8), 0))
    Button(bar, th, "取消", lambda: finish(False), kind="secondary", height=34, padx=20).pack(side="right")
    top.bind("<Escape>", lambda e: finish(False))
    top.protocol("WM_DELETE_WINDOW", lambda: finish(False))
    present_modal(app.root, top, px(min_width))
    return result[0]


def _field(app, parent, text, var):
    label(parent, text, app.theme.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, app.px(6)))
    field = Field(parent, app.theme, var)
    field.pack(fill="x", pady=(0, app.px(12)))
    return field


def _describe(info):
    loader = "{} {}".format(LOADER_NAMES.get(info["loader"], info["loader"]), info["loader_version"]) \
        if info["loader"] else "原版"
    return "Minecraft {}  ·  {}  ·  {} 个文件".format(info["mc"], loader, info["file_count"])


def _run_install(app, path, name, cleanup=False):
    gl = app.make_launcher()

    def job():
        try:
            return modpack.install(gl, path, name, app.log, app.progress)
        finally:
            if cleanup and os.path.isfile(path):
                os.remove(path)

    def done(version):
        app._append_log("整合包 {} 安装完成".format(version), "success")
        app.refresh_installed(select=version)
        app.show_page("launch")
        app.toast("整合包 {} 安装完成".format(version))

    app._append_log("─" * 48, "launcher")
    app.run_task("安装整合包", job, done)


def import_modpack_file(app):
    path = filedialog.askopenfilename(title="选择整合包", filetypes=[
        ("整合包", "*.mrpack *.zip"), ("所有文件", "*.*")])
    if not path:
        return
    try:
        info = modpack.read_manifest(path)
    except modpack.ModpackError as e:
        app.dialog("无法导入", str(e), "error")
        return
    gl = app.make_launcher()
    name_var = tk.StringVar(value=_unique_name(gl, info["name"]))

    def build(body):
        _field(app, body, "版本名称", name_var)
        return lambda: instance.check_name(gl, name_var.get())

    title = "{} {}".format(info["name"], info["version"]).strip()
    name = _form(app, "导入整合包", "{}\n{}".format(title, _describe(info)), build, "安装")
    if name:
        _run_install(app, path, name)


def install_modpack_from_hit(app, client, hit):
    app.status.set("正在获取 {} 的版本列表…".format(hit["title"]))

    def done(versions):
        app.status.set("就绪")
        if not versions:
            app.dialog("无法安装", "{} 没有可下载的整合包文件。".format(hit["title"]), "error")
            return
        _choose_and_install(app, hit, versions)

    def failed(error):
        app.status.set("就绪")
        app.dialog("获取版本列表失败", str(error), "error")

    app.spawn(lambda: client.modpack_versions(hit["id"]), done, failed)


def _choose_and_install(app, hit, versions):
    gl = app.make_launcher()
    th, px = app.theme, app.px
    name_var = tk.StringVar(value=_unique_name(gl, hit["title"]))
    version_var = tk.StringVar(value=versions[0]["name"])

    def build(body):
        label(body, "整合包版本", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
        box = ttk.Combobox(body, textvariable=version_var, state="readonly", font=th.f_body,
                           values=[v["name"] for v in versions])
        box.pack(fill="x")
        detail = label(body, versions[0]["detail"], th.f_small, T.DIM)
        detail.pack(anchor="w", pady=(px(6), px(14)))
        box.bind("<<ComboboxSelected>>", lambda e: detail.configure(text=versions[box.current()]["detail"]))
        _field(app, body, "版本名称", name_var)
        return lambda: (versions[max(box.current(), 0)], instance.check_name(gl, name_var.get()))

    result = _form(app, "安装整合包", hit["title"], build, "安装")
    if not result:
        return
    item, name = result
    path = gl.path("cache", "modpacks", re.sub(r'[\\/:*?"<>|]', "_", item["filename"]))

    def job():
        gl.dl.download_many([DownloadTask(item["url"], path, item.get("sha1"), item.get("size"))],
                            lambda d, t: app.progress(d, t, "下载整合包"))
        try:
            return modpack.install(gl, path, name, app.log, app.progress)
        finally:
            if os.path.isfile(path):
                os.remove(path)

    def done(version):
        app._append_log("整合包 {} 安装完成".format(version), "success")
        app.refresh_installed(select=version)
        app.show_page("launch")
        app.toast("整合包 {} 安装完成".format(version))

    app._append_log("─" * 48, "launcher")
    app.run_task("安装整合包 " + hit["title"], job, done)


def export_modpack(app, version):
    gl = app.make_launcher()
    loader = gl.detect_loader(version)[0]
    if loader == "optifine":
        app.dialog("无法导出", "OptiFine 独立版本无法导出为整合包，请改用 Forge + OptiFine。", "warn")
        return
    th, px = app.theme, app.px
    pack = gl.version_settings(version).get("modpack") or {}
    name_var = tk.StringVar(value=pack.get("name") or version)
    version_var = tk.StringVar(value=pack.get("version") or "1.0.0")
    summary_var = tk.StringVar()
    switches = {}

    def build(body):
        _field(app, body, "整合包名称", name_var)
        _field(app, body, "整合包版本", version_var)
        _field(app, body, "简介（可选）", summary_var)
        label(body, "包含内容（模组总是包含）", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(px(4), px(8)))
        for key, text, desc, default in EXPORT_OPTIONS:
            row = tk.Frame(body, bg=T.BG)
            row.pack(fill="x", pady=(0, px(8)))
            var = tk.BooleanVar(value=default)
            Switch(row, th, var).pack(side="left")
            label(row, text, th.f_body).pack(side="left", padx=(px(12), 0))
            label(row, desc, th.f_small, T.DIM).pack(side="left", padx=(px(8), 0))
            switches[key] = var

        def get():
            if not name_var.get().strip():
                raise ValueError("请填写整合包名称")
            return name_var.get().strip(), version_var.get().strip(), summary_var.get().strip(), \
                [k for k, v in switches.items() if v.get()]
        return get

    result = _form(app, "导出整合包", "导出为 Modrinth 整合包（.mrpack），可以在 MaoX、HMCL、PCL、Prism 等启动器中导入。"
                   "能在 Modrinth 上找到的模组只记录下载地址，体积更小。", build, "导出", 560)
    if not result:
        return
    name, pack_version, summary, include = result
    safe = re.sub(r'[\\/:*?"<>|]', "_", "{}-{}".format(name, pack_version))
    dest = filedialog.asksaveasfilename(title="保存整合包", defaultextension=".mrpack", initialfile=safe + ".mrpack",
                                        filetypes=[("Modrinth 整合包", "*.mrpack")])
    if not dest:
        return
    app.run_task("导出整合包", lambda: modpack.export_mrpack(gl, version, dest, name, pack_version, summary, include,
                                                         app.log, app.progress),
                 lambda r: app.toast("整合包已导出（{} 个在线文件，{} 个打包文件）".format(*r)))
