import base64
import threading
import time
import tkinter as tk
import webbrowser

from .. import accounts as acc
from . import theme as T
from .widgets import Button, Field, ScrollFrame, ask_string, create_modal, label, present_modal


def face_image(skin_png, zoom):
    """从皮肤贴图中裁出头像（含帽子层），放大 zoom 倍。"""
    skin = tk.PhotoImage(data=base64.b64encode(skin_png))
    face = tk.PhotoImage(width=8 * zoom, height=8 * zoom)
    face.tk.call(face, "copy", skin, "-from", 8, 8, 16, 16, "-zoom", zoom)
    if skin.height() >= 32:
        face.tk.call(face, "copy", skin, "-from", 40, 8, 48, 16, "-zoom", zoom, "-compositingrule", "overlay")
    return face


def draw_avatar(canvas, size, account, image=None, bg=T.CARD):
    canvas.delete("all")
    if image is not None:
        canvas.create_image(size // 2, size // 2, image=image)
        return
    name = (account or {}).get("name") or "?"
    canvas.create_oval(1, 1, size - 1, size - 1, fill=T.ACCENT_DIM, outline=T.ACCENT)
    font = ("Segoe UI", max(8, size // 4), "bold")
    canvas.create_text(size // 2, size // 2, text=name[0].upper(), font=font, fill=T.ACCENT)


def _validate_offline(name):
    if not name:
        raise ValueError("名称不能为空")
    if any(c.isspace() for c in name):
        raise ValueError("名称不能包含空格")
    if len(name) > 16:
        raise ValueError("名称不能超过 16 个字符")
    return name


class AccountDialog:
    def __init__(self, app):
        self.app = app
        self.th, self.px = app.theme, app.px
        th, px = self.th, self.px
        self.top = top = create_modal(app.root, "账号管理")

        body = tk.Frame(top, bg=T.BG)
        body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(4)))
        label(body, "账号管理", th.f_h3).pack(anchor="w")
        label(body, "离线账号可以随意改名；正版和外置账号联机时能显示皮肤", th.f_body, T.MUTED).pack(
            anchor="w", pady=(px(4), px(14)))
        self.list = ScrollFrame(body, th)
        self.list.configure(height=px(250))
        self.list.pack(fill="x")
        self.list.pack_propagate(False)

        add = tk.Frame(body, bg=T.BG)
        add.pack(fill="x", pady=(px(16), 0))
        label(add, "添加账号", th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(8)))
        row = tk.Frame(add, bg=T.BG)
        row.pack(anchor="w")
        Button(row, th, "离线账号", self.add_offline, kind="secondary", icon="people", height=36, padx=16).pack(side="left")
        Button(row, th, "外置登录", self.add_authlib, kind="secondary", icon="link", height=36, padx=16).pack(
            side="left", padx=(px(8), 0))
        Button(row, th, "微软正版", self.add_msa, kind="secondary", icon="game", height=36, padx=16).pack(
            side="left", padx=(px(8), 0))

        bar = tk.Frame(top, bg=T.BG)
        bar.pack(fill="x", padx=px(24), pady=(px(18), px(20)))
        Button(bar, th, "完成", self.close, height=34, padx=22).pack(side="right")
        top.bind("<Escape>", lambda e: self.close())
        top.protocol("WM_DELETE_WINDOW", self.close)
        self.render()
        present_modal(app.root, top, px(560))

    def close(self):
        self.top.grab_release()
        self.top.destroy()

    def render(self):
        th, px = self.th, self.px
        self.list.clear()
        accounts = self.app.accounts
        if not accounts:
            label(self.list.inner, "还没有账号，先添加一个吧", th.f_body, T.MUTED).pack(pady=px(30))
        for index, account in enumerate(accounts):
            selected = index == self.app.account_index
            row = tk.Frame(self.list.inner, bg=T.CARD, highlightthickness=1,
                           highlightbackground=T.ACCENT if selected else T.CARD_BORDER, cursor="hand2")
            row.pack(fill="x", pady=(0, px(8)), padx=(0, px(6)))
            size = px(36)
            avatar = tk.Canvas(row, width=size, height=size, bg=T.CARD, highlightthickness=0)
            avatar.pack(side="left", padx=px(14), pady=px(10))
            draw_avatar(avatar, size, account, self.app.avatar_for(
                account, size, lambda: self.top.winfo_exists() and self.render()))
            text = tk.Frame(row, bg=T.CARD)
            text.pack(side="left", fill="x", expand=True)
            label(text, account["name"], th.f_body_bold).pack(anchor="w")
            label(text, acc.describe(account), th.f_small, T.MUTED).pack(anchor="w")
            Button(row, th, "", lambda i=index: self.remove(i), kind="ghost", icon="delete", height=30,
                   padx=8).pack(side="right", padx=(0, px(10)))
            if account["type"] == "offline":
                Button(row, th, "改名", lambda i=index: self.rename(i), kind="ghost", height=30, padx=10).pack(
                    side="right")
            if selected:
                label(row, "使用中", th.f_small_bold, T.ACCENT).pack(side="right", padx=px(10))
            for w in (row, avatar, text, *text.winfo_children()):
                w.bind("<Button-1>", lambda e, i=index: self.select(i))

    def select(self, index):
        self.app.set_account(index)
        self.render()

    def remove(self, index):
        account = self.app.accounts[index]
        if not self.app.dialog("删除账号", "确定要删除账号「{}」吗？".format(account["name"]), "warn",
                               [("删除", True, "primary"), ("取消", False, "secondary")]):
            return
        self.app.remove_account(index)
        self.render()

    def rename(self, index):
        account = self.app.accounts[index]
        name = ask_string(self.app.root, self.th, "修改名称", "离线账号的玩家名称（不超过 16 个字符，不能有空格）",
                          account["name"], _validate_offline)
        if name and name != account["name"]:
            self.app.replace_account(index, acc.offline_account(name))
            self.render()

    def add_offline(self):
        name = ask_string(self.app.root, self.th, "添加离线账号", "输入玩家名称（不超过 16 个字符，不能有空格）",
                          "", _validate_offline, "添加")
        if name:
            self.app.add_account(acc.offline_account(name))
            self.render()

    def add_authlib(self):
        account = AuthlibLogin(self.app).result
        if account:
            self.app.add_account(account)
            self.render()

    def add_msa(self):
        client_id = self.app.client_id_var.get().strip()
        if not client_id:
            choice = self.app.dialog(
                "需要先设置 Client ID",
                "微软登录需要一个在 Azure 注册的应用 Client ID。\n\n"
                "1. 在 Azure 门户注册应用，账户类型选「个人 Microsoft 帐户」，并开启「允许公共客户端流」\n"
                "2. 向微软提交 Minecraft 接口权限申请（aka.ms/mce-reviewappid），审核通过后即可使用\n"
                "3. 把 Client ID 填到「设置 → 账号」中",
                "info", [("查看注册教程", "guide", "secondary"), ("前往设置", "settings", "primary")])
            if choice == "guide":
                webbrowser.open(acc.MSA_APP_GUIDE)
            elif choice == "settings":
                self.close()
                self.app.show_page("settings")
            return
        account = MsaLogin(self.app, client_id).result
        if account:
            self.app.add_account(account)
            self.render()


class AuthlibLogin:
    def __init__(self, app):
        self.app, th, px = app, app.theme, app.px
        self.result = None
        self.top = top = create_modal(app.root, "外置登录")
        self.server_var = tk.StringVar(value=acc.LITTLESKIN_API)
        self.user_var = tk.StringVar()
        self.pass_var = tk.StringVar()

        body = tk.Frame(top, bg=T.BG)
        body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(4)))
        label(body, "外置登录", th.f_h3).pack(anchor="w")
        label(body, "使用 LittleSkin 等皮肤站账号登录（authlib-injector），联机时能显示皮肤", th.f_body,
              T.MUTED).pack(anchor="w", pady=(px(4), px(16)))
        for text, var, secret in (("认证服务器", self.server_var, False), ("邮箱或用户名", self.user_var, False),
                                  ("密码", self.pass_var, True)):
            label(body, text, th.f_small_bold, T.MUTED).pack(anchor="w", pady=(0, px(6)))
            field = Field(body, th, var)
            field.pack(fill="x", pady=(0, px(12)))
            if secret:
                field.entry.configure(show="•")
                field.entry.bind("<Return>", lambda e: self.login())
        self.status = label(body, "", th.f_small, T.ERROR, wraplength=px(420), justify="left")
        self.status.pack(anchor="w")

        bar = tk.Frame(top, bg=T.BG)
        bar.pack(fill="x", padx=px(24), pady=(px(8), px(20)))
        self.login_btn = Button(bar, th, "登录", self.login, height=34, padx=22)
        self.login_btn.pack(side="right", padx=(px(8), 0))
        Button(bar, th, "取消", self.close, kind="secondary", height=34, padx=20).pack(side="right")
        top.bind("<Escape>", lambda e: self.close())
        top.protocol("WM_DELETE_WINDOW", self.close)
        present_modal(app.root, top, px(480))

    def close(self):
        if self.top.winfo_exists():
            self.top.grab_release()
            self.top.destroy()

    def login(self):
        server, user, password = self.server_var.get(), self.user_var.get().strip(), self.pass_var.get()
        if not user or not password:
            self.status.configure(text="请填写账号和密码", fg=T.ERROR)
            return
        self.login_btn.set_enabled(False)
        self.status.configure(text="正在登录…", fg=T.MUTED)

        def job():
            api, meta = acc.resolve_yggdrasil(server)
            token, profiles = acc.yggdrasil_login(api, user, password)
            return api, meta, token, profiles

        def done(result):
            if not self.top.winfo_exists():
                return
            api, meta, token, profiles = result
            profile = token.get("selectedProfile")
            if not profile:
                profile = self._choose(profiles)
                if not profile:
                    self.login_btn.set_enabled(True)
                    self.status.configure(text="")
                    return
                self.app.spawn(lambda: acc.yggdrasil_select(api, token, profile),
                               lambda t: self._finish(api, meta, user, t, profile), failed)
                return
            self._finish(api, meta, user, token, profile)

        def failed(error):
            if self.top.winfo_exists():
                self.login_btn.set_enabled(True)
                self.status.configure(text=str(error), fg=T.ERROR)

        self.app.spawn(job, done, failed)

    def _finish(self, api, meta, user, token, profile):
        self.result = acc.authlib_account(api, meta, user, token, profile)
        self.close()

    def _choose(self, profiles):
        buttons = [(p["name"], p, "secondary") for p in profiles[:4]]
        return self.app.dialog("选择角色", "这个账号下有多个角色，请选择要使用的一个。", "info", buttons)


