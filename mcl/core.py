import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import uuid
import zipfile
from . import java as javautil
from .download import Downloader, DownloadTask

LAUNCHER_NAME = "MaoXLauncher"
LAUNCHER_VERSION = "1.0.0"

VERSION_MANIFEST_URL = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
LIBRARIES_URL = "https://libraries.minecraft.net/"
RESOURCES_URL = "https://resources.download.minecraft.net/"
LEGACY_MAVEN = [
    ("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
    ("https://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
    ("http://", "https://"),
]
FALLBACK_MAVENS = [
    "https://maven.minecraftforge.net/",
    "https://libraries.minecraft.net/",
    "https://repo1.maven.org/maven2/",
]
LOADER_LIBRARIES = {
    "org.quiltmc:quilt-loader": "quilt",
    "net.fabricmc:fabric-loader": "fabric",
    "net.neoforged:neoforge": "neoforge",
    "net.neoforged:forge": "neoforge",
    "net.minecraftforge:forge": "forge",
    "optifine:OptiFine": "optifine",
}
LOADER_NAMES = {"forge": "Forge", "neoforge": "NeoForge", "fabric": "Fabric", "quilt": "Quilt", "optifine": "OptiFine"}
VERSION_SETTINGS = "maox.json"

if sys.platform == "win32":
    OS_NAME = "windows"
elif sys.platform == "darwin":
    OS_NAME = "osx"
else:
    OS_NAME = "linux"


def _detect_arch():
    machine = platform.machine().lower()
    if machine in ("arm64", "aarch64"):
        return "arm64"
    if machine in ("amd64", "x86_64"):
        return "x86_64"
    return "x86"


ARCH = _detect_arch()
IS_64BIT = ARCH != "x86"

LEGACY_JVM_ARGS = [
    "-Djava.library.path=${natives_directory}",
    "-Dminecraft.launcher.brand=${launcher_name}",
    "-Dminecraft.launcher.version=${launcher_version}",
    "-cp", "${classpath}",
]
if OS_NAME == "windows":
    LEGACY_JVM_ARGS.insert(0, "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump")

_PLACEHOLDER = re.compile(r"\$\{(\w+)\}")


def rule_matches(rule, features):
    os_rule = rule.get("os")
    if os_rule:
        if "name" in os_rule and os_rule["name"] != OS_NAME:
            return False
        if "arch" in os_rule and os_rule["arch"] != ARCH:
            return False
        if "version" in os_rule and not re.search(os_rule["version"], platform.version()):
            return False
    for key, expected in (rule.get("features") or {}).items():
        if bool(features.get(key)) != expected:
            return False
    return True


def rules_allow(rules, features=None):
    if not rules:
        return True
    allowed = False
    for rule in rules:
        if rule_matches(rule, features or {}):
            allowed = rule.get("action") == "allow"
    return allowed


def maven_path(name):
    """group:artifact:version[:classifier][@ext] -> group/path/artifact/version/artifact-version[-classifier].ext"""
    ext = "jar"
    if "@" in name:
        name, ext = name.split("@", 1)
    parts = name.split(":")
    group, artifact, version = parts[0], parts[1], parts[2]
    filename = "{}-{}".format(artifact, version)
    if len(parts) > 3:
        filename += "-" + parts[3]
    return "/".join(group.split(".") + [artifact, version, filename + "." + ext])


def offline_uuid(name):
    """与 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 一致。"""
    digest = bytearray(hashlib.md5(("OfflinePlayer:" + name).encode("utf-8")).digest())
    digest[6] = (digest[6] & 0x0F) | 0x30
    digest[8] = (digest[8] & 0x3F) | 0x80
    return uuid.UUID(bytes=bytes(digest)).hex


def _loader_from_json(data):
    args = [a for a in (data.get("arguments") or {}).get("game", []) if isinstance(a, str)]
    for flag, loader in (("--fml.neoForgeVersion", "neoforge"), ("--fml.forgeVersion", "forge")):
        if flag in args:
            i = args.index(flag)
            return loader, args[i + 1] if i + 1 < len(args) else None
    for lib in data.get("libraries", []):
        parts = lib.get("name", "").split(":")
        if len(parts) >= 3 and parts[0] + ":" + parts[1] in LOADER_LIBRARIES:
            loader = LOADER_LIBRARIES[parts[0] + ":" + parts[1]]
            if loader == "optifine":
                version = parts[2].split("_", 1)[-1]
            elif parts[1] == "forge":
                version = parts[2].split("-", 1)[-1]
            else:
                version = parts[2]
            return loader, version
    if "FMLTweaker" in data.get("minecraftArguments", ""):
        return "forge", None
    return None, None


def merge_version(parent, child):
    merged = dict(parent)
    for key, value in child.items():
        if key == "libraries":
            merged["libraries"] = value + parent.get("libraries", [])
        elif key == "arguments":
            parent_args = parent.get("arguments") or {}
            merged["arguments"] = {
                kind: parent_args.get(kind, []) + value.get(kind, []) for kind in ("game", "jvm")
            }
        elif key != "inheritsFrom":
            merged[key] = value
    return merged


class LogParser:
    """把 log4j 的 XML 控制台输出转换成普通文本行。"""

    def __init__(self):
        self.prefix = ""
        self.buffer = None

    def feed(self, line):
        if self.buffer is not None:
            end = line.find("]]>")
            if end < 0:
                self.buffer.append(line)
                return []
            self.buffer.append(line[:end])
            text, self.buffer = "\n".join(self.buffer), None
            return [self.prefix + text]

        stripped = line.strip()
        if stripped.startswith("<log4j:Event"):
            attrs = dict(re.findall(r'(\w+)="([^"]*)"', stripped))
            ts = attrs.get("timestamp", "")
            clock = time.strftime("%H:%M:%S", time.localtime(int(ts) / 1000)) if ts.isdigit() else ""
            self.prefix = "[{}] [{}/{}]: ".format(clock, attrs.get("thread", ""), attrs.get("level", ""))
            return []
        start = line.find("<![CDATA[")
        if start >= 0:
            rest = line[start + 9:]
            end = rest.find("]]>")
            if end >= 0:
                return [self.prefix + rest[:end]]
            self.buffer = [rest]
            return []
        if stripped.startswith("<log4j:") or stripped.startswith("</log4j:"):
            return []
        return [line]


class GameLauncher:
    def __init__(self, cfg, log=print, progress=None):
        self.cfg = cfg
        self.log = log
        self.progress = progress or (lambda done, total, text="": None)
        self.dl = Downloader(cfg.get("download_source", "auto"), cfg.get("download_threads", 16))
        self.manifest = None

    @property
    def mc_dir(self):
        return os.path.abspath(self.cfg["minecraft_dir"])

    def path(self, *parts):
        return os.path.join(self.mc_dir, *parts)

    def version_json_path(self, version_id):
        return self.path("versions", version_id, version_id + ".json")

    # ------------------------------------------------------------------ 版本管理

    def get_manifest(self):
        if self.manifest is None:
            self.log("正在获取版本列表...")
            self.manifest = self.dl.fetch_json(VERSION_MANIFEST_URL)
        return self.manifest

    def installed_versions(self):
        root = self.path("versions")
        if not os.path.isdir(root):
            return []
        found = []
        for name in os.listdir(root):
            json_path = self.version_json_path(name)
            if os.path.isfile(json_path):
                found.append((os.path.getmtime(json_path), name))
        return [name for _, name in sorted(found, reverse=True)]

    def ensure_version_json(self, version_id):
        json_path = self.version_json_path(version_id)
        if not os.path.isfile(json_path):
            entry = next((v for v in self.get_manifest()["versions"] if v["id"] == version_id), None)
            if entry is None:
                raise RuntimeError("找不到版本 {}".format(version_id))
            self.log("正在下载版本信息 {}...".format(version_id))
            self.dl.download_many([DownloadTask(entry["url"], json_path, entry.get("sha1"))])
        with open(json_path, encoding="utf-8") as f:
            parent = json.load(f).get("inheritsFrom")
        if parent:
            self.ensure_version_json(parent)

    def load_version(self, version_id):
        with open(self.version_json_path(version_id), encoding="utf-8") as f:
            data = json.load(f)
        parent_id = data.get("inheritsFrom")
        if parent_id:
            parent = self.load_version(parent_id)
            merged = merge_version(parent, data)
            merged["_jar"] = data.get("jar") or parent["_jar"]
            return merged
        data["_jar"] = data.get("jar") or version_id
        return data

    def detect_loader(self, version_id):
        """返回 (加载器, 加载器版本, Minecraft 版本)，原版的加载器为 None。"""
        loader = loader_version = None
        game = version_id
        current = version_id
        while current:
            try:
                with open(self.version_json_path(current), encoding="utf-8") as f:
                    data = json.load(f)
            except (OSError, ValueError):
                break
            if loader is None:
                loader, loader_version = _loader_from_json(data)
            game = current
            current = data.get("inheritsFrom")
        if loader == "forge" and "neoforge" in version_id.lower():
            loader = "neoforge"
        if loader_version and loader_version.endswith("-" + game):
            loader_version = loader_version[:-len(game) - 1]
        return loader, loader_version, game

    # ------------------------------------------------------------------ 文件准备

    def _collect_libraries(self, vjson):
        classpath, natives, tasks = [], [], []
        seen = set()
        for lib in vjson.get("libraries", []):
            if not rules_allow(lib.get("rules")) or lib.get("clientreq") is False:
                continue
            name = lib["name"]
            parts = name.split("@")[0].split(":")
            key = ":".join(parts[:2] + parts[3:])
            if key in seen:
                continue
            seen.add(key)

            downloads = lib.get("downloads") or {}
            artifact = downloads.get("artifact")
            base = (lib.get("url") or LIBRARIES_URL).rstrip("/") + "/"
            for old, new in LEGACY_MAVEN:
                if base.startswith(old):
                    base = new + base[len(old):]
            if artifact:
                full = self.path("libraries", *(artifact.get("path") or maven_path(name)).split("/"))
                if artifact.get("url"):
                    tasks.append(DownloadTask(artifact["url"], full, artifact.get("sha1"), artifact.get("size")))
                classpath.append(full)
            elif "natives" not in lib:
                rel = maven_path(name)
                full = self.path("libraries", *rel.split("/"))
                alternates = [b + rel for b in FALLBACK_MAVENS if b != base]
                tasks.append(DownloadTask(base + rel, full, lib.get("sha1"), lib.get("size"), alternates))
                classpath.append(full)

            classifier = (lib.get("natives") or {}).get(OS_NAME)
            if classifier:
                classifier = classifier.replace("${arch}", "64" if IS_64BIT else "32")
                info = (downloads.get("classifiers") or {}).get(classifier)
                if info:
                    full = self.path("libraries", *info["path"].split("/"))
                    tasks.append(DownloadTask(info["url"], full, info.get("sha1"), info.get("size")))
                else:
                    rel = maven_path(name + ":" + classifier)
                    full = self.path("libraries", *rel.split("/"))
                    tasks.append(DownloadTask(base + rel, full))
                natives.append((full, (lib.get("extract") or {}).get("exclude", [])))
        return classpath, natives, tasks

    def _collect_assets(self, vjson):
        index = vjson.get("assetIndex")
        if not index:
            return None, []
        index_path = self.path("assets", "indexes", index["id"] + ".json")
        self.dl.download_many([DownloadTask(index["url"], index_path, index.get("sha1"), index.get("size"))])
        with open(index_path, encoding="utf-8") as f:
            data = json.load(f)
        tasks = []
        for obj in data.get("objects", {}).values():
            h = obj["hash"]
            tasks.append(DownloadTask(RESOURCES_URL + h[:2] + "/" + h,
                                      self.path("assets", "objects", h[:2], h), h, obj.get("size")))
        return (index["id"], data), tasks

    def _extract_natives(self, natives, dest):
        os.makedirs(dest, exist_ok=True)
        for jar, excludes in natives:
            with zipfile.ZipFile(jar) as zf:
                for info in zf.infolist():
                    name = info.filename
                    if (name.endswith("/") or name.startswith("META-INF/") or ".." in name.split("/")
                            or any(name.startswith(e) for e in excludes)):
                        continue
                    target = os.path.join(dest, *name.split("/"))
                    if os.path.isfile(target) and os.path.getsize(target) == info.file_size:
                        continue
                    os.makedirs(os.path.dirname(target), exist_ok=True)
                    try:
                        with zf.open(info) as src, open(target, "wb") as out:
                            shutil.copyfileobj(src, out)
                    except PermissionError:
                        pass  # 文件正被运行中的游戏占用

    def _map_legacy_assets(self, asset_index, game_dir):
        """1.7.2 及更早的版本需要把资源文件按原始文件名展开。"""
        assets_root = self.path("assets")
        if not asset_index:
            return assets_root
        index_id, data = asset_index
        if data.get("map_to_resources"):
            target = os.path.join(game_dir, "resources")
        elif data.get("virtual"):
            target = self.path("assets", "virtual", index_id)
        else:
            return assets_root
        self.log("正在整理旧版资源文件...")
        for name, obj in data["objects"].items():
            h = obj["hash"]
            dest = os.path.join(target, *name.split("/"))
            if os.path.isfile(dest) and os.path.getsize(dest) == obj.get("size"):
                continue
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copyfile(self.path("assets", "objects", h[:2], h), dest)
        return target

    def _ensure_launcher_profiles(self):
        # Forge 等安装器要求游戏目录下存在这个文件
        path = self.path("launcher_profiles.json")
        if not os.path.isfile(path):
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"profiles": {}}, f)

    def version_settings(self, version_id):
        """版本单独的设置（内存、Java、JVM 参数、版本隔离），保存在版本文件夹中。"""
        try:
            with open(self.path("versions", version_id, VERSION_SETTINGS), encoding="utf-8") as f:
                data = json.load(f)
            return data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            return {}

    def save_version_settings(self, version_id, data):
        path = self.path("versions", version_id, VERSION_SETTINGS)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)

    def effective_cfg(self, version_id):
        cfg = dict(self.cfg)
        settings = self.version_settings(version_id)
        if settings.get("custom"):
            for key in ("max_memory", "java_path", "jvm_args"):
                if key in settings:
                    cfg[key] = settings[key]
        return cfg

    def game_dir_for(self, version_id):
        isolation = self.version_settings(version_id).get("isolation")
        if isolation is None:
            isolation = self.cfg.get("version_isolation")
        if isolation:
            return self.path("versions", version_id)
        return self.mc_dir

    def prepare(self, version_id):
        """确保某个版本的所有文件都已下载并校验，返回启动所需信息。"""
        os.makedirs(self.mc_dir, exist_ok=True)
        self.ensure_version_json(version_id)
        vjson = self.load_version(version_id)
        self.log("正在检查 {} 的游戏文件...".format(version_id))

        tasks = []
        jar_id = vjson["_jar"]
        client_jar = self.path("versions", jar_id, jar_id + ".jar")
        client = (vjson.get("downloads") or {}).get("client")
        if client:
            tasks.append(DownloadTask(client["url"], client_jar, client.get("sha1"), client.get("size")))
        elif not os.path.isfile(client_jar):
            raise RuntimeError("缺少游戏本体文件 {}".format(client_jar))

        classpath, natives, lib_tasks = self._collect_libraries(vjson)
        tasks += lib_tasks
        version_jar = self.path("versions", version_id, version_id + ".jar")

        logging_arg = None
        log_cfg = (vjson.get("logging") or {}).get("client")
        if log_cfg and log_cfg.get("file"):
            f = log_cfg["file"]
            log_path = self.path("assets", "log_configs", f["id"])
            tasks.append(DownloadTask(f["url"], log_path, f.get("sha1"), f.get("size")))
            logging_arg = log_cfg.get("argument", "").replace("${path}", log_path)

        asset_index, asset_tasks = self._collect_assets(vjson)
        tasks += asset_tasks

        if not all(t.is_valid() for t in tasks):
            source = self.dl.resolved_source()
            self.log("下载源：{}".format("BMCLAPI 镜像" if source == "bmclapi" else "Mojang 官方"))
        count = self.dl.download_many(tasks, lambda d, t: self.progress(d, t, "下载游戏文件"))
        self.log("已下载 {} 个文件".format(count) if count else "游戏文件完整，无需下载")

        # 和官方启动器一样把本体复制成 <版本名>.jar，新版 Forge 按这个文件名把本体排除出类路径
        if os.path.normcase(version_jar) != os.path.normcase(client_jar):
            if not os.path.isfile(version_jar) or os.path.getsize(version_jar) != os.path.getsize(client_jar):
                shutil.copyfile(client_jar, version_jar)
        classpath.append(version_jar)

        game_dir = self.game_dir_for(version_id)
        os.makedirs(game_dir, exist_ok=True)
        natives_dir = self.path("versions", version_id, "natives")
        self._extract_natives(natives, natives_dir)
        game_assets = self._map_legacy_assets(asset_index, game_dir)
        self._ensure_launcher_profiles()

        return {
            "vjson": vjson,
            "classpath": classpath,
            "natives_dir": natives_dir,
            "game_dir": game_dir,
            "game_assets": game_assets,
            "logging_arg": logging_arg,
        }

    # ------------------------------------------------------------------ 启动

    def select_java(self, vjson):
        custom = (self.cfg.get("java_path") or "").strip()
        if custom:
            if not os.path.isfile(custom):
                raise RuntimeError("设置中的 Java 路径不存在：{}".format(custom))
            return custom

        java_info = vjson.get("javaVersion") or {}
        required = java_info.get("majorVersion", 8)
        component = java_info.get("component", "jre-legacy")
        runtime_root = self.path("runtime")
        javas = javautil.find_java([runtime_root])

        exact = javautil.pick_exact(javas, required)
        if exact:
            return exact

        self.log("本机没有 Java {}，正在下载官方 Java 运行时 ({})...".format(required, component))
        try:
            return javautil.install_runtime(component, runtime_root, self.dl,
                                            lambda d, t: self.progress(d, t, "下载 Java"))
        except Exception as e:
            self.log("[警告] Java 运行时下载失败：{}".format(e))

        fallback = javautil.pick_compatible(javas, required)
        if fallback:
            self.log("[警告] 将使用更高版本的 Java 代替 Java {}，可能存在兼容问题".format(required))
            return fallback
        raise RuntimeError("找不到可用的 Java {}，请安装后在设置中指定".format(required))

    def build_command(self, vjson, java, info, server=None, auth=None):
        """server 为 "主机:端口" 时，游戏启动后直接进入该服务器。
        auth 为 {"name", "uuid", "token", "user_type", "jvm_args"}，缺省时使用离线账号。"""
        cfg = self.cfg
        if auth is None:
            uid = offline_uuid(cfg["username"])
            auth = {"name": cfg["username"], "uuid": uid, "token": uid, "user_type": "msa"}
        width, height = str(cfg.get("window_width", 854)), str(cfg.get("window_height", 480))
        values = {
            "auth_player_name": auth["name"],
            "version_name": vjson["id"],
            "game_directory": info["game_dir"],
            "assets_root": self.path("assets"),
            "game_assets": info["game_assets"],
            "assets_index_name": (vjson.get("assetIndex") or {}).get("id", vjson.get("assets", "legacy")),
            "auth_uuid": auth["uuid"],
            "auth_access_token": auth["token"],
            "auth_session": auth["token"],
            "auth_xuid": auth.get("xuid") or "0",
            "clientid": "0",
            "user_type": auth.get("user_type", "msa"),
            "user_properties": "{}",
            "version_type": LAUNCHER_NAME,
            "resolution_width": width,
            "resolution_height": height,
            "natives_directory": info["natives_dir"],
            "launcher_name": LAUNCHER_NAME,
            "launcher_version": LAUNCHER_VERSION,
            "classpath": os.pathsep.join(info["classpath"]),
            "classpath_separator": os.pathsep,
            "library_directory": self.path("libraries"),
        }
        features = {"has_custom_resolution": True}

        def resolve(entries):
            out = []
            for entry in entries:
                if isinstance(entry, str):
                    out.append(entry)
                elif rules_allow(entry.get("rules"), features):
                    value = entry.get("value")
                    out += value if isinstance(value, list) else [value]
            return out

        arguments = vjson.get("arguments") or {}
        if "minecraftArguments" in vjson:
            jvm_args = LEGACY_JVM_ARGS + resolve(arguments.get("jvm", []))
            game_args = vjson["minecraftArguments"].split() + resolve(arguments.get("game", []))
            game_args += ["--width", width, "--height", height]
        else:
            jvm_args = resolve(arguments.get("jvm", []))
            game_args = resolve(arguments.get("game", []))

        if server:
            host, _, port = server.rpartition(":")
            if "${quickPlayMultiplayer}" in json.dumps(arguments.get("game", [])):
                features["is_quick_play_multiplayer"] = True
                values["quickPlayMultiplayer"] = server
                game_args = resolve(arguments.get("game", []))
            else:
                game_args += ["--server", host or server, "--port", port or "25565"]

        cmd = [
            java,
            "-Xmx{}m".format(int(cfg.get("max_memory", 2048))),
            "-XX:+UseG1GC",
            "-XX:-OmitStackTraceInFastThrow",
            "-Dlog4j2.formatMsgNoLookups=true",
            "-Dfml.ignoreInvalidMinecraftCertificates=true",
            "-Dfml.ignorePatchDiscrepancies=true",
        ]
        cmd += auth.get("jvm_args") or []
        cmd += (cfg.get("jvm_args") or "").split()
        cmd += jvm_args
        if info.get("logging_arg"):
            cmd.append(info["logging_arg"])
        cmd.append(vjson["mainClass"])
        cmd += game_args
        return [_PLACEHOLDER.sub(lambda m: values.get(m.group(1), m.group(0)), arg) for arg in cmd]

    def launch(self, version_id, server=None, auth=None):
        self.cfg = self.effective_cfg(version_id)
        info = self.prepare(version_id)
        vjson = info["vjson"]
        java = self.select_java(vjson)
        self.log("使用 Java：{}".format(java))
        cmd = self.build_command(vjson, java, info, server, auth)
        self.log("正在启动 {}（玩家 {}，最大内存 {} MB）...".format(
            version_id, auth["name"] if auth else self.cfg["username"], self.cfg.get("max_memory")))
        kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
        return subprocess.Popen(cmd, cwd=info["game_dir"], stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **kwargs)
