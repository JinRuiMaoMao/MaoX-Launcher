"""版本管理：重命名、复制、删除，以及存档列表与备份。"""
import gzip
import json
import os
import re
import shutil
import struct
import time
import zipfile

INVALID_NAME = re.compile(r'[\\/:*?"<>|]')


def check_name(gl, name):
    name = name.strip()
    if not name:
        raise ValueError("名称不能为空")
    if INVALID_NAME.search(name) or name.endswith(".") or name in (".", ".."):
        raise ValueError('名称不能包含 \\ / : * ? " < > | 等字符')
    if os.path.exists(gl.path("versions", name)):
        raise ValueError("已经存在名为「{}」的版本".format(name))
    return name


def _read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)


def dependents(gl, version_id):
    """继承自该版本的其他版本。"""
    out = []
    for vid in gl.installed_versions():
        try:
            data = _read_json(gl.version_json_path(vid))
        except (OSError, ValueError):
            continue
        if data.get("inheritsFrom") == version_id:
            out.append(vid)
    return out


def _retarget(folder, old, new):
    """把版本文件夹中以旧名称命名的文件改成新名称，并更新 JSON 中的 id。"""
    for ext in (".json", ".jar"):
        src = os.path.join(folder, old + ext)
        if os.path.isfile(src):
            os.replace(src, os.path.join(folder, new + ext))
    json_path = os.path.join(folder, new + ".json")
    data = _read_json(json_path)
    data["id"] = new
    if data.get("jar") == old:
        data["jar"] = new
    _write_json(json_path, data)


def rename(gl, old, new):
    new = check_name(gl, new)
    children = dependents(gl, old)
    os.rename(gl.path("versions", old), gl.path("versions", new))
    _retarget(gl.path("versions", new), old, new)
    for child in children:
        path = gl.version_json_path(child)
        data = _read_json(path)
        data["inheritsFrom"] = new
        if data.get("jar") == old:
            data["jar"] = new
        _write_json(path, data)
    return new


def duplicate(gl, src, new, progress=None):
    new = check_name(gl, new)
    src_dir, dst_dir = gl.path("versions", src), gl.path("versions", new)
    files = [os.path.join(root, f) for root, _, names in os.walk(src_dir) for f in names]
    total = len(files)
    try:
        for i, path in enumerate(files, 1):
            rel = os.path.relpath(path, src_dir)
            if rel.split(os.sep)[0] == "natives":
                continue
            dest = os.path.join(dst_dir, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy2(path, dest)
            if progress and (i % 20 == 0 or i == total):
                progress(i, total, "复制版本")
        _retarget(dst_dir, src, new)
    except BaseException:
        shutil.rmtree(dst_dir, ignore_errors=True)
        raise
    return new


def delete(gl, version_id):
    shutil.rmtree(gl.path("versions", version_id))


# ---------------------------------------------------------------------- 存档

def _read_nbt(data):
    """极简 NBT 解析（只用于读取 level.dat），返回根复合标签的内容。"""
    pos = 0

    def take(fmt):
        nonlocal pos
        size = struct.calcsize(fmt)
        value = struct.unpack_from(fmt, data, pos)
        pos += size
        return value[0]

    def string():
        nonlocal pos
        length = take(">H")
        value = data[pos:pos + length].decode("utf-8", "replace")
        pos += length
        return value

    def payload(tag):
        nonlocal pos
        if tag == 1:
            return take(">b")
        if tag == 2:
            return take(">h")
        if tag == 3:
            return take(">i")
        if tag == 4:
            return take(">q")
        if tag == 5:
            return take(">f")
        if tag == 6:
            return take(">d")
        if tag in (7, 11, 12):
            length = take(">i")
            pos += length * {7: 1, 11: 4, 12: 8}[tag]
            return None
        if tag == 8:
            return string()
        if tag == 9:
            item, length = take(">b"), take(">i")
            return [payload(item) for _ in range(length)]
        if tag == 10:
            out = {}
            while True:
                child = take(">b")
                if child == 0:
                    return out
                name = string()
                out[name] = payload(child)
        raise ValueError("未知的 NBT 类型 {}".format(tag))

    if take(">b") != 10:
        raise ValueError("不是 NBT 复合标签")
    string()
    return payload(10)


GAME_MODES = {0: "生存", 1: "创造", 2: "冒险", 3: "旁观"}


def list_worlds(game_dir):
    saves = os.path.join(game_dir, "saves")
    if not os.path.isdir(saves):
        return []
    worlds = []
    for folder in os.listdir(saves):
        path = os.path.join(saves, folder)
        level = os.path.join(path, "level.dat")
        if not os.path.isfile(level):
            continue
        info = {"folder": folder, "path": path, "name": folder, "mode": "", "version": "",
                "last_played": os.path.getmtime(level), "hardcore": False,
                "icon": os.path.join(path, "icon.png") if os.path.isfile(os.path.join(path, "icon.png")) else None}
        try:
            with gzip.open(level) as f:
                data = _read_nbt(f.read()).get("Data", {})
            info["name"] = data.get("LevelName") or folder
            info["mode"] = GAME_MODES.get(data.get("GameType"), "")
            info["hardcore"] = bool(data.get("hardcore"))
            info["version"] = (data.get("Version") or {}).get("Name", "")
            if data.get("LastPlayed"):
                info["last_played"] = data["LastPlayed"] / 1000
        except (OSError, ValueError, struct.error, EOFError, AttributeError):
            pass
        worlds.append(info)
    worlds.sort(key=lambda w: w["last_played"], reverse=True)
    return worlds


def list_datapacks(world_path):
    folder = os.path.join(world_path, "datapacks")
    if not os.path.isdir(folder):
        return []
    return sorted((f for f in os.listdir(folder) if f.lower().endswith(".zip")
                   or os.path.isfile(os.path.join(folder, f, "pack.mcmeta"))), key=str.lower)


def backup_world(game_dir, world, progress=None):
    """把存档打包到 <游戏目录>/backups，返回备份文件路径。"""
    backups = os.path.join(game_dir, "backups")
    os.makedirs(backups, exist_ok=True)
    stamp = time.strftime("%Y-%m-%d_%H-%M-%S")
    dest = os.path.join(backups, "{}_{}.zip".format(world["folder"], stamp))
    files = [os.path.join(root, f) for root, _, names in os.walk(world["path"]) for f in names]
    tmp = dest + ".part"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zf:
        for i, path in enumerate(files, 1):
            if os.path.basename(path) == "session.lock":
                continue
            zf.write(path, os.path.join(world["folder"], os.path.relpath(path, world["path"])))
            if progress and (i % 20 == 0 or i == len(files)):
                progress(i, len(files), "备份存档")
    os.replace(tmp, dest)
    return dest
