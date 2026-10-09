import base64
import os
import tkinter as tk

from . import theme as T
from .imaging import banner_png


class Banner:
    """启动页横幅右上方的自定义背景图：位于标题右侧、操作栏上方，左边缘和底部渐隐到卡片底色。"""

    def __init__(self, app, hero, watermark, title, controls):
        self.app, self.hero, self.watermark = app, hero, watermark
        self.title, self.controls = title, controls
        self.canvas = tk.Canvas(hero, bg=T.CARD, highlightthickness=0, bd=0)
        self.data = None
        self.image = None
        self.size = None
        self._job = None
        self._gen = 0
        for widget in (hero, title, controls):
            widget.bind("<Configure>", lambda e: self._schedule(), add="+")

    def set_image(self, path):
        self.data = None
        if path and os.path.isfile(path):
            try:
                with open(path, "rb") as f:
                    self.data = f.read()
            except OSError:
                self.data = None
        self.size = None
        if self.data:
            self._schedule(0)
        else:
            self.canvas.place_forget()
            self.watermark.place(relx=1.0, rely=0.5, x=-self.app.px(40), anchor="e")

    def _schedule(self, delay=200):
        if not self.data:
            return
        if self._job:
            self.hero.after_cancel(self._job)
        self._job = self.hero.after(delay, self._render)

    def _render(self):
        self._job = None
        hero_w, hero_h = self.hero.winfo_width() - 2, self.hero.winfo_height() - 2
        if hero_w < 50 or hero_h < 50:
            self._schedule()
            return
        left = max(int(hero_w * 0.36), self.title.winfo_x() + self.title.winfo_width() + self.app.px(24))
        width = hero_w - left
        height = self.controls.winfo_y() - self.app.px(6)
        if width < 80 or height < 60:
            width, height = 0, 0
        if self.size == (width, height):
            return
        self.size = (width, height)
        self._gen += 1
        if not width:
            self.canvas.place_forget()
            return
        gen, data, fade, fade_bottom = self._gen, self.data, int(width * 0.45), int(height * 0.4)

        def done(png):
            if gen != self._gen or not self.data:
                return
            image = None
            if png:
                image = tk.PhotoImage(data=base64.b64encode(png))
            elif data[:8] == b"\x89PNG\r\n\x1a\n" or data[:4] == b"GIF8":
                image = tk.PhotoImage(data=base64.b64encode(data))
            if image is None:
                self.app.toast("无法读取背景图片", "error")
                return
            self.image = image
            self.canvas.delete("all")
            self.canvas.create_image(width, 0, image=image, anchor="ne")
            self.watermark.place_forget()
            self.canvas.place(relx=1.0, y=1, x=-1, anchor="ne", width=width, height=height)
            self.canvas.tk.call("lower", self.canvas._w)

        self.app.spawn(lambda: banner_png(data, width, height, T.CARD, fade, fade_bottom), done,
                       lambda e: done(None))
