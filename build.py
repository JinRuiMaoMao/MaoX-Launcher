"""把 MaoX Launcher 打包成单个 exe：python build.py

在 .build-venv 虚拟环境中安装 PyInstaller（不影响系统 Python），生成图标后输出 dist/MaoX Launcher.exe。
"""
import os
import shutil
import struct
import subprocess
import sys
import zlib

ROOT = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(ROOT, ".build-venv")
BUILD = os.path.join(ROOT, "build")
DIST = os.path.join(ROOT, "dist")
NAME = "MaoX Launcher"
ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)

sys.path.insert(0, ROOT)
from mcl.ui import theme as T  # noqa: E402
from mcl.ui.widgets import _inside, logo_polygons  # noqa: E402


def _rgb(color):
    return int(color[1:3], 16), int(color[3:5], 16), int(color[5:7], 16)


def _png_rgba(size, pixels):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + bytes(pixels[y * size * 4:(y + 1) * size * 4]) for y in range(size))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def render_icon(size):
    """与窗口图标相同的标志：圆角深色底 + M/X 标志，4×4 超采样，圆角外透明。"""
    bg, bar, cross = _rgb(T.BG), _rgb(T.TEXT), _rgb(T.ACCENT)
    pad = size * 0.2
    bars, crosses = logo_polygons(pad, pad, size - 2 * pad)
    layers = [(p, bar) for p in bars] + [(p, cross) for p in crosses]
    radius = size * 0.22
    n = 4
    offsets = [(i + 0.5) / n for i in range(n)]
    pixels = bytearray(size * size * 4)
    for py in range(size):
        for px in range(size):
            r = g = b = a = 0
            for oy in offsets:
                for ox in offsets:
                    x, y = px + ox, py + oy
                    cx = min(max(x, radius), size - radius)
                    cy = min(max(y, radius), size - radius)
                    if (x - cx) ** 2 + (y - cy) ** 2 > radius ** 2:
                        continue
                    color = bg
                    for poly, c in layers:
                        if _inside(poly, x, y):
                            color = c
                    r += color[0]
                    g += color[1]
                    b += color[2]
                    a += 1
            i = (py * size + px) * 4
            if a:
                pixels[i:i + 4] = bytes((r // a, g // a, b // a, a * 255 // (n * n)))
    return _png_rgba(size, pixels)


def write_icon(path):
    images = [render_icon(size) for size in ICON_SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, png in zip(ICON_SIZES, images):
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    with open(path, "wb") as f:
        f.write(header + entries + data)


def run(*args):
    print(">", " ".join('"{}"'.format(a) if " " in a else a for a in args), flush=True)
    subprocess.check_call(args, cwd=ROOT)


def main():
    python = os.path.join(VENV, "Scripts", "python.exe")
    if not os.path.isfile(python):
        run(sys.executable, "-m", "venv", VENV)
    run(python, "-m", "pip", "install", "--disable-pip-version-check", "-q", "pyinstaller")

    os.makedirs(BUILD, exist_ok=True)
    icon = os.path.join(BUILD, "icon.ico")
    print("> 生成图标", icon, flush=True)
    write_icon(icon)

    run(python, "-m", "PyInstaller", "--noconfirm", "--clean", "--onefile", "--windowed",
        "--name", NAME, "--icon", icon,
        "--distpath", DIST, "--workpath", os.path.join(BUILD, "pyinstaller"), "--specpath", BUILD,
        os.path.join(ROOT, "main.py"))
    exe = os.path.join(DIST, NAME + ".exe")
    print("\n完成：{}（{:.1f} MB）".format(exe, os.path.getsize(exe) / 1048576))
    print("exe 放在哪个文件夹，游戏、配置和工具就保存在哪个文件夹。")


if __name__ == "__main__":
    if "--clean" in sys.argv:
        for path in (BUILD, DIST):
            shutil.rmtree(path, ignore_errors=True)
    main()
