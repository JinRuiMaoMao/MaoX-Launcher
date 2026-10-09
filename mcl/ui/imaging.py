"""把任意格式的图标（PNG / JPEG / WebP / GIF）缩放成 Tk 能直接显示的 PNG。

Windows 上通过系统自带的 WIC 解码（支持 WebP、JPEG），其他平台只处理 Tk 原生支持的 PNG / GIF。
"""
import ctypes
import struct
import sys
import zlib

_wic = None


def _png(width, height, rgb):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    stride = width * 3
    raw = b"".join(b"\x00" + rgb[y * stride:(y + 1) * stride] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


class _Wic:
    def __init__(self):
        from ctypes import POINTER, byref, c_double, c_int, c_uint, c_void_p, wintypes

        class GUID(ctypes.Structure):
            _fields_ = [("d1", wintypes.DWORD), ("d2", wintypes.WORD), ("d3", wintypes.WORD),
                        ("d4", ctypes.c_ubyte * 8)]

        self.ole32 = ctypes.OleDLL("ole32")
        shlwapi = ctypes.WinDLL("shlwapi")
        self.mem_stream = shlwapi.SHCreateMemStream
        self.mem_stream.restype = c_void_p
        self.mem_stream.argtypes = [ctypes.c_char_p, c_uint]

        def guid(text):
            g = GUID()
            self.ole32.CLSIDFromString(ctypes.c_wchar_p(text), byref(g))
            return g

        self.clsid_factory = guid("{cacaf262-9370-4615-a13b-9f5539da4c0a}")
        self.iid_factory = guid("{ec5ec8a9-c395-4314-9c77-54d7a935ff70}")
        self.bgra = guid("{6fddc324-4e03-4bfe-b185-3d77768dc90f}")
        self.t = dict(POINTER=POINTER, byref=byref, c_double=c_double, c_int=c_int, c_uint=c_uint,
                      c_void_p=c_void_p, GUID=GUID)

    @staticmethod
    def _method(obj, index, *argtypes):
        vtable = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        fn = ctypes.WINFUNCTYPE(ctypes.HRESULT, ctypes.c_void_p, *argtypes)(vtable[index])
        return lambda *args: fn(obj, *args)

    @staticmethod
    def _release(obj):
        if obj:
            vtable = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
            ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])(obj)

    def decode(self, data, size, cover=None):
        """返回 (宽, 高, BGRA 像素)，等比缩放到不超过 size；指定 cover=(宽, 高) 时缩放到刚好铺满该区域。"""
        t = self.t
        P, byref, c_uint, c_void_p = t["POINTER"], t["byref"], t["c_uint"], t["c_void_p"]
        try:
            self.ole32.CoInitializeEx(None, 0)
        except OSError:
            pass
        objects = []
        try:
            factory = c_void_p()
            self.ole32.CoCreateInstance(byref(self.clsid_factory), None, 1, byref(self.iid_factory), byref(factory))
            objects.append(factory.value)
            stream = self.mem_stream(data, len(data))
            if not stream:
                return None
            objects.append(stream)
            decoder = c_void_p()
            self._method(factory, 4, c_void_p, c_void_p, t["c_int"], P(c_void_p))(stream, None, 0, byref(decoder))
            objects.append(decoder.value)
            frame = c_void_p()
            self._method(decoder, 13, c_uint, P(c_void_p))(0, byref(frame))
            objects.append(frame.value)
            w, h = c_uint(), c_uint()
            self._method(frame, 3, P(c_uint), P(c_uint))(byref(w), byref(h))
            if cover:
                scale = max(cover[0] / w.value, cover[1] / h.value)
            else:
                scale = min(size / w.value, size / h.value)
            tw, th = max(1, round(w.value * scale)), max(1, round(h.value * scale))
            scaler = c_void_p()
            self._method(factory, 11, P(c_void_p))(byref(scaler))
            objects.append(scaler.value)
            # WICBitmapInterpolationModeFant：缩小时质量最好
            self._method(scaler, 8, c_void_p, c_uint, c_uint, t["c_int"])(frame, tw, th, 3)
            converter = c_void_p()
            self._method(factory, 10, P(c_void_p))(byref(converter))
            objects.append(converter.value)
            self._method(converter, 8, c_void_p, P(t["GUID"]), t["c_int"], c_void_p, t["c_double"], t["c_int"])(
                scaler, byref(self.bgra), 0, None, 0.0, 0)
            buf = ctypes.create_string_buffer(tw * th * 4)
            self._method(converter, 7, c_void_p, c_uint, c_uint, P(ctypes.c_char))(None, tw * 4, len(buf), buf)
            return tw, th, buf.raw
        except OSError:
            return None
        finally:
            for obj in reversed(objects):
                self._release(obj)


