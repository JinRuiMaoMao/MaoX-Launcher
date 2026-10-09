"""多人联机：集成 Terracotta | 陶瓦联机。

陶瓦联机基于 EasyTier 的 P2P 组网，无需公网 IP，邀请码与 HMCL、PCL 社区版互通。
启动器只下载并运行未经修改的官方程序，通过它提供的本地 HTTP 接口交互。
Terracotta 版权归 Burning_TNT 所有，以 AGPL-3.0 许可发布：https://github.com/burningtnt/Terracotta
"""
import hashlib
import http.client
import json
import locale
import os
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
from urllib.parse import urlencode

from .download import DownloadTask

VERSION = "0.4.2"
PROJECT_URL = "https://github.com/burningtnt/Terracotta"
NODE_LIST_URL = "https://terracotta.glavo.site/nodes"
DOWNLOADS = [
    "https://github.com/burningtnt/Terracotta/releases/download/v{version}/{name}",
    "https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v{version}/{name}",
    "https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{version}/{name}",
]
# SHA-512，与 HMCL 内置的校验值一致
PACKAGES = {
    "windows-x86_64": (
        "6a98f524d4f00373696517306af8aa50d01d55ce4eadb27e9e4bc2f882707a0b5f20d5d4c33371d1459dcf5bf144ffed9beb414202d9ccf32b11dbbfcf19d650",
        {
            "VCRUNTIME140.DLL": "3d4b24061f72c0e957c7b04a0c4098c94c8f1afb4a7e159850b9939c7210d73398be6f27b5ab85073b4e8c999816e7804fef0f6115c39cd061f4aaeb4dcda8cf",
            "terracotta-0.4.2-windows-x86_64.exe": "6e98d1f2380ed22fb5a2dd4aafce6c773e9cf69100c8bb8e49e7d6983756bdb9a31f80e06bcfbe5a2742144fe806d3d687dec54d8f09d87c659341f99dd9fd80",
        },
    ),
    "windows-arm64": (
        "fc1077247014ac0c712469498bde2ef7f6d881d5fcb7bdd5e11ebe20218fed365be19afdb8d453a79d77b729f866058522b910741767f4df947faa891434b463",
        {
            "VCRUNTIME140.DLL": "5cb5ce114614101d260f4754c09e8a0dd57e4da885ebb96b91e274326f3e1dd95ed0ade9f542f1922fad0ed025e88a1f368e791e1d01fae69718f0ec3c7b98c8",
            "terracotta-0.4.2-windows-arm64.exe": "30a15c5c53e5817c5a3634532172559327474741d3b2c7ef4e8a30acc6f59cdcf3570bf5f583e3cbe9e2abc8253e977c1abda1e9f36c88c4e99240da257347d0",
        },
    ),
    "linux-x86_64": (
        "d326ad95815d04568d485b5038e40ffc47ca54292fa0925eee6f5cea014024f901d661708aac2a743037b990882ad82b4d0b7bb03dc3b2fe720dbf0f3efe1c98",
        {"terracotta-0.4.2-linux-x86_64": "fac328ba8957a711b03557bb913940f22d61b76608cd203fdf51024b6f94b19f5bc91c9b8a9fa80baf6968e1e6873c1880fd4cf54a2f8e3c6cf1e6ac161f8d0c"},
    ),
    "linux-arm64": (
        "57c08f48d9535e93ad547d2dfc852d267992cc164a7208b42a2da0a6cbc2f21862f610e02a746b4b67150f4dec26b86a4f96eb9bd2f58d124d5b40ba50c6d55e",
        {"terracotta-0.4.2-linux-arm64": "d807744c2041c98686e4b505324713badea7a0f31e8810be49ae053a63fb6dfc474ac58d678fb93eea0dd5cccff7372d9ec6135a1046f4b306cad35cd90ecacd"},
    ),
}

EXCEPTIONS = [
    "无法连接到房主，房间可能已经关闭",
    "与房主的连接已断开",
    "联机组件意外退出，请重新加入",
    "联机组件意外退出，请重新创建房间",
    "房主的游戏世界已关闭",
    "房主使用的联机协议不兼容，请双方更新启动器后重试",
]
DIFFICULTIES = {
    "EASIEST": "网络条件极佳，即将连接",
    "SIMPLE": "网络条件良好，即将连接",
    "MEDIUM": "网络条件一般，正在尝试打洞或中继",
    "TOUGH": "网络条件较差，连接可能不稳定",
    "UNKNOWN": "正在检测网络状况",
}


class TerracottaError(Exception):
    pass


def classifier():
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x86_64" if machine in ("amd64", "x86_64") else machine
    system = {"win32": "windows", "linux": "linux"}.get(sys.platform, sys.platform)
    name = "{}-{}".format(system, arch)
    if name.startswith("windows") and sys.getwindowsversion().major < 10:
        return None
    return name if name in PACKAGES else None


def _sha512(path):
    h = hashlib.sha512()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _in_china():
    try:
        lang = locale.getdefaultlocale()[0] or ""
    except ValueError:
        lang = ""
    return lang.lower() in ("zh_cn", "zh_hans_cn") or time.timezone == -8 * 3600