class MsaLogin:
    def __init__(self, app, client_id):
        self.app, th, px = app, app.theme, app.px
        self.client_id = client_id
        self.result = None
        self.cancelled = False
        self.top = top = create_modal(app.root, "微软登录")
        self.code = None

        body = tk.Frame(top, bg=T.BG)
        body.pack(fill="both", expand=True, padx=px(28), pady=(px(24), px(4)))
        label(body, "微软正版登录", th.f_h3).pack(anchor="w")
        self.hint = label(body, "正在获取登录代码…", th.f_body, T.MUTED, wraplength=px(440), justify="left")
        self.hint.pack(anchor="w", pady=(px(6), px(14)))
        self.code_label = label(body, "", th.f_h1, T.ACCENT)
        self.code_label.pack(anchor="w")
        self.status = label(body, "", th.f_small, T.MUTED, wraplength=px(440), justify="left")
        self.status.pack(anchor="w", pady=(px(10), 0))

        bar = tk.Frame(top, bg=T.BG)
        bar.pack(fill="x", padx=px(24), pady=(px(14), px(20)))
        self.open_btn = Button(bar, th, "复制代码并打开登录页", self.open_page, icon="link", height=34, padx=18)
        self.open_btn.pack(side="right", padx=(px(8), 0))
        self.open_btn.set_enabled(False)
        Button(bar, th, "取消", self.close, kind="secondary", height=34, padx=20).pack(side="right")
        top.bind("<Escape>", lambda e: self.close())
        top.protocol("WM_DELETE_WINDOW", self.close)
        app.spawn(lambda: acc.msa_device_code(client_id), self._got_code, self._failed)
        present_modal(app.root, top, px(500))
        self.cancelled = True

    def close(self):
        self.cancelled = True
        if self.top.winfo_exists():
            self.top.grab_release()
            self.top.destroy()

    def _failed(self, error):
        if self.top.winfo_exists():
            self.status.configure(text=str(error), fg=T.ERROR)

    def _got_code(self, data):
        if not self.top.winfo_exists():
            return
        self.code = data
        self.hint.configure(text="点击下方按钮打开微软登录页面（{}），输入下面的代码并登录你的微软账号：".format(
            data["verification_uri"]))
        self.code_label.configure(text=data["user_code"])
        self.status.configure(text="等待你在浏览器中完成登录…")
        self.open_btn.set_enabled(True)
        threading.Thread(target=self._poll, args=(data,), daemon=True).start()

    def open_page(self):
        self.app.root.clipboard_clear()
        self.app.root.clipboard_append(self.code["user_code"])
        webbrowser.open(self.code["verification_uri"])

    def _poll(self, data):
        interval = int(data.get("interval", 5))
        deadline = time.time() + int(data.get("expires_in", 900))
        while not self.cancelled and time.time() < deadline:
            time.sleep(interval)
            if self.cancelled:
                return
            try:
                tokens = acc.msa_poll(self.client_id, data["device_code"])
                if tokens is None:
                    continue
                self.app.ui(self._set_status, "已授权，正在登录 Minecraft…", T.MUTED)
                account = acc.msa_account(tokens)
            except Exception as e:
                self.app.ui(self._set_status, str(e), T.ERROR)
                return
            self.app.ui(self._done, account)
            return
        if not self.cancelled:
            self.app.ui(self._set_status, "登录代码已过期，请重新开始", T.ERROR)

    def _set_status(self, text, color):
        if self.top.winfo_exists():
            self.status.configure(text=text, fg=color)

    def _done(self, account):
        self.result = account
        self.close()
