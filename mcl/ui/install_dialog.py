import threading
import tkinter as tk
from tkinter import ttk

from ..core import LOADER_NAMES
from ..loaders import LOADERS, LoaderInstaller
from . import theme as T
from .widgets import Button, Chip, Switch, create_modal, label, present_modal

OPTIFINE_LOADERS = ("vanilla", "forge")


def ask_install_options(app, mc):
    """选择要安装的加载器与 OptiFine。
    返回 (加载器或 None, 加载器版本信息, OptiFine 版本信息或 None)；取消返回 None。"""
    th, px, root = app.theme, app.px, app.root
    top = create_modal(root, "安装 Minecraft " + mc)
    results = {}
    result = [None]
    installer = LoaderInstaller(app.make_launcher())

    body = tk.Frame(top, bg=T.BG)
    body.pack(fill="both", expand=True, padx=px(30), pady=(px(26), px(6)))
    label(body, "安装 Minecraft " + mc, th.f_h3).pack(anchor="w")
    label(body, "选择一个模组加载器；只想玩原版的话直接安装即可。", th.f_body, T.MUTED).pack(anchor="w", pady=(px(4), 0))

    loader_var = tk.StringVar(value="vanilla")
    chips_row = tk.Frame(body, bg=T.BG)
    chips_row.pack(anchor="w", pady=(px(20), px(18)))
    chips = {}
    for key in ("vanilla",) + LOADERS:
        chip = Chip(chips_row, th, "原版" if key == "vanilla" else LOADER_NAMES[key], loader_var,
                    command=lambda: refresh(), value=key)
        chip.pack(side="left", padx=(0, px(8)))
        chips[key] = chip

    label(body, "加载器版本", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
    version_var = tk.StringVar()
    version_box = ttk.Combobox(body, textvariable=version_var, state="disabled", width=44, font=th.f_body)
    version_box.pack(anchor="w", fill="x")
    hint = label(body, "", th.f_small, T.MUTED, wraplength=px(500), justify="left")
    hint.pack(anchor="w", pady=(px(8), 0))

    tk.Frame(body, bg=T.CARD_BORDER, height=1).pack(fill="x", pady=(px(18), px(16)))
    of_head = tk.Frame(body, bg=T.BG)
    of_head.pack(fill="x", pady=(0, px(8)))
    of_var = tk.BooleanVar(value=False)
    of_switch = Switch(of_head, th, of_var, command=lambda: refresh())
    of_switch.pack(side="left")
    label(of_head, "同时安装 OptiFine", th.f_body_bold).pack(side="left", padx=(px(12), 0))
    label(of_head, "高清修复与光影支持", th.f_small, T.DIM).pack(side="left", padx=(px(10), 0))
    of_version_var = tk.StringVar()
    of_box = ttk.Combobox(body, textvariable=of_version_var, state="disabled", width=44, font=th.f_body)
    of_box.pack(anchor="w", fill="x")
    of_hint = label(body, "", th.f_small, T.MUTED, wraplength=px(500), justify="left")
    of_hint.pack(anchor="w", pady=(px(8), 0))

    bar = tk.Frame(top, bg=T.BG)
    bar.pack(fill="x", padx=px(26), pady=(px(14), px(22)))
    install_btn = Button(bar, th, "安装", lambda: finish(True), icon="download", height=36, padx=22)
    install_btn.pack(side="right", padx=(px(8), 0))
    Button(bar, th, "取消", lambda: finish(False), kind="secondary", height=36, padx=20).pack(side="right")

    def label_for(item):
        tags = []
        if item.get("recommended"):
            tags.append("推荐")
        if not item["stable"]:
            tags.append("测试版")
        return item["display"] + ("    （{}）".format("，".join(tags)) if tags else "")

    def fill_box(box, var, items):
        values = [label_for(i) for i in items]
        box.configure(state="readonly", values=values)
        if var.get() not in values:
            var.set(values[0])

    def refresh_loader():
        loader = loader_var.get()
        if loader == "vanilla":
            version_box.configure(state="disabled", values=[])
            version_var.set("无需选择")
            hint.configure(text="将安装不带模组加载器的原版游戏。", fg=T.MUTED)
            return True
        state = results.get(loader)
        version_box.configure(state="disabled", values=[])
        if state is None:
            version_var.set("")
            hint.configure(text="正在获取 {} 版本列表…".format(LOADER_NAMES[loader]), fg=T.MUTED)
            return False
        if isinstance(state, Exception):
            version_var.set("")
            hint.configure(text="获取版本列表失败：{}".format(state), fg=T.ERROR)
            return False
        fill_box(version_box, version_var, state)
        hint.configure(text="共 {} 个可用版本，默认选中推荐版本。".format(len(state)), fg=T.MUTED)
        return True

    def refresh_optifine():
        loader = loader_var.get()
        state = results.get("optifine")
        available = loader in OPTIFINE_LOADERS and isinstance(state, list) and bool(state)
        of_switch.set_enabled(available)
        if not available:
            of_var.set(False)
        if state is None:
            message = "正在获取 OptiFine 版本列表…"
        elif isinstance(state, Exception):
            message = "获取 OptiFine 版本列表失败：{}".format(state)
        elif not state:
            message = "OptiFine 暂不支持 Minecraft {}。".format(mc)
        elif loader not in OPTIFINE_LOADERS:
            message = "OptiFine 只能搭配原版或 Forge 使用。"
        elif loader == "forge":
            message = "OptiFine 会作为模组放进 mods 文件夹，与 Forge 一起加载。"
        else:
            message = "将生成独立的 OptiFine 版本（独立版本无法加载其他模组）。"
        if of_var.get():
            fill_box(of_box, of_version_var, state)
            item = state[max(of_box.current(), 0)]
            if loader == "forge" and item.get("forge", "").startswith("Forge "):
                message += "建议 Forge 版本不低于 {}。".format(item["forge"][6:])
        else:
            of_box.configure(state="disabled", values=[])
            of_version_var.set("")
        of_hint.configure(text=message, fg=T.ERROR if isinstance(state, Exception) else T.MUTED)

    def refresh():
        if not top.winfo_exists():
            return
        ok = refresh_loader()
        refresh_optifine()
        install_btn.set_enabled(ok)

    of_box.bind("<<ComboboxSelected>>", lambda e: refresh())

    def on_loaded(loader, items):
        results[loader] = items
        if not top.winfo_exists():
            return
        if loader in chips and isinstance(items, list) and not items:
            chips[loader].set_text(LOADER_NAMES[loader] + " · 不支持")
            chips[loader].set_enabled(False)
            if loader_var.get() == loader:
                loader_var.set("vanilla")
        refresh()

    def fetch(loader):
        try:
            items = installer.list_versions(loader, mc)
        except Exception as e:
            items = e
        app.ui(on_loaded, loader, items)

    for key in LOADERS + ("optifine",):
        threading.Thread(target=fetch, args=(key,), daemon=True).start()

    def finish(confirm):
        if confirm:
            loader = loader_var.get()
            item = None
            if loader != "vanilla":
                items = results.get(loader)
                if not isinstance(items, list) or not items:
                    return
                item = items[max(version_box.current(), 0)]
            optifine = None
            if of_var.get() and isinstance(results.get("optifine"), list):
                optifine = results["optifine"][max(of_box.current(), 0)]
            result[0] = (None if loader == "vanilla" else loader, item, optifine)
        top.grab_release()
        top.destroy()

    top.bind("<Escape>", lambda e: finish(False))
    top.protocol("WM_DELETE_WINDOW", lambda: finish(False))
    refresh()
    present_modal(root, top, px(560))
    return result[0]
