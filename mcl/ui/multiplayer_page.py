import os
import tkinter as tk
import webbrowser

from .. import config
from ..multiplayer import DIFFICULTIES, EXCEPTIONS, PROJECT_URL, Terracotta
from . import theme as T
from .widgets import Button, Field, ProgressLine, label

ROOM_STATES = ("host-scanning", "host-starting", "host-ok", "guest-connecting", "guest-starting", "guest-ok")
KIND_NAMES = {"HOST": "房主", "LOCAL": "你", "GUEST": "玩家"}


class MultiplayerPage:
    def __init__(self, app, page):
        self.app = app
        self.theme = app.theme
        self.px = app.px
        self.tc = Terracotta(os.path.join(config.BASE_DIR, "tools", "terracotta"), app.make_launcher().dl,
                             log=app.log)
        self.phase = "init"
        self.error = ""
        self.state = {}
        self.prev_profiles = []
        self.signature = None
        self._polling = False
        self._poll_job = None
        self.code_var = tk.StringVar()
        self._build(page)

    # ------------------------------------------------------------------ 布局

    def _build(self, page):
        px, th = self.px, self.theme
        self.app._page_header(page, "多人联机",
                              "基于陶瓦联机，无需公网 IP 就能和好友一起玩，邀请码与 HMCL、PCL 社区版互通")
        footer = tk.Frame(page, bg=T.BG)
        footer.pack(side="bottom", fill="x", pady=(px(12), 0))
        credit = label(footer, "联机服务由 Terracotta | 陶瓦联机 提供  ·  © Burning_TNT  ·  AGPL-3.0  ·  基于 EasyTier",
                       th.f_small, T.DIM, cursor="hand2")
        credit.pack(side="left")
        credit.bind("<Button-1>", lambda e: webbrowser.open(PROJECT_URL))
        credit.bind("<Enter>", lambda e: credit.configure(fg=T.ACCENT))
        credit.bind("<Leave>", lambda e: credit.configure(fg=T.DIM))
        self.body = tk.Frame(page, bg=T.BG)
        self.body.pack(fill="both", expand=True)

    def _clear(self):
        for child in self.body.winfo_children():
            child.destroy()

    def _card(self, parent, **pack):
        card = tk.Frame(parent, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
        card.pack(**pack)
        return card

    def _center_card(self, icon, title, text, color=T.ACCENT):
        px, th = self.px, self.theme
        card = self._card(self.body, fill="x")
        inner = tk.Frame(card, bg=T.CARD)
        inner.pack(padx=px(36), pady=px(34))
        label(inner, th.icon(icon), th.f_icon_big, color).pack()
        label(inner, title, th.f_h3).pack(pady=(px(12), px(6)))
        if text:
            label(inner, text, th.f_body, T.MUTED, justify="center").pack()
        actions = tk.Frame(inner, bg=T.CARD)
        actions.pack(pady=(px(20), 0))
        return card, actions

    def _steps(self, parent, steps):
        px, th = self.px, self.theme
        for i, text in enumerate(steps, 1):
            row = tk.Frame(parent, bg=parent.cget("bg"))
            row.pack(fill="x", pady=(0, px(10)))
            badge = tk.Canvas(row, width=px(22), height=px(22), bg=parent.cget("bg"), highlightthickness=0)
            badge.pack(side="left", anchor="n")
            badge.create_oval(1, 1, px(22) - 1, px(22) - 1, fill=T.ACCENT_DIM, outline="")
            badge.create_text(px(11), px(11), text=str(i), font=th.f_small_bold, fill=T.ACCENT)
            label(row, text, th.f_body, T.TEXT, justify="left", wraplength=px(330)).pack(
                side="left", padx=(px(12), 0), anchor="w")

    # ------------------------------------------------------------------ 生命周期

    def on_show(self):
        if self.phase == "init":
            self._bootstrap()
        self._render()
        self._schedule(0)

    def _bootstrap(self):
        if not self.tc.supported:
            self.phase = "unsupported"
            return
        self.phase = "checking"
        self.app.spawn(self.tc.installed, lambda ok: self._start() if ok else self._set_phase("missing"),
                       lambda e: self._set_phase("missing"))

    def _set_phase(self, phase, error=""):
        self.phase, self.error = phase, error
        self._render()
        if phase == "ready":
            self._schedule(0)

    def install(self):
        self._set_phase("installing")
        self.app.progress_line.start()
        self.app.status.set("正在下载陶瓦联机…")

        def done(_):
            self.app.status.set("就绪")
            self.app.progress_line.set(0)
            self.app.progress_text.set("")
            self._start()

        def failed(error):
            self.app.status.set("就绪")
            self.app.progress_line.set(0)
            self.app.progress_text.set("")
            self._set_phase("fatal", "下载陶瓦联机失败：{}".format(error))

        self.app.spawn(lambda: self.tc.install(self.app.progress), done, failed)

    def _start(self):
        self._set_phase("starting")
        self.app.spawn(self.tc.start, lambda _: self._set_phase("ready"),
                       lambda e: self._set_phase("fatal", str(e)))

    def in_room(self):
        return self.phase == "ready" and self.state.get("state") in ROOM_STATES

    def shutdown(self):
        self.tc.shutdown()

    # ------------------------------------------------------------------ 状态轮询

    def _schedule(self, delay=None):
        if self._poll_job:
            self.app.root.after_cancel(self._poll_job)
            self._poll_job = None
        if self.phase != "ready":
            return
        if delay is None:
            delay = 1000 if self.app.current_page == "multiplayer" else 3000
        self._poll_job = self.app.root.after(delay, self._poll)

    def _poll(self):
        self._poll_job = None
        if self._polling or self.phase != "ready":
            return
        self._polling = True

        def done(state):
            self._polling = False
            previous = self.state.get("state")
            self.state = state
            current = state.get("state")
            if previous == "host-ok" and current == "host-ok":
                joined = len(state.get("profiles", [])) - len(self.prev_profiles)
                if joined > 0:
                    self.app.toast("有新玩家加入了房间")
            if current == "guest-ok" and previous != "guest-ok":
                self.app.toast("已成功加入房间")
            self.prev_profiles = state.get("profiles", [])
            self._render()
            self._schedule()

        def failed(error):
            self._polling = False
            if not self.tc.alive():
                self.state = {}
                self._set_phase("fatal", "陶瓦联机已停止运行")
            else:
                self._schedule()

        self.app.spawn(self.tc.state, done, failed)

    def _action(self, fn, error_title):
        def failed(error):
            self.app.dialog(error_title, str(error), "error")
            self._schedule(0)

        self.app.spawn(fn, lambda _: self._schedule(0), failed)

    def create_room(self):
        player = self.app.username_var.get().strip() or "Steve"
        self._action(lambda: self.tc.host(player), "创建房间失败")

    def join_room(self):
        code = self.code_var.get().strip()
        if not code:
            self.app.toast("请先输入邀请码", "warn")
            return
        player = self.app.username_var.get().strip() or "Steve"
        self._action(lambda: self.tc.join(code, player), "加入房间失败")

    def leave_room(self):
        self._action(self.tc.leave, "操作失败")

    def copy(self, text, what):
        self.app.root.clipboard_clear()
        self.app.root.clipboard_append(text)
        self.app.toast("已复制{}".format(what))

    # ------------------------------------------------------------------ 渲染

    def _render(self):
        state = self.state if self.phase == "ready" else {}
        signature = (self.phase, self.error, state.get("state"), state.get("room"), state.get("url"),
                     state.get("difficulty"), state.get("type"),
                     tuple((p.get("machine_id"), p.get("name"), p.get("kind")) for p in state.get("profiles", [])))
        if signature == self.signature:
            return
        self.signature = signature
        self._clear()
        getattr(self, "_view_" + (state.get("state", "unknown").replace("-", "_") if state else self.phase),
                self._view_loading)()

    def _view_loading(self):
        text = {"installing": "正在下载陶瓦联机…", "starting": "正在启动联机服务…"}.get(self.phase, "正在准备…")
        card, _ = self._center_card("people", text, "首次启动时 Windows 可能会询问是否允许网络访问，请选择允许。")
        line = ProgressLine(card, self.theme)
        line.pack(fill="x", side="bottom")
        line.start()

    _view_init = _view_checking = _view_installing = _view_starting = _view_unknown = _view_loading

    def _view_unsupported(self):
        self._center_card("error", "当前系统暂不支持联机", "陶瓦联机需要 Windows 10 及以上（x64 / ARM64）或 Linux。", T.WARN)

    def _view_missing(self):
        _, actions = self._center_card(
            "people", "启用多人联机",
            "首次使用需要下载陶瓦联机组件（约 8 MB），下载后会自动启动。\n"
            "它基于 EasyTier 建立点对点连接，不需要公网 IP，也不需要管理员权限。")
        Button(actions, self.theme, "下载并启用", self.install, icon="download", height=40, padx=22).pack()

    def _view_fatal(self):
        _, actions = self._center_card("error", "联机服务出现问题", self.error, T.ERROR)
        Button(actions, self.theme, "重试", self._retry, icon="refresh", height=38, padx=20).pack()

    def _retry(self):
        if self.tc.installed():
            self._start()
        else:
            self.install()

    def _view_waiting(self):
        px, th = self.px, self.theme
        grid = tk.Frame(self.body, bg=T.BG)
        grid.pack(fill="both", expand=True)
        grid.columnconfigure(0, weight=1, uniform="col")
        grid.columnconfigure(1, weight=1, uniform="col")

        def column(col, icon, title, subtitle):
            card = tk.Frame(grid, bg=T.CARD, highlightthickness=1, highlightbackground=T.CARD_BORDER)
            card.grid(row=0, column=col, sticky="nsew", padx=(0 if col == 0 else px(10), px(10) if col == 0 else 0))
            inner = tk.Frame(card, bg=T.CARD)
            inner.pack(fill="both", expand=True, padx=px(26), pady=px(24))
            head = tk.Frame(inner, bg=T.CARD)
            head.pack(fill="x")
            label(head, th.icon(icon), th.f_icon, T.ACCENT).pack(side="left")
            label(head, title, th.f_h3).pack(side="left", padx=(px(10), 0))
            label(inner, subtitle, th.f_body, T.MUTED).pack(anchor="w", pady=(px(6), px(20)))
            return inner

        host = column(0, "game", "创建房间", "我是房主，邀请好友来我的世界")
        self._steps(host, ["启动游戏，进入一个单人世界",
                           "按\u00a0Esc\u00a0打开菜单，选择「对局域网开放」，再点「创建局域网世界」",
                           "回到这里点击「创建房间」，把邀请码发给好友"])
        Button(host, th, "创建房间", self.create_room, icon="people", height=40, padx=22).pack(
            anchor="w", side="bottom", pady=(px(10), 0))

        guest = column(1, "link", "加入房间", "好友已经创建了房间，输入邀请码加入")
        label(guest, "邀请码", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
        field = Field(guest, th, self.code_var)
        field.pack(fill="x")
        field.entry.bind("<Return>", lambda e: self.join_room())
        label(guest, "形如 U/XXXX-XXXX-XXXX-XXXX，也可以使用 HMCL、PCL 社区版生成的邀请码",
              th.f_small, T.DIM, wraplength=px(380), justify="left").pack(anchor="w", pady=(px(8), 0))
        Button(guest, th, "加入房间", self.join_room, icon="link", height=40, padx=22).pack(
            anchor="w", side="bottom", pady=(px(10), 0))

    def _view_host_scanning(self):
        card, actions = self._center_card(
            "game", "正在寻找对局域网开放的世界…",
            "请在游戏中按 Esc，选择「对局域网开放」并点击「创建局域网世界」。\n检测到之后会自动创建房间。")
        Button(actions, self.theme, "取消", self.leave_room, kind="secondary", height=38, padx=20).pack()
        line = ProgressLine(card, self.theme)
        line.pack(fill="x", side="bottom")
        line.start()

    def _view_host_starting(self):
        card, actions = self._center_card("people", "正在创建房间…", "正在连接公共节点，通常只需要几秒钟。")
        Button(actions, self.theme, "取消", self.leave_room, kind="secondary", height=38, padx=20).pack()
        line = ProgressLine(card, self.theme)
        line.pack(fill="x", side="bottom")
        line.start()

    def _view_guest_connecting(self):
        card, actions = self._center_card("link", "正在加入房间…", "正在连接公共节点并寻找房主。")
        Button(actions, self.theme, "取消", self.leave_room, kind="secondary", height=38, padx=20).pack()
        line = ProgressLine(card, self.theme)
        line.pack(fill="x", side="bottom")
        line.start()

    def _view_guest_starting(self):
        text = DIFFICULTIES.get(self.state.get("difficulty"), DIFFICULTIES["UNKNOWN"])
        card, actions = self._center_card("link", "已找到房主，正在建立连接…", text)
        Button(actions, self.theme, "取消", self.leave_room, kind="secondary", height=38, padx=20).pack()
        line = ProgressLine(card, self.theme)
        line.pack(fill="x", side="bottom")
        line.start()

    def _hero(self, overline, value, value_font):
        px, th = self.px, self.theme
        card = self._card(self.body, fill="x")
        inner = tk.Frame(card, bg=T.CARD)
        inner.pack(fill="x", padx=px(30), pady=(px(26), px(26)))
        label(inner, " ".join(overline), th.f_overline, T.ACCENT).pack(anchor="w")
        label(inner, value, value_font).pack(anchor="w", pady=(px(6), px(4)))
        return inner

    def _members(self):
        px, th = self.px, self.theme
        profiles = self.state.get("profiles", [])
        card = self._card(self.body, fill="both", expand=True, pady=(px(16), 0))
        head = tk.Frame(card, bg=T.CARD)
        head.pack(fill="x", padx=px(22), pady=(px(16), px(10)))
        label(head, "房间成员", th.f_body_bold).pack(side="left")
        label(head, str(len(profiles)), th.f_body_bold, T.ACCENT).pack(side="left", padx=(px(8), 0))
        for profile in profiles:
            row = tk.Frame(card, bg=T.CARD)
            row.pack(fill="x", padx=px(22), pady=(0, px(10)))
            name = profile.get("name") or "?"
            avatar = tk.Canvas(row, width=px(32), height=px(32), bg=T.CARD, highlightthickness=0)
            avatar.pack(side="left")
            avatar.create_oval(1, 1, px(32) - 1, px(32) - 1, fill=T.ACCENT_DIM, outline="")
            avatar.create_text(px(16), px(16), text=name[0].upper(), font=th.f_body_bold, fill=T.ACCENT)
            text = tk.Frame(row, bg=T.CARD)
            text.pack(side="left", padx=(px(12), 0))
            line = tk.Frame(text, bg=T.CARD)
            line.pack(anchor="w")
            label(line, name, th.f_body_bold).pack(side="left")
            kind = profile.get("kind", "GUEST")
            label(line, KIND_NAMES.get(kind, kind), th.f_small, T.ACCENT if kind == "HOST" else T.MUTED).pack(
                side="left", padx=(px(8), 0))
            label(text, profile.get("vendor", ""), th.f_small, T.DIM).pack(anchor="w")

    def _view_host_ok(self):
        px, th = self.px, self.theme
        code = self.state.get("room", "")
        inner = self._hero("ROOM CODE", code, th.f_h1)
        label(inner, "房间已创建，把邀请码发给好友即可加入。请保持游戏和启动器运行。", th.f_body, T.MUTED).pack(anchor="w")
        actions = tk.Frame(inner, bg=T.CARD)
        actions.pack(anchor="w", pady=(px(18), 0))
        Button(actions, th, "复制邀请码", lambda: self.copy(code, "邀请码"), icon="copy", height=38, padx=18).pack(
            side="left")
        Button(actions, th, "关闭房间", self.leave_room, kind="secondary", icon="close", height=38, padx=18).pack(
            side="left", padx=(px(10), 0))
        self._members()

    def _view_guest_ok(self):
        px, th = self.px, self.theme
        url = self.state.get("url", "")
        inner = self._hero("CONNECTED", url, th.f_h1)
        label(inner, "已加入房间。在游戏「多人游戏」列表中会出现「陶瓦联机大厅」，也可以直接连接上面的地址。",
              th.f_body, T.MUTED, wraplength=px(640), justify="left").pack(anchor="w")
        actions = tk.Frame(inner, bg=T.CARD)
        actions.pack(anchor="w", pady=(px(18), 0))
        Button(actions, th, "启动游戏并进入", lambda: self.app.launch(server=url), icon="play", height=38,
               padx=18).pack(side="left")
        Button(actions, th, "复制地址", lambda: self.copy(url, "服务器地址"), kind="secondary", icon="copy",
               height=38, padx=16).pack(side="left", padx=(px(10), 0))
        Button(actions, th, "退出房间", self.leave_room, kind="secondary", icon="close", height=38, padx=16).pack(
            side="left", padx=(px(10), 0))
        self._members()

    def _view_exception(self):
        kind = self.state.get("type", -1)
        message = EXCEPTIONS[kind] if 0 <= kind < len(EXCEPTIONS) else "联机出现未知错误"
        _, actions = self._center_card("warn", "联机已中断", message, T.WARN)
        Button(actions, self.theme, "返回", self.leave_room, height=38, padx=22).pack()
