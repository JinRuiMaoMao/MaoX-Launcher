import glob
import os
import platform
import re
import shutil
import stat
import subprocess
import sys

from .download import DownloadTask

IS_WINDOWS = os.name == "nt"
JAVA_EXE = "javaw.exe" if IS_WINDOWS else "java"
RUNTIME_MANIFEST_URL = (
    "https://launchermeta.mojang.com/v1/products/java-runtime/"
    "2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json"
)


def _no_window():
    return {"creationflags": subprocess.CREATE_NO_WINDOW} if IS_WINDOWS else {}


def parse_major(version):
    parts = version.split(".")
    if parts[0] == "1" and len(parts) > 1:
        parts = parts[1:]
    m = re.match(r"\d+", parts[0])
    return int(m.group()) if m else None


def java_version(java_path):
    """返回 Java 主版本号（如 8、17、21），无法识别时返回 None。"""
    home = os.path.dirname(os.path.dirname(java_path))
    release = os.path.join(home, "release")
    if os.path.isfile(release):
        try:
            with open(release, encoding="utf-8", errors="replace") as f:
                m = re.search(r'JAVA_VERSION="([^"]+)"', f.read())
            if m:
                return parse_major(m.group(1))
        except OSError:
            pass

    exe = java_path
    if IS_WINDOWS and os.path.basename(java_path).lower() == "javaw.exe":
        console = os.path.join(os.path.dirname(java_path), "java.exe")
        if os.path.isfile(console):
            exe = console
    try:
        out = subprocess.run([exe, "-version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             timeout=15, **_no_window())
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r'version "([^"]+)"', (out.stderr + out.stdout).decode("utf-8", "replace"))
    return parse_major(m.group(1)) if m else None


def _is_32bit_java(home):
    release = os.path.join(home, "release")
    if os.path.isfile(release):
        try:
            with open(release, encoding="utf-8", errors="replace") as f:
                m = re.search(r'OS_ARCH="([^"]+)"', f.read())
            if m:
                return m.group(1).lower() in ("x86", "i386", "i586", "i686")
        except OSError:
            pass
    return "(x86)" in home


def _candidate_homes(runtime_dirs):
    homes = []
    if os.environ.get("JAVA_HOME"):
        homes.append(os.environ["JAVA_HOME"])
    which = shutil.which("java")
    if which:
        homes.append(os.path.dirname(os.path.dirname(os.path.realpath(which))))

    patterns = []
    if IS_WINDOWS:
        roots = {os.environ.get(k) for k in ("ProgramFiles", "ProgramFiles(x86)", "ProgramW6432")}
        vendors = ("Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Microsoft",
                   "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "Oracle")
        for root in filter(None, roots):
            patterns += [os.path.join(root, v, "*") for v in vendors]
            patterns.append(os.path.join(root, "Minecraft Launcher", "runtime", "*", "*", "*"))
        local = os.environ.get("LOCALAPPDATA")
        if local:
            patterns.append(os.path.join(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe",
                                         "LocalCache", "Local", "runtime", "*", "*", "*"))
    elif sys.platform == "darwin":
        patterns.append("/Library/Java/JavaVirtualMachines/*/Contents/Home")
    else:
        patterns.append("/usr/lib/jvm/*")
    for d in runtime_dirs:
        patterns.append(os.path.join(d, "*"))
        patterns.append(os.path.join(d, "*", "jre.bundle", "Contents", "Home"))

    for p in patterns:
        homes += glob.glob(p)
    return homes


def find_java(runtime_dirs=()):
    """扫描本机已安装的 Java，返回 [(可执行文件路径, 主版本号)]，按版本号降序。"""
    skip_32bit = platform.machine().lower().endswith("64")
    seen = set()
    result = []
    for home in _candidate_homes(runtime_dirs):
        exe = os.path.join(home, "bin", JAVA_EXE)
        if not os.path.isfile(exe) or (skip_32bit and _is_32bit_java(home)):
            continue
        key = os.path.normcase(os.path.realpath(exe))
        if key in seen:
            continue
        seen.add(key)
        version = java_version(exe)
        if version:
            result.append((exe, version))
    result.sort(key=lambda j: -j[1])
    return result


def pick_exact(javas, required):
    for path, version in javas:
        if version == required:
            return path
    return None


def pick_compatible(javas, required):
    newer = sorted((j for j in javas if j[1] >= required), key=lambda j: j[1])
    return newer[0][0] if newer else None


def runtime_platform():
    machine = platform.machine().lower()
    arm = machine in ("arm64", "aarch64")
    if IS_WINDOWS:
        if arm:
            return "windows-arm64"
        return "windows-x64" if machine.endswith("64") else "windows-x86"
    if sys.platform == "darwin":
        return "mac-os-arm64" if arm else "mac-os"
    return "linux" if machine.endswith("64") else "linux-i386"


def runtime_java_path(home):
    if sys.platform == "darwin":
        return os.path.join(home, "jre.bundle", "Contents", "Home", "bin", "java")
    return os.path.join(home, "bin", JAVA_EXE)


def install_runtime(component, runtime_root, downloader, progress=None):
    """从 Mojang 下载官方 Java 运行时（如 java-runtime-delta），返回 java 可执行文件路径。"""
    all_runtimes = downloader.fetch_json(RUNTIME_MANIFEST_URL)
    entries = all_runtimes.get(runtime_platform(), {}).get(component) or []
    if not entries:
        raise RuntimeError("官方没有为当前平台提供 Java 运行时 {}".format(component))
    manifest = downloader.fetch_json(entries[0]["manifest"]["url"])

    home = os.path.join(runtime_root, component)
    tasks, executables, links = [], [], []
    for rel, info in manifest["files"].items():
        dest = os.path.join(home, *rel.split("/"))
        kind = info.get("type")
        if kind == "directory":
            os.makedirs(dest, exist_ok=True)
        elif kind == "file":
            raw = info["downloads"]["raw"]
            tasks.append(DownloadTask(raw["url"], dest, raw.get("sha1"), raw.get("size")))
            if info.get("executable"):
                executables.append(dest)
        elif kind == "link":
            links.append((dest, info["target"]))

    downloader.download_many(tasks, progress)

    if not IS_WINDOWS:
        for path in executables:
            os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        for dest, target in links:
            if not os.path.lexists(dest):
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                os.symlink(target, dest)

    java = runtime_java_path(home)
    if not os.path.isfile(java):
        raise RuntimeError("Java 运行时安装不完整：找不到 {}".format(java))
    return java
