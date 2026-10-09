"""模组、资源包、光影、数据包与整合包的搜索和安装（含前置），以及本地模组管理与更新，
支持 Modrinth 与 CurseForge。"""
import json
import os
import re
import sys
import zipfile
from array import array
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import quote, urlencode

from .download import DownloadTask, file_sha1

MODRINTH_API = "https://api.modrinth.com/v2"
# CurseForge 官方 API 必须使用开发者密钥，这里与 PCL / HMCL 一样经由 MCIM 镜像访问
CURSEFORGE_API = "https://mod.mcimirror.top/curseforge/v1"
CURSEFORGE_CDN = "https://edge.forgecdn.net/files/"
MOD_LOADERS = ("forge", "neoforge", "fabric", "quilt")
SORTS = [("relevance", "相关性"), ("downloads", "下载量"), ("follows", "热门"),
         ("updated", "最近更新"), ("newest", "最新发布")]
HIDDEN_CATEGORIES = {"fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift", "minecraft",
                     "datapack"}
# 资源类型：(键, 名称, 存放的文件夹)
KINDS = [("mod", "模组", "mods"), ("resourcepack", "资源包", "resourcepacks"), ("shader", "光影", "shaderpacks"),
         ("datapack", "数据包", "datapacks"), ("modpack", "整合包", None)]
KIND_NAMES = {k: n for k, n, _ in KINDS}
KIND_FOLDERS = {k: f for k, _, f in KINDS}


def modrinth_version_loaders(kind, loader):
    if kind == "mod":
        return modrinth_loaders(loader)
    if kind == "datapack":
        return ["datapack"]
    return []


def modrinth_loaders(loader):
    """启动器的加载器 -> 在 Modrinth 上可用的加载器标签。"""
    return {
        "fabric": ["fabric"],
        "quilt": ["quilt", "fabric"],
        "forge": ["forge"],
        "neoforge": ["neoforge"],
    }.get(loader, [])