def _decode(data, size, cover=None):
    global _wic
    if sys.platform != "win32":
        return None
    if _wic is None:
        try:
            _wic = _Wic()
        except (OSError, AttributeError):
            _wic = False
    return _wic.decode(data, size, cover) if _wic else None


def banner_png(data, width, height, bg, fade, fade_bottom=0, dim=0.82):
    """把图片铺满 width×height（居中裁切），整体压暗，左侧 fade、底部 fade_bottom 像素渐隐到背景色，返回 PNG 字节。"""
    decoded = _decode(data, 0, (width, height))
    if decoded is None:
        return None
    w, h, bgra = decoded
    ox, oy = (w - width) // 2, (h - height) // 2
    rows = [bgra[((oy + y) * w + ox) * 4:((oy + y) * w + ox + width) * 4] for y in range(height)]
    src = b"".join(rows)
    rgb = bytearray(width * height * 3)
    rgb[0::3], rgb[1::3], rgb[2::3] = src[2::4], src[1::4], src[0::4]
    rgb = bytearray(bytes(rgb).translate(bytes(int(v * dim) for v in range(256))))
    stride = width * 3
    background = _hex_rgb(bg)
    fade = min(fade, width)
    for x in range(fade):
        t = (x / fade) ** 1.6
        for c in range(3):
            table = bytes(int(v * t + background[c] * (1 - t)) for v in range(256))
            rgb[x * 3 + c::stride] = bytes(rgb[x * 3 + c::stride]).translate(table)
    fade_bottom = min(fade_bottom, height)
    for i in range(fade_bottom):
        y = height - 1 - i
        t = (i / fade_bottom) ** 1.6
        for c in range(3):
            table = bytes(int(v * t + background[c] * (1 - t)) for v in range(256))
            start = y * stride + c
            rgb[start:start + stride - c:3] = bytes(rgb[start:start + stride - c:3]).translate(table)
    return _png(width, height, bytes(rgb))


def _hex_rgb(color):
    return int(color[1:3], 16), int(color[3:5], 16), int(color[5:7], 16)


def icon_png(data, size, bg, radius):
    """把图标解码、缩放、居中并裁成圆角，合成到背景色上，返回 PNG 字节；无法解码时返回 None。"""
    decoded = _decode(data, size)
    if decoded is None:
        return None
    w, h, bgra = decoded
    br, bgc, bb = _hex_rgb(bg)
    ox, oy = (size - w) // 2, (size - h) // 2
    out = bytearray(bytes((br, bgc, bb)) * (size * size))
    r = float(radius)
    for y in range(h):
        sy = oy + y
        for x in range(w):
            i = (y * w + x) * 4
            a = bgra[i + 3]
            if a == 0:
                continue
            sx = ox + x
            # 圆角：只对四个角落的像素计算覆盖率，带抗锯齿
            cx = r - sx - 0.5 if sx < r else sx + 0.5 - (size - r) if sx >= size - r else 0
            cy = r - sy - 0.5 if sy < r else sy + 0.5 - (size - r) if sy >= size - r else 0
            if cx > 0 and cy > 0:
                cover = r - (cx * cx + cy * cy) ** 0.5 + 0.5
                if cover <= 0:
                    continue
                a = int(a * min(cover, 1.0))
            o = (sy * size + sx) * 3
            inv = 255 - a
            out[o] = (bgra[i + 2] * a + out[o] * inv) // 255
            out[o + 1] = (bgra[i + 1] * a + out[o + 1] * inv) // 255
            out[o + 2] = (bgra[i] * a + out[o + 2] * inv) // 255
    return _png(size, size, bytes(out))
