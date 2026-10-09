"""模组加载器安装：Forge、NeoForge、Fabric、Quilt，以及 OptiFine。"""
import json
import os
import re
import shutil
import subprocess
import tempfile
import zipfile
from urllib.parse import quote

from .core import LOADER_NAMES, maven_path
from .download import DownloadError, DownloadTask, file_sha1

FABRIC_META = "https://meta.fabricmc.net/v2"
QUILT_META = "https://meta.quiltmc.org/v3"
FORGE_MAVEN = "https://maven.minecraftforge.net/net/minecraftforge/forge/"
FORGE_PROMOS = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"
NEOFORGE_API = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/"
NEOFORGE_MAVEN = "https://maven.neoforged.net/releases/net/neoforged/"
# OptiFine 官网没有直链，与 PCL / HMCL 一样使用 BMCLAPI 提供的 OptiFine 镜像
OPTIFINE_API = "https://bmclapi2.bangbang93.com/optifine/"
OPTIFINE_TWEAKER = "optifine.OptiFineTweaker"

LOADERS = ("forge", "neoforge", "fabric", "quilt")


def is_unstable(version):
    return bool(re.search(r"alpha|beta|pre|rc|snapshot", version, re.I))


def version_key(version):
    base = re.split(r"[-+]", version, 1)[0]
    return ([int(x) for x in re.findall(r"\d+", base)], not is_unstable(version),
            [int(x) for x in re.findall(r"\d+", version)])


def neoforge_prefix(mc):
    """1.21.1 -> "21.1."，1.21 -> "21.0."，26.3 -> "26.3.0."。"""
    parts = mc.split(".")
    if parts[0] == "1":
        parts = parts[1:]
        while len(parts) < 2:
            parts.append("0")
        return ".".join(parts[:2]) + "."
    while len(parts) < 3:
        parts.append("0")
    return ".".join(parts[:3]) + "."


def optifine_key(name):
    """HD_U_I6 / HD_U_I6_pre3 -> 可比较的排序键（正式版排在同号测试版之后）。"""
    m = re.search(r"_([A-Z])(\d+)(?:_pre(\d+))?$", name)
    if not m:
        return ("", 0, False, 0)
    return (m.group(1), int(m.group(2)), m.group(3) is None, int(m.group(3) or 0))


def _main_class(jar):
    with zipfile.ZipFile(jar) as zf:
        text = zf.read("META-INF/MANIFEST.MF").decode("utf-8", "replace")
    text = text.replace("\r\n ", "").replace("\n ", "")
    m = re.search(r"^Main-Class:\s*(\S+)", text, re.M)
    if not m:
        raise RuntimeError("{} 中没有 Main-Class".format(os.path.basename(jar)))
    return m.group(1)


def _extract(zf, member, dest):
    info = zf.getinfo(member)
    if os.path.isfile(dest) and os.path.getsize(dest) == info.file_size:
        return
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with zf.open(info) as src, open(dest, "wb") as out:
        shutil.copyfileobj(src, out)