class ModrinthClient:
    """搜索结果统一为 {id, slug, title, author, description, downloads, icon_url, categories, url}。"""
    name = "Modrinth"

    def __init__(self, downloader):
        self.dl = downloader

    def search(self, query, game_version, loader, offset=0, limit=20, index="relevance", kind="mod"):
        loaders = modrinth_loaders(loader) if kind == "mod" else []
        facets = [["project_type:" + kind]]
        if game_version:
            facets.append(["versions:" + game_version])
        if loaders:
            facets.append(["categories:" + l for l in loaders])
        params = {"query": query, "facets": json.dumps(facets), "offset": offset, "limit": limit, "index": index}
        data = self.dl.fetch_json(MODRINTH_API + "/search?" + urlencode(params))
        hits = [{
            "id": h["project_id"],
            "slug": h.get("slug", ""),
            "title": h.get("title", ""),
            "author": h.get("author", ""),
            "description": h.get("description", ""),
            "downloads": h.get("downloads", 0),
            "icon_url": h.get("icon_url") or "",
            "categories": [c for c in h.get("display_categories") or h.get("categories", [])
                           if c not in HIDDEN_CATEGORIES],
            "url": "https://modrinth.com/{}/{}".format(kind, h.get("slug") or h["project_id"]),
        } for h in data.get("hits", [])]
        return hits, data.get("total_hits", 0)

    def project_versions(self, project_id, game_version, loaders):
        params = {}
        if game_version:
            params["game_versions"] = json.dumps([game_version])
        if loaders:
            params["loaders"] = json.dumps(loaders)
        url = "{}/project/{}/version".format(MODRINTH_API, quote(project_id))
        return self.dl.fetch_json(url + ("?" + urlencode(params) if params else ""))

    def modpack_versions(self, project_id):
        """整合包的全部版本：[{name, detail, url, filename, sha1, size}]，新版本在前。"""
        out = []
        for v in self.project_versions(project_id, None, None):
            files = v.get("files") or []
            file = next((f for f in files if f.get("primary")), files[0] if files else None)
            if not file or not file["filename"].endswith(".mrpack"):
                continue
            out.append({"name": v.get("version_number") or v.get("name", ""),
                        "detail": "Minecraft {}  ·  {}".format(", ".join(v.get("game_versions", [])[:3]),
                                                               ", ".join(v.get("loaders", []))),
                        "url": file["url"], "filename": file["filename"],
                        "sha1": (file.get("hashes") or {}).get("sha1"), "size": file.get("size")})
        return out

    def best_version(self, project_id, game_version, loaders):
        versions = self.project_versions(project_id, game_version, loaders)
        if not versions:
            return None
        return next((v for v in versions if v.get("version_type") == "release"), versions[0])

    def identify(self, sha1_hashes):
        """根据文件 SHA1 查询对应的 Modrinth 版本，返回 {sha1: version}。"""
        if not sha1_hashes:
            return {}
        return self.dl.post_json(MODRINTH_API + "/version_files",
                                 {"hashes": list(sha1_hashes), "algorithm": "sha1"})

    def installed_ids(self, mods):
        """通过文件 SHA1 识别已安装模组对应的 Modrinth 项目。"""
        hashes = {}
        for mod in mods:
            try:
                hashes[file_sha1(mod["path"])] = mod
            except OSError:
                pass
        try:
            found = self.identify(list(hashes))
        except Exception:
            return set()
        return {v["project_id"] for v in found.values() if isinstance(v, dict) and v.get("project_id")}

    def install(self, project_id, game_version, loader, mods_dir, installed_projects, log=print, kind="mod"):
        """安装资源（模组会连同全部必需前置），返回新安装的文件名列表。installed_projects 会被更新。"""
        loaders = modrinth_version_loaders(kind, loader)
        os.makedirs(mods_dir, exist_ok=True)
        queue = [project_id]
        installed = []
        while queue:
            pid = queue.pop(0)
            if pid in installed_projects:
                continue
            version = self.best_version(pid, game_version, loaders)
            if version is None:
                if pid == project_id:
                    raise RuntimeError("这个模组没有适用于当前版本的文件")
                log("[警告] 前置模组 {} 没有适用于当前版本的文件，已跳过".format(pid))
                continue
            files = version.get("files") or []
            if not files:
                continue
            file = next((f for f in files if f.get("primary")), files[0])
            dest = os.path.join(mods_dir, file["filename"])
            self.dl.download_many([DownloadTask(file["url"], dest, (file.get("hashes") or {}).get("sha1"),
                                                file.get("size"))])
            installed_projects.add(version["project_id"])
            installed.append(file["filename"])
            if pid != project_id:
                log("已安装前置模组 {}".format(file["filename"]))
            for dep in version.get("dependencies") or [] if kind == "mod" else []:
                if dep.get("dependency_type") == "required" and dep.get("project_id"):
                    queue.append(dep["project_id"])
        return installed

    def updates(self, mods, game_version, loader):
        """返回 (可更新列表, Modrinth 不认识的模组列表)。"""
        hashes = {}
        for mod in mods:
            try:
                hashes[file_sha1(mod["path"])] = mod
            except OSError:
                pass
        if not hashes:
            return [], []
        data = self.dl.post_json(MODRINTH_API + "/version_files/update", {
            "hashes": list(hashes), "algorithm": "sha1", "loaders": modrinth_loaders(loader),
            "game_versions": [game_version]})
        known = self.identify(list(hashes))
        updates = []
        for sha1, version in data.items():
            files = version.get("files") or []
            file = next((f for f in files if f.get("primary")), files[0] if files else None)
            if file and (file.get("hashes") or {}).get("sha1") != sha1:
                updates.append({"mod": hashes[sha1], "source": "Modrinth", "filename": file["filename"],
                                "url": file["url"], "sha1": file["hashes"].get("sha1"), "size": file.get("size"),
                                "version": version.get("version_number", "")})
        unknown = [mod for sha1, mod in hashes.items() if sha1 not in known and sha1 not in data]
        return updates, unknown


# ---------------------------------------------------------------------- CurseForge

CF_GAME_ID = 432
CF_MOD_CLASS = 6
CF_CLASSES = {"mod": 6, "resourcepack": 12, "shader": 6552, "datapack": 6945, "modpack": 4471}
CF_LOADER_TYPES = {"forge": [1], "neoforge": [6], "fabric": [4], "quilt": [5, 4]}
CF_LOADER_TAGS = {"forge", "neoforge", "fabric", "quilt"}
CF_SORT_FIELDS = {"relevance": 2, "downloads": 6, "follows": 2, "updated": 3, "newest": 11}
CF_REQUIRED_DEPENDENCY = 3

