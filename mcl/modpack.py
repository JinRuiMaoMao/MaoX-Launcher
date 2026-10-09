"""整合包：安装 Modrinth（.mrpack）与 CurseForge 整合包，导出为 .mrpack。"""
import hashlib
import json
import os
import shutil
import zipfile

from .download import DownloadTask, file_sha1
from .instance import check_name
from .loaders import LoaderInstaller
from .mods import CURSEFORGE_API, CurseForgeClient, ModrinthClient

CF_CLASS_FOLDERS = {6: "mods", 12: "resourcepacks", 6552: "shaderpacks"}
MR_LOADER_KEYS = {"fabric-loader": "fabric", "quilt-loader": "quilt", "forge": "forge", "neoforge": "neoforge"}
EXPORT_GROUPS = {
    "config": ["config", "defaultconfigs", "kubejs", "scripts", "global_packs"],
    "options": ["options.txt", "optionsof.txt", "optionsshaders.txt"],
    "resourcepacks": ["resourcepacks"],
    "shaderpacks": ["shaderpacks"],
    "saves": ["saves"],
}


class ModpackError(Exception):
    pass


def _safe_join(root, rel):
    rel = rel.replace("\\", "/")
    parts = [p for p in rel.split("/") if p not in ("", ".")]
    if not parts or ".." in parts or ":" in parts[0]:
        raise ModpackError("整合包中包含不安全的路径：{}".format(rel))
    return os.path.join(root, *parts)


def read_manifest(path):
    """读取整合包信息：{format, name, version, summary, mc, loader, loader_version, file_count}。"""
    try:
        with zipfile.ZipFile(path) as zf:
            names = set(zf.namelist())
            if "modrinth.index.json" in names:
                index = json.loads(zf.read("modrinth.index.json").decode("utf-8"))
                deps = index.get("dependencies") or {}
                loader = next(((MR_LOADER_KEYS[k], v) for k, v in deps.items() if k in MR_LOADER_KEYS), (None, None))
                return {"format": "modrinth", "name": index.get("name") or "整合包", "version": index.get("versionId", ""),
                        "summary": index.get("summary", ""), "mc": deps.get("minecraft"), "loader": loader[0],
                        "loader_version": loader[1], "file_count": len(index.get("files", [])), "raw": index}
            if "manifest.json" in names:
                manifest = json.loads(zf.read("manifest.json").decode("utf-8-sig"))
                if manifest.get("manifestType") != "minecraftModpack":
                    raise ModpackError("不支持的 CurseForge 整合包类型")
                minecraft = manifest.get("minecraft") or {}
                loaders = minecraft.get("modLoaders") or []
                primary = next((l for l in loaders if l.get("primary")), loaders[0] if loaders else None)
                loader = loader_version = None
                if primary:
                    loader, _, loader_version = primary["id"].partition("-")
                return {"format": "curseforge", "name": manifest.get("name") or "整合包",
                        "version": manifest.get("version", ""), "summary": manifest.get("author", ""),
                        "mc": minecraft.get("version"), "loader": loader, "loader_version": loader_version,
                        "file_count": len(manifest.get("files", [])), "raw": manifest}
    except (zipfile.BadZipFile, ValueError, KeyError) as e:
        raise ModpackError("整合包文件已损坏：{}".format(e))
    raise ModpackError("不是支持的整合包（支持 Modrinth .mrpack 与 CurseForge 整合包 .zip）")


def _loader_item(installer, loader, mc, version):
    if loader in ("fabric", "quilt"):
        return {"version": version, "display": version}
    for item in installer.list_versions(loader, mc):
        if version in (item["version"], item["display"]) or item["version"].endswith("-" + version):
            return item
    if loader == "forge":
        return {"version": "{}-{}".format(mc, version), "display": version}
    return {"version": version, "display": version, "artifact": "forge" if mc == "1.20.1" else "neoforge"}