class LoaderInstaller:
    def __init__(self, launcher):
        self.gl = launcher
        self.dl = launcher.dl
        self.log = launcher.log

    # ------------------------------------------------------------------ 版本列表
    # 每项为 {"version": 安装用的完整版本号, "display": 显示用版本号, "stable": bool, "recommended": bool}

    def list_versions(self, loader, mc):
        try:
            items = getattr(self, "_list_" + loader)(mc)
        except DownloadError as e:
            if e.status == 404:
                return []
            raise
        items.sort(key=lambda i: optifine_key(i["display"]) if loader == "optifine" else version_key(i["display"]),
                   reverse=True)
        if not any(i.get("recommended") for i in items):
            first_stable = next((i for i in items if i["stable"]), None)
            if first_stable:
                first_stable["recommended"] = True
        items.sort(key=lambda i: not i.get("recommended"))
        return items

    def _list_fabric(self, mc):
        data = self.dl.fetch_json("{}/versions/loader/{}".format(FABRIC_META, quote(mc)))
        return [{"version": d["loader"]["version"], "display": d["loader"]["version"],
                 "stable": bool(d["loader"].get("stable"))} for d in data]

    def _list_quilt(self, mc):
        data = self.dl.fetch_json("{}/versions/loader/{}".format(QUILT_META, quote(mc)))
        return [{"version": d["loader"]["version"], "display": d["loader"]["version"],
                 "stable": not is_unstable(d["loader"]["version"])} for d in data]

    def _list_forge(self, mc):
        xml = self.dl.fetch(FORGE_MAVEN + "maven-metadata.xml", mirror=False).decode("utf-8")
        try:
            promos = self.dl.fetch_json(FORGE_PROMOS, mirror=False).get("promos", {})
        except Exception:
            promos = {}
        recommended = promos.get(mc + "-recommended")
        items = []
        for full in re.findall(r"<version>([^<]+)</version>", xml):
            if not full.startswith(mc + "-"):
                continue
            display = full[len(mc) + 1:]
            if display.endswith("-" + mc):
                display = display[:-len(mc) - 1]
            items.append({"version": full, "display": display, "stable": True,
                          "recommended": display == recommended})
        return items

    def _list_neoforge(self, mc):
        if mc == "1.20.1":
            artifact, prefix = "forge", "1.20.1-"
        else:
            artifact, prefix = "neoforge", neoforge_prefix(mc)
        versions = self.dl.fetch_json(NEOFORGE_API + artifact, mirror=False).get("versions", [])
        return [{"version": v, "display": v, "stable": not is_unstable(v), "artifact": artifact}
                for v in versions if v.startswith(prefix)]

    def _list_optifine(self, mc):
        data = self.dl.fetch_json(OPTIFINE_API + quote(mc), mirror=False)
        items = []
        for d in data:
            name = "{}_{}".format(d["type"], d["patch"])
            items.append({"version": name, "display": name, "stable": not d["patch"].startswith("pre"),
                          "type": d["type"], "patch": d["patch"], "filename": d["filename"],
                          "forge": d.get("forge") or ""})
        return items

    # ------------------------------------------------------------------ 安装

    def install(self, loader, mc, item):
        """安装加载器，返回新版本的 id。原版需要已经安装好（或会被自动补全）。"""
        name = LOADER_NAMES[loader]
        self.log("正在安装 {} {}（Minecraft {}）".format(name, item["display"], mc))
        if loader in ("fabric", "quilt"):
            meta = FABRIC_META if loader == "fabric" else QUILT_META
            profile = self.dl.fetch_json("{}/versions/loader/{}/{}/profile/json".format(
                meta, quote(mc), quote(item["version"])))
            return self._write_version(profile)

        if loader == "forge":
            full = item["version"]
            url = "{0}{1}/forge-{1}-installer.jar".format(FORGE_MAVEN, full)
        else:
            artifact, v = item.get("artifact", "neoforge"), item["version"]
            url = "{0}{1}/{2}/{1}-{2}-installer.jar".format(NEOFORGE_MAVEN, artifact, v)
        return self._install_from_installer(mc, url, name)

    def install_optifine(self, mc, item, forge_version=None):
        """安装 OptiFine。指定 forge_version 时作为模组放进该版本的 mods 文件夹，
        否则生成一个独立的 OptiFine 版本（OptiFine 补丁库 + launchwrapper）。返回版本 id。"""
        url = "{}{}/{}/{}".format(OPTIFINE_API, quote(mc), quote(item["type"]), quote(item["patch"]))
        installer = self.gl.path("cache", "installers", item["filename"])
        self.log("正在下载 OptiFine {}...".format(item["display"]))
        self.dl.download_many([DownloadTask(url, installer)])
        if not zipfile.is_zipfile(installer):
            os.remove(installer)
            raise RuntimeError("OptiFine 文件已损坏，请重试")

        if forge_version:
            mods = os.path.join(self.gl.game_dir_for(forge_version), "mods")
            os.makedirs(mods, exist_ok=True)
            shutil.copyfile(installer, os.path.join(mods, item["filename"]))
            self.log("已将 OptiFine 放入 {} 的 mods 文件夹".format(forge_version))
            return forge_version

        self.gl.prepare(mc)
        coord = "optifine:OptiFine:{}_{}".format(mc, item["version"])
        library = self._lib_path(coord)
        with zipfile.ZipFile(installer) as zf:
            names = set(zf.namelist())
            if "launchwrapper-of.txt" in names:
                lw_version = zf.read("launchwrapper-of.txt").decode("utf-8").strip()
                lw_coord = "optifine:launchwrapper-of:" + lw_version
                _extract(zf, "launchwrapper-of-{}.jar".format(lw_version), self._lib_path(lw_coord))
            elif "launchwrapper-2.0.jar" in names:
                lw_coord = "optifine:launchwrapper:2.0"
                _extract(zf, "launchwrapper-2.0.jar", self._lib_path(lw_coord))
            else:
                lw_coord = None

        os.makedirs(os.path.dirname(library), exist_ok=True)
        if "optifine/Patcher.class" in names:
            self.log("正在生成 OptiFine 补丁...")
            java = self.gl.select_java(self.gl.load_version(mc))
            kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
            result = subprocess.run([java, "-cp", installer, "optifine.Patcher",
                                     self.gl.path("versions", mc, mc + ".jar"), installer, library],
                                    stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    **kwargs)
            if result.returncode != 0 or not os.path.isfile(library):
                tail = result.stdout.decode("utf-8", "replace").strip().splitlines()[-12:]
                raise RuntimeError("OptiFine 补丁生成失败（退出码 {}）：\n{}".format(result.returncode, "\n".join(tail)))
        else:
            shutil.copyfile(installer, library)

        def local_lib(name):
            return {"name": name, "downloads": {"artifact": {"path": maven_path(name)}}}

        with open(self.gl.version_json_path(mc), encoding="utf-8") as f:
            parent = json.load(f)
        version_id = "{}-OptiFine_{}".format(mc, item["version"])
        data = {
            "id": version_id,
            "inheritsFrom": mc,
            "type": parent.get("type", "release"),
            "time": parent.get("time", ""),
            "releaseTime": parent.get("releaseTime", ""),
            "mainClass": "net.minecraft.launchwrapper.Launch",
            "libraries": [local_lib(coord),
                          local_lib(lw_coord) if lw_coord else {"name": "net.minecraft:launchwrapper:1.12"}],
        }
        if "arguments" in parent:
            data["arguments"] = {"game": ["--tweakClass", OPTIFINE_TWEAKER], "jvm": []}
        else:
            data["minecraftArguments"] = parent.get("minecraftArguments", "") + " --tweakClass " + OPTIFINE_TWEAKER
        return self._write_version(data)

    def _write_version(self, data):
        version_id = data["id"]
        path = self.gl.version_json_path(version_id)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        return version_id

    def _lib_path(self, coord):
        return self.gl.path("libraries", *maven_path(coord).split("/"))

    def _install_from_installer(self, mc, url, name):
        self.gl.prepare(mc)
        installer = self.gl.path("cache", "installers", url.rsplit("/", 1)[-1])
        self.log("正在下载 {} 安装器...".format(name))
        self.dl.download_many([DownloadTask(url, installer)])
        try:
            with zipfile.ZipFile(installer) as zf:
                profile = json.loads(zf.read("install_profile.json").decode("utf-8"))
                if "versionInfo" in profile:
                    return self._install_legacy(zf, profile, mc)
                return self._install_modern(zf, profile, mc, installer, name)
        except zipfile.BadZipFile:
            os.remove(installer)
            raise RuntimeError("{} 安装器文件已损坏，请重试".format(name))

    def _install_legacy(self, zf, profile, mc):
        """1.12.2 及更早的旧版安装器：解出 universal 包并写入版本信息。"""
        install, info = profile["install"], profile["versionInfo"]
        _extract(zf, install["filePath"], self._lib_path(install["path"]))
        info["id"] = install.get("target") or info["id"]
        if not info.get("inheritsFrom"):
            info["inheritsFrom"] = install.get("minecraft") or mc
        info.setdefault("jar", info["inheritsFrom"])
        return self._write_version(info)

    def _install_modern(self, zf, profile, mc, installer, name):
        """新版安装器（Forge 1.13+ 与全部 NeoForge）：下载依赖并依次运行安装处理器。"""
        members = set(zf.namelist())
        version = json.loads(zf.read(profile["json"].lstrip("/")).decode("utf-8"))
        lib_dir = self.gl.path("libraries")

        tasks = []
        for lib in profile.get("libraries", []) + version.get("libraries", []):
            artifact = (lib.get("downloads") or {}).get("artifact") or {}
            rel = artifact.get("path") or maven_path(lib["name"])
            dest = os.path.join(lib_dir, *rel.split("/"))
            if "maven/" + rel in members:
                _extract(zf, "maven/" + rel, dest)
            elif artifact.get("url"):
                tasks.append(DownloadTask(artifact["url"], dest, artifact.get("sha1"), artifact.get("size")))
        self.log("正在下载 {} 依赖库...".format(name))
        self.dl.download_many(tasks, lambda d, t: self.gl.progress(d, t, "下载 {} 依赖库".format(name)))

        processors = [p for p in profile.get("processors", []) if not p.get("sides") or "client" in p["sides"]]
        if processors:
            self._run_processors(zf, profile, processors, mc, installer, name)
        return self._write_version(version)

    def _run_processors(self, zf, profile, processors, mc, installer, name):
        work = tempfile.mkdtemp(prefix="maox-installer-")
        try:
            data = {}
            for key, value in (profile.get("data") or {}).items():
                value = value.get("client", "") if isinstance(value, dict) else value
                if value.startswith("[") and value.endswith("]"):
                    data[key] = self._lib_path(value[1:-1])
                elif value.startswith("'") and value.endswith("'"):
                    data[key] = value[1:-1]
                elif value.startswith("/"):
                    dest = os.path.join(work, *value.lstrip("/").split("/"))
                    _extract(zf, value.lstrip("/"), dest)
                    data[key] = dest
                else:
                    data[key] = value
            data.update(SIDE="client", MINECRAFT_VERSION=mc, ROOT=self.gl.mc_dir, INSTALLER=installer,
                        MINECRAFT_JAR=self.gl.path("versions", mc, mc + ".jar"),
                        LIBRARY_DIR=self.gl.path("libraries"))

            def fill(arg):
                if arg.startswith("[") and arg.endswith("]"):
                    return self._lib_path(arg[1:-1])
                return re.sub(r"\{(\w+)\}", lambda m: data.get(m.group(1), m.group(0)), arg)

            java = self.gl.select_java(self.gl.load_version(mc))
            kwargs = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}
            total = len(processors)
            for index, proc in enumerate(processors, 1):
                outputs = {fill(k): fill(v).strip("'") for k, v in (proc.get("outputs") or {}).items()}
                if outputs and all(os.path.isfile(p) and file_sha1(p) == sha for p, sha in outputs.items()):
                    continue
                jar = self._lib_path(proc["jar"])
                classpath = os.pathsep.join([jar] + [self._lib_path(c) for c in proc.get("classpath", [])])
                args = [fill(a) for a in proc.get("args", [])]
                task = args[args.index("--task") + 1] if "--task" in args[:-1] else proc["jar"].split(":")[1]
                self.log("运行安装处理器 {}/{}：{}".format(index, total, task))
                self.gl.progress(index - 1, total, "安装 " + name)
                result = subprocess.run([java, "-cp", classpath, _main_class(jar)] + args, cwd=work,
                                        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, **kwargs)
                if result.returncode != 0:
                    tail = result.stdout.decode("utf-8", "replace").strip().splitlines()[-12:]
                    raise RuntimeError("安装处理器 {} 失败（退出码 {}）：\n{}".format(
                        task, result.returncode, "\n".join(tail)))
                for path, sha in outputs.items():
                    if not os.path.isfile(path) or file_sha1(path) != sha:
                        raise RuntimeError("安装处理器 {} 的输出校验失败：{}".format(task, os.path.basename(path)))
            self.gl.progress(total, total, "安装 " + name)
        finally:
            shutil.rmtree(work, ignore_errors=True)