_fingerprints = {}


def curseforge_fingerprint(path):
    """CurseForge 的文件指纹：去掉空白字节后的 MurmurHash2（seed = 1）。"""
    stat = os.stat(path)
    key = (path, stat.st_size, stat.st_mtime)
    if key in _fingerprints:
        return _fingerprints[key]
    with open(path, "rb") as f:
        data = f.read().translate(None, b"\t\n\r ")
    m, mask = 0x5BD1E995, 0xFFFFFFFF
    length = len(data)
    h = (1 ^ length) & mask
    n4 = length - length % 4
    words = array("I" if array("I").itemsize == 4 else "L")
    words.frombytes(data[:n4])
    if sys.byteorder != "little":
        words.byteswap()
    for k in words:
        k = (k * m) & mask
        k ^= k >> 24
        h = ((h * m) & mask) ^ ((k * m) & mask)
    tail = data[n4:]
    if len(tail) == 3:
        h ^= tail[2] << 16
    if len(tail) >= 2:
        h ^= tail[1] << 8
    if tail:
        h = ((h ^ tail[0]) * m) & mask
    h ^= h >> 13
    h = (h * m) & mask
    h ^= h >> 15
    _fingerprints[key] = h
    return h


class CurseForgeClient:
    name = "CurseForge"

    def __init__(self, downloader):
        self.dl = downloader

    def _get(self, path, params=None):
        url = CURSEFORGE_API + path + ("?" + urlencode(params) if params else "")
        return self.dl.fetch_json(url, mirror=False)

    def search(self, query, game_version, loader, offset=0, limit=20, index="relevance", kind="mod"):
        params = {"gameId": CF_GAME_ID, "classId": CF_CLASSES[kind], "index": offset, "pageSize": limit,
                  "sortField": CF_SORT_FIELDS.get(index, 2), "sortOrder": "desc"}
        if query:
            params["searchFilter"] = query
        if game_version:
            params["gameVersion"] = game_version
        types = CF_LOADER_TYPES.get(loader) if kind == "mod" else None
        if types and len(types) == 1:
            params["modLoaderType"] = types[0]
        elif types:
            params["modLoaderTypes"] = json.dumps(types)
        data = self._get("/mods/search", params)
        hits = []
        for mod in data.get("data", []):
            hits.append({
                "id": str(mod["id"]),
                "slug": mod.get("slug", ""),
                "title": mod.get("name", ""),
                "author": ", ".join(a["name"] for a in mod.get("authors", [])[:2]),
                "description": mod.get("summary", ""),
                "downloads": int(mod.get("downloadCount") or 0),
                "icon_url": (mod.get("logo") or {}).get("thumbnailUrl") or "",
                "categories": [c["name"] for c in mod.get("categories", [])],
                "url": (mod.get("links") or {}).get("websiteUrl") or "",
            })
        # CurseForge 的分页上限为 10000 条
        total = min((data.get("pagination") or {}).get("totalCount", 0), 10000 - limit)
        return hits, total

    def _files(self, mod_id, game_version, loader, kind="mod"):
        types = (CF_LOADER_TYPES.get(loader) if kind == "mod" else None) or [None]
        for loader_type in types:
            params = {"pageSize": 50}
            if game_version:
                params["gameVersion"] = game_version
            if loader_type:
                params["modLoaderType"] = loader_type
            files = self._get("/mods/{}/files".format(mod_id), params).get("data", [])
            if files:
                return files
        if loader == "forge" and kind == "mod":
            # 很多老版本 Forge 模组的文件没有标注加载器
            files = self._get("/mods/{}/files".format(mod_id), {"gameVersion": game_version, "pageSize": 50})
            return [f for f in files.get("data", [])
                    if not CF_LOADER_TAGS & {v.lower() for v in f.get("gameVersions", [])}]
        return []

    def best_file(self, mod_id, game_version, loader, kind="mod"):
        files = [f for f in self._files(mod_id, game_version, loader, kind) if f.get("isAvailable", True)]
        if not files:
            return None
        files.sort(key=lambda f: f.get("fileDate", ""), reverse=True)
        return next((f for f in files if f.get("releaseType") == 1), files[0])

    def modpack_versions(self, project_id):
        files = self._get("/mods/{}/files".format(project_id), {"pageSize": 50}).get("data", [])
        files.sort(key=lambda f: f.get("fileDate", ""), reverse=True)
        out = []
        for f in files:
            if not f.get("isAvailable", True) or f.get("isServerPack"):
                continue
            mc = [v for v in f.get("gameVersions", []) if re.match(r"\d", v)]
            loaders = [v for v in f.get("gameVersions", []) if v.lower() in CF_LOADER_TAGS]
            out.append({"name": f.get("displayName") or f["fileName"],
                        "detail": "Minecraft {}  ·  {}".format(", ".join(mc[:3]), ", ".join(loaders)),
                        "url": self.download_url(f), "filename": f["fileName"],
                        "sha1": next((h["value"] for h in f.get("hashes", []) if h.get("algo") == 1), None),
                        "size": f.get("fileLength")})
        return out

    def updates(self, mods, game_version, loader):
        prints = {}
        for mod in mods:
            try:
                prints[curseforge_fingerprint(mod["path"])] = mod
            except OSError:
                pass
        if not prints:
            return []
        data = self.dl.post_json(CURSEFORGE_API + "/fingerprints", {"fingerprints": list(prints)})
        matches = (data.get("data") or {}).get("exactMatches", [])

        def check(match):
            current = match["file"]
            best = self.best_file(match["id"], game_version, loader)
            if best and best["id"] != current["id"] and best.get("fileDate", "") > current.get("fileDate", ""):
                return {"mod": prints[current["fileFingerprint"]], "source": "CurseForge",
                        "filename": best["fileName"], "url": self.download_url(best),
                        "sha1": next((h["value"] for h in best.get("hashes", []) if h.get("algo") == 1), None),
                        "size": best.get("fileLength"), "version": best.get("displayName") or best["fileName"]}
            return None

        matches = [m for m in matches if m.get("file", {}).get("fileFingerprint") in prints]
        with ThreadPoolExecutor(max_workers=8) as pool:
            return [u for u in pool.map(lambda m: _quiet(check, m), matches) if u]

    @staticmethod
    def download_url(file):
        if file.get("downloadUrl"):
            return file["downloadUrl"]
        file_id = int(file["id"])
        return "{}{}/{}/{}".format(CURSEFORGE_CDN, file_id // 1000, file_id % 1000, quote(file["fileName"]))

    def installed_ids(self, mods):
        prints = []
        for mod in mods:
            try:
                prints.append(curseforge_fingerprint(mod["path"]))
            except OSError:
                pass
        if not prints:
            return set()
        try:
            data = self.dl.post_json(CURSEFORGE_API + "/fingerprints", {"fingerprints": prints})
        except Exception:
            return set()
        return {str(m["id"]) for m in (data.get("data") or {}).get("exactMatches", [])}

    def install(self, project_id, game_version, loader, mods_dir, installed_projects, log=print, kind="mod"):
        os.makedirs(mods_dir, exist_ok=True)
        queue = [str(project_id)]
        installed, seen = [], set()
        while queue:
            pid = queue.pop(0)
            if pid in installed_projects or pid in seen:
                continue
            seen.add(pid)
            file = self.best_file(pid, game_version, loader, kind)
            if file is None:
                if pid == str(project_id):
                    raise RuntimeError("这个模组没有适用于当前版本的文件")
                log("[警告] 前置模组 {} 没有适用于当前版本的文件，已跳过".format(pid))
                continue
            sha1 = next((h["value"] for h in file.get("hashes", []) if h.get("algo") == 1), None)
            dest = os.path.join(mods_dir, file["fileName"])
            self.dl.download_many([DownloadTask(self.download_url(file), dest, sha1, file.get("fileLength"))])
            installed_projects.add(pid)
            installed.append(file["fileName"])
            if pid != str(project_id):
                log("已安装前置模组 {}".format(file["fileName"]))
            for dep in file.get("dependencies") or [] if kind == "mod" else []:
                if dep.get("relationType") == CF_REQUIRED_DEPENDENCY:
                    queue.append(str(dep["modId"]))
        return installed


def _quiet(fn, *args):
    try:
        return fn(*args)
    except Exception:
        return None


def check_updates(clients, mods, game_version, loader):
    """先用 Modrinth 检查，Modrinth 不认识的模组再交给 CurseForge。"""
    updates, unknown = clients["modrinth"].updates(mods, game_version, loader)
    if unknown:
        try:
            updates += clients["curseforge"].updates(unknown, game_version, loader)
        except Exception:
            pass
    return updates


def apply_update(dl, update):
    """下载新版本并替换旧文件（保留禁用状态），返回新文件路径。"""
    mod = update["mod"]
    folder = os.path.dirname(mod["path"])
    dest = os.path.join(folder, update["filename"])
    dl.download_many([DownloadTask(update["url"], dest, update.get("sha1"), update.get("size"))])
    if not mod["enabled"]:
        os.replace(dest, dest + ".disabled")
        dest += ".disabled"
    if os.path.normcase(os.path.abspath(dest)) != os.path.normcase(os.path.abspath(mod["path"])):
        os.remove(mod["path"])
    return dest


# ---------------------------------------------------------------------- 本地模组

def _toml_value(text, key):
    m = re.search(r'^\s*{}\s*=\s*["\']([^"\']*)["\']'.format(key), text, re.M)
    return m.group(1) if m else None


def read_mod_info(path):
    """从模组 jar 中读取名称与版本，支持 Fabric / Quilt / Forge / NeoForge / 旧版 mcmod.info。"""
    name = version = None
    try:
        with zipfile.ZipFile(path) as zf:
            names = set(zf.namelist())

            def read(member):
                return zf.read(member).decode("utf-8", "replace")

            if "fabric.mod.json" in names:
                data = json.loads(read("fabric.mod.json"), strict=False)
                name, version = data.get("name") or data.get("id"), data.get("version")
            elif "quilt.mod.json" in names:
                data = json.loads(read("quilt.mod.json"), strict=False).get("quilt_loader", {})
                name = (data.get("metadata") or {}).get("name") or data.get("id")
                version = data.get("version")
            else:
                toml = next((m for m in ("META-INF/neoforge.mods.toml", "META-INF/mods.toml") if m in names), None)
                if toml:
                    text = read(toml)
                    name, version = _toml_value(text, "displayName"), _toml_value(text, "version")
                    if version and "${" in version and "META-INF/MANIFEST.MF" in names:
                        m = re.search(r"^Implementation-Version:\s*(\S+)", read("META-INF/MANIFEST.MF"), re.M)
                        version = m.group(1) if m else None
                elif "mcmod.info" in names:
                    data = json.loads(read("mcmod.info"), strict=False)
                    entries = data.get("modList", []) if isinstance(data, dict) else data
                    if entries:
                        name, version = entries[0].get("name"), entries[0].get("version")
    except (OSError, ValueError, zipfile.BadZipFile, KeyError, AttributeError):
        pass
    if version and "${" in version:
        version = None
    return name, version


def list_local_mods(mods_dir):
    if not os.path.isdir(mods_dir):
        return []
    mods = []
    for filename in sorted(os.listdir(mods_dir), key=str.lower):
        lower = filename.lower()
        if not (lower.endswith(".jar") or lower.endswith(".jar.disabled") or lower.endswith(".zip")):
            continue
        path = os.path.join(mods_dir, filename)
        name, version = read_mod_info(path)
        mods.append({
            "filename": filename,
            "path": path,
            "enabled": not lower.endswith(".disabled"),
            "name": name or re.sub(r"\.(jar|zip)(\.disabled)?$", "", filename, flags=re.I),
            "version": version,
        })
    return mods


def list_local_files(folder):
    """资源包、光影包、数据包文件夹中的 zip 与已解压的文件夹。"""
    if not os.path.isdir(folder):
        return []
    items = []
    for filename in sorted(os.listdir(folder), key=str.lower):
        path = os.path.join(folder, filename)
        is_dir = os.path.isdir(path)
        if not (is_dir or filename.lower().endswith(".zip")):
            continue
        items.append({"filename": filename, "path": path, "enabled": True, "is_dir": is_dir,
                      "name": re.sub(r"\.zip$", "", filename, flags=re.I), "version": "文件夹" if is_dir else None})
    return items


def set_mod_enabled(mod, enabled):
    path = mod["path"]
    target = path[:-len(".disabled")] if path.endswith(".disabled") else path
    if not enabled:
        target += ".disabled"
    if target != path:
        os.replace(path, target)
    return target