def _create_version(gl, info, name, log):
    """安装原版与加载器，并生成以整合包命名的版本。"""
    mc, loader = info["mc"], info["loader"]
    if not mc:
        raise ModpackError("整合包没有指定 Minecraft 版本")
    if loader and loader not in ("forge", "neoforge", "fabric", "quilt"):
        raise ModpackError("不支持的模组加载器：{}".format(loader))
    gl.ensure_version_json(mc)
    base, created = mc, False
    if loader:
        installer = LoaderInstaller(gl)
        before = set(gl.installed_versions())
        base = installer.install(loader, mc, _loader_item(installer, loader, mc, info["loader_version"]))
        created = base not in before
    with open(gl.version_json_path(base), encoding="utf-8") as f:
        data = json.load(f)
    data["id"] = name
    os.makedirs(gl.path("versions", name), exist_ok=True)
    with open(gl.version_json_path(name), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    if created:
        shutil.rmtree(gl.path("versions", base), ignore_errors=True)
    gl.save_version_settings(name, {"isolation": True, "modpack": {
        "name": info["name"], "version": info["version"], "format": info["format"]}})


def _modrinth_tasks(index, game_dir):
    tasks = []
    for f in index.get("files", []):
        if (f.get("env") or {}).get("client") == "unsupported" or not f.get("downloads"):
            continue
        urls = f["downloads"]
        tasks.append(DownloadTask(urls[0], _safe_join(game_dir, f["path"]), (f.get("hashes") or {}).get("sha1"),
                                  f.get("fileSize"), urls[1:]))
    return tasks


def _curseforge_tasks(gl, manifest, game_dir, log):
    ids = [f["fileID"] for f in manifest.get("files", []) if f.get("required", True)]
    files = []
    for i in range(0, len(ids), 500):
        files += gl.dl.post_json(CURSEFORGE_API + "/mods/files", {"fileIds": ids[i:i + 500]}).get("data", [])
    mod_ids = sorted({f["modId"] for f in files})
    classes = {}
    for i in range(0, len(mod_ids), 500):
        for mod in gl.dl.post_json(CURSEFORGE_API + "/mods", {"modIds": mod_ids[i:i + 500]}).get("data", []):
            classes[mod["id"]] = mod.get("classId")
    missing = len(set(ids) - {f["id"] for f in files})
    if missing:
        log("[警告] 有 {} 个文件在 CurseForge 上已不存在，已跳过".format(missing))
    tasks = []
    for f in files:
        folder = CF_CLASS_FOLDERS.get(classes.get(f["modId"]), "mods")
        sha1 = next((h["value"] for h in f.get("hashes", []) if h.get("algo") == 1), None)
        tasks.append(DownloadTask(CurseForgeClient.download_url(f), os.path.join(game_dir, folder, f["fileName"]),
                                  sha1, f.get("fileLength")))
    return tasks


def _extract_overrides(path, prefixes, game_dir):
    count = 0
    with zipfile.ZipFile(path) as zf:
        for member in zf.infolist():
            name = member.filename
            prefix = next((p for p in prefixes if name.startswith(p + "/")), None)
            if prefix is None or name.endswith("/"):
                continue
            dest = _safe_join(game_dir, name[len(prefix) + 1:])
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with zf.open(member) as src, open(dest, "wb") as out:
                shutil.copyfileobj(src, out)
            count += 1
    return count


def install(gl, path, name, log=print, progress=None):
    """安装整合包，返回新版本的 id。"""
    info = read_manifest(path)
    name = check_name(gl, name)
    log("正在安装整合包 {}（Minecraft {}{}）".format(
        info["name"], info["mc"], "，{} {}".format(info["loader"], info["loader_version"]) if info["loader"] else ""))
    try:
        _create_version(gl, info, name, log)
        game_dir = gl.game_dir_for(name)
        if info["format"] == "modrinth":
            tasks = _modrinth_tasks(info["raw"], game_dir)
            prefixes = ["overrides", "client-overrides"]
        else:
            tasks = _curseforge_tasks(gl, info["raw"], game_dir, log)
            prefixes = [info["raw"].get("overrides") or "overrides"]
        log("正在下载整合包中的 {} 个文件...".format(len(tasks)))
        gl.dl.download_many(tasks, lambda d, t: progress and progress(d, t, "下载整合包文件"))
        log("已解压 {} 个配置文件".format(_extract_overrides(path, prefixes, game_dir)))
        gl.prepare(name)
    except BaseException:
        shutil.rmtree(gl.path("versions", name), ignore_errors=True)
        raise
    return name


# ---------------------------------------------------------------------- 导出

def _sha512(path):
    h = hashlib.sha512()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def export_mrpack(gl, version_id, dest, name, pack_version, summary, include, log=print, progress=None):
    """导出为 Modrinth 整合包。能在 Modrinth 上找到的文件只记录下载地址，其余文件放进 overrides。"""
    loader, loader_version, mc = gl.detect_loader(version_id)
    if loader == "optifine":
        raise ModpackError("OptiFine 独立版本无法导出为整合包，请改用 Forge + OptiFine")
    deps = {"minecraft": mc}
    if loader:
        key = next(k for k, v in MR_LOADER_KEYS.items() if v == loader)
        deps[key] = loader_version
    game_dir = gl.game_dir_for(version_id)

    candidates = []
    for folder, exts in (("mods", (".jar",)), ("resourcepacks", (".zip",)), ("shaderpacks", (".zip",))):
        if folder != "mods" and folder not in include:
            continue
        root = os.path.join(game_dir, folder)
        if os.path.isdir(root):
            for f in sorted(os.listdir(root)):
                if f.lower().endswith(exts) and os.path.isfile(os.path.join(root, f)):
                    candidates.append((folder + "/" + f, os.path.join(root, f)))
    log("正在识别 {} 个文件...".format(len(candidates)))
    hashes = {rel: file_sha1(path) for rel, path in candidates}
    try:
        found = ModrinthClient(gl.dl).identify(list(hashes.values())) if hashes else {}
    except Exception as e:
        log("[警告] 无法连接 Modrinth，所有文件将直接打包：{}".format(e))
        found = {}

    files, overrides = [], []
    for rel, path in candidates:
        sha1 = hashes[rel]
        version = found.get(sha1)
        file = next((f for f in (version or {}).get("files", []) if (f.get("hashes") or {}).get("sha1") == sha1), None)
        if file and file.get("url"):
            files.append({"path": rel, "hashes": {"sha1": sha1, "sha512": _sha512(path)},
                          "env": {"client": "required", "server": "required"},
                          "downloads": [file["url"]], "fileSize": os.path.getsize(path)})
        else:
            overrides.append((rel, path))
    for group in include:
        for entry in EXPORT_GROUPS.get(group, []):
            if group in ("resourcepacks", "shaderpacks"):
                continue
            path = os.path.join(game_dir, entry)
            if os.path.isfile(path):
                overrides.append((entry, path))
            elif os.path.isdir(path):
                for root, _, names in os.walk(path):
                    for f in names:
                        full = os.path.join(root, f)
                        if f != "session.lock":
                            overrides.append((os.path.relpath(full, game_dir).replace(os.sep, "/"), full))
    # 资源包、光影文件夹中的非 zip 内容（解压的资源包）也要带上
    for folder in ("resourcepacks", "shaderpacks"):
        root = os.path.join(game_dir, folder)
        if folder in include and os.path.isdir(root):
            for entry in os.listdir(root):
                full = os.path.join(root, entry)
                if os.path.isdir(full):
                    for r, _, names in os.walk(full):
                        for f in names:
                            p = os.path.join(r, f)
                            overrides.append((os.path.relpath(p, game_dir).replace(os.sep, "/"), p))

    index = {"formatVersion": 1, "game": "minecraft", "versionId": pack_version or "1.0.0", "name": name,
             "summary": summary, "files": files, "dependencies": deps}
    tmp = dest + ".part"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("modrinth.index.json", json.dumps(index, indent=2, ensure_ascii=False))
        for i, (rel, path) in enumerate(overrides, 1):
            zf.write(path, "overrides/" + rel)
            if progress and (i % 20 == 0 or i == len(overrides)):
                progress(i, len(overrides), "打包文件")
    os.replace(tmp, dest)
    log("整合包已导出：{}（{} 个在线文件，{} 个打包文件）".format(dest, len(files), len(overrides)))
    return len(files), len(overrides)