class Terracotta:
    def __init__(self, root, downloader, log=print):
        self.cls = classifier()
        self.dir = os.path.join(root, VERSION)
        self.dl = downloader
        self.log = log
        self.port = None
        self._verified = False
        self._nodes = None

    @property
    def supported(self):
        return self.cls is not None

    def _files(self):
        return PACKAGES[self.cls][1]

    def executable(self):
        return os.path.join(self.dir, next(n for n in self._files() if n.startswith("terracotta-")))

    def installed(self):
        if not self.supported:
            return False
        if self._verified:
            return True
        try:
            ok = all(_sha512(os.path.join(self.dir, name)) == sha for name, sha in self._files().items())
        except OSError:
            ok = False
        self._verified = ok
        return ok

    def install(self, progress=None):
        """下载并解压陶瓦联机，校验每个文件的 SHA-512。"""
        if not self.supported:
            raise TerracottaError("当前系统暂不支持陶瓦联机")
        name = "terracotta-{}-{}-pkg.tar.gz".format(VERSION, self.cls)
        urls = [u.format(version=VERSION, name=name) for u in DOWNLOADS]
        archive = os.path.join(tempfile.gettempdir(), "maox-" + name)
        package_hash = PACKAGES[self.cls][0]
        if not (os.path.isfile(archive) and _sha512(archive) == package_hash):
            if os.path.isfile(archive):
                os.remove(archive)
            self.log("正在下载陶瓦联机 {}...".format(VERSION))
            self.dl.download_many([DownloadTask(urls[0], archive, alternates=urls[1:])], progress)
            if _sha512(archive) != package_hash:
                os.remove(archive)
                raise TerracottaError("陶瓦联机安装包校验失败，请重试")
        os.makedirs(self.dir, exist_ok=True)
        with tarfile.open(archive) as tar:
            members = {os.path.basename(m.name): m for m in tar.getmembers() if m.isfile()}
            for filename, sha in self._files().items():
                member = members.get(filename)
                if member is None:
                    raise TerracottaError("安装包中缺少 {}".format(filename))
                data = tar.extractfile(member).read()
                if hashlib.sha512(data).hexdigest() != sha:
                    raise TerracottaError("{} 校验失败".format(filename))
                dest = os.path.join(self.dir, filename)
                with open(dest, "wb") as f:
                    f.write(data)
                if not filename.lower().endswith((".exe", ".dll")):
                    os.chmod(dest, 0o755)
        os.remove(archive)
        self._verified = True
        self.log("陶瓦联机安装完成")

    # ------------------------------------------------------------------ 进程与接口

    def _request(self, path, params=None, timeout=5):
        if not self.port:
            raise TerracottaError("联机服务未启动")
        url = path + ("?" + urlencode(params, doseq=True) if params else "")
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        try:
            conn.request("GET", url)
            resp = conn.getresponse()
            body = resp.read()
        except OSError as e:
            raise TerracottaError("无法连接联机服务：{}".format(e))
        finally:
            conn.close()
        return resp.status, body

    def alive(self):
        try:
            return self._request("/meta", timeout=2)[0] == 200
        except TerracottaError:
            return False

    def start(self, timeout=20):
        """启动（或接管已运行的）陶瓦联机后台进程，记录它的 HTTP 端口。"""
        if self.port and self.alive():
            return
        port_file = os.path.join(tempfile.mkdtemp(prefix="maox-terracotta-"), "http")
        kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
        proc = subprocess.Popen([self.executable(), "--hmcl", port_file], cwd=self.dir, stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **kwargs)
        deadline = time.time() + timeout
        while time.time() < deadline:
            if os.path.isfile(port_file):
                try:
                    with open(port_file, encoding="utf-8") as f:
                        self.port = int(json.load(f)["port"])
                    break
                except (OSError, ValueError, KeyError):
                    pass
            if proc.poll() not in (None, 0):
                raise TerracottaError("陶瓦联机启动失败（退出码 {}）".format(proc.returncode))
            time.sleep(0.2)
        else:
            proc.kill()
            raise TerracottaError("陶瓦联机启动超时")
        shutil.rmtree(os.path.dirname(port_file), ignore_errors=True)
        self.log("陶瓦联机已启动（本地端口 {}）".format(self.port))

    def meta(self):
        return json.loads(self._request("/meta")[1].decode("utf-8"))

    def state(self):
        status, body = self._request("/state")
        if status != 200:
            raise TerracottaError("获取联机状态失败（HTTP {}）".format(status))
        return json.loads(body.decode("utf-8"))

    def public_nodes(self):
        """额外的公共节点列表（与 HMCL 使用同一来源），获取失败时只用陶瓦内置的节点。"""
        if self._nodes is None:
            try:
                nodes = self.dl.fetch_json(NODE_LIST_URL, mirror=False)
                china = _in_china()
                self._nodes = [n["url"] for n in nodes if isinstance(n, dict) and n.get("url")
                               and (not n.get("region") or (n["region"].upper() == "CN") == china)]
            except Exception:
                self._nodes = []
        return self._nodes

    def host(self, player):
        self._request("/state/scanning", {"player": player, "public_nodes": self.public_nodes()})

    def join(self, room, player):
        status, _ = self._request("/state/guesting",
                                  {"room": room, "player": player, "public_nodes": self.public_nodes()})
        if status == 400:
            raise TerracottaError("邀请码无效，请检查后重试")
        if status != 200:
            raise TerracottaError("加入房间失败（HTTP {}）".format(status))

    def leave(self):
        self._request("/state/ide")

    def shutdown(self):
        if self.port:
            try:
                self._request("/panic", {"peaceful": "true"}, timeout=2)
            except TerracottaError:
                pass
            self.port = None
