import gzip
import hashlib
import http.client
import json
import os
import ssl
import threading
import time
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, as_completed, wait
from urllib.parse import urljoin, urlsplit

USER_AGENT = "MaoXLauncher/1.0"
BMCLAPI = "https://bmclapi2.bangbang93.com"
MCIM = "https://mod.mcimirror.top"

MIRROR_RULES = {
    "bmclapi": [
        ("https://piston-meta.mojang.com", BMCLAPI),
        ("https://launchermeta.mojang.com", BMCLAPI),
        ("https://launcher.mojang.com", BMCLAPI),
        ("https://piston-data.mojang.com", BMCLAPI),
        ("https://resources.download.minecraft.net", BMCLAPI + "/assets"),
        ("https://libraries.minecraft.net", BMCLAPI + "/maven"),
        ("https://maven.fabricmc.net", BMCLAPI + "/maven"),
        ("https://meta.fabricmc.net", BMCLAPI + "/fabric-meta"),
        ("https://maven.minecraftforge.net", BMCLAPI + "/maven"),
        ("https://maven.neoforged.net/releases", BMCLAPI + "/maven"),
        ("https://api.modrinth.com", MCIM + "/modrinth"),
        ("https://cdn.modrinth.com", MCIM),
        ("https://edge.forgecdn.net", MCIM),
        ("https://mediafilez.forgecdn.net", MCIM),
    ],
}

PROBE_URL = "https://libraries.minecraft.net/com/mojang/logging/1.1.1/logging-1.1.1.jar"
_CONNECTION_ERRORS = (http.client.HTTPException, ConnectionError, OSError)
_ssl_context = ssl.create_default_context()
_local = threading.local()
_auto_source = {}
_auto_lock = threading.Lock()


class DownloadError(Exception):
    status = None


class HTTPStatusError(DownloadError):
    def __init__(self, status, url):
        super().__init__("HTTP {}：{}".format(status, url))
        self.status = status


def file_sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


class DownloadTask:
    __slots__ = ("url", "path", "sha1", "size", "alternates")

    def __init__(self, url, path, sha1=None, size=None, alternates=()):
        self.url = url
        self.path = path
        self.sha1 = sha1.lower() if sha1 else None
        self.size = size
        self.alternates = tuple(alternates)

    def is_valid(self):
        if not os.path.isfile(self.path):
            return False
        if self.size is not None:
            return os.path.getsize(self.path) == self.size
        if self.sha1:
            return file_sha1(self.path) == self.sha1
        return True


def _pool():
    pool = getattr(_local, "pool", None)
    if pool is None:
        pool = _local.pool = {}
    return pool


def _drop_connection(key):
    conn = _pool().pop(key, None)
    if conn is not None:
        conn.close()


def http_request(url, timeout=30, method="GET", body=None, content_type=None, max_redirects=5, compressed=False):
    """在当前线程复用 keep-alive 连接发起请求，返回 (响应, 连接标识)。调用方必须读完响应体。
    compressed=True 时允许 gzip 压缩，响应体需通过 _read_all 读取以自动解压。"""
    for _ in range(max_redirects + 1):
        parts = urlsplit(url)
        key = (parts.scheme, parts.netloc)
        path = (parts.path or "/") + ("?" + parts.query if parts.query else "")
        headers = {"User-Agent": USER_AGENT, "Accept-Encoding": "gzip" if compressed else "identity"}
        if content_type:
            headers["Content-Type"] = content_type
        for attempt in range(2):
            pool = _pool()
            conn = pool.get(key)
            if conn is None:
                if parts.scheme == "https":
                    conn = http.client.HTTPSConnection(parts.netloc, timeout=timeout, context=_ssl_context)
                else:
                    conn = http.client.HTTPConnection(parts.netloc, timeout=timeout)
                pool[key] = conn
            try:
                conn.request(method, path, body=body, headers=headers)
                resp = conn.getresponse()
                break
            except _CONNECTION_ERRORS:
                _drop_connection(key)
                if attempt:
                    raise
        if resp.status in (301, 302, 303, 307, 308):
            location = resp.getheader("Location")
            resp.read()
            if resp.will_close:
                _drop_connection(key)
            url = urljoin(url, location)
            if resp.status == 303:
                method, body, content_type = "GET", None, None
            continue
        if resp.status != 200:
            resp.read()
            _drop_connection(key)
            raise HTTPStatusError(resp.status, url)
        return resp, key
    raise DownloadError("重定向次数过多：{}".format(url))


def _finish(resp, key):
    if resp.will_close:
        _drop_connection(key)


def _read_all(resp, key):
    try:
        data = resp.read()
    except _CONNECTION_ERRORS:
        _drop_connection(key)
        raise
    _finish(resp, key)
    if (resp.getheader("Content-Encoding") or "").lower() == "gzip":
        data = gzip.decompress(data)
    return data


def mirror_url(url, source):
    for prefix, replacement in MIRROR_RULES.get(source, []):
        if url.startswith(prefix):
            return replacement + url[len(prefix):]
    return url


def detect_fastest_source(timeout=8):
    """同时请求官方源和镜像源的同一个小文件，返回先成功的那个。结果在进程内缓存。"""
    with _auto_lock:
        if "result" in _auto_source:
            return _auto_source["result"]

        def probe(source):
            resp, key = http_request(mirror_url(PROBE_URL, source), timeout=timeout)
            _read_all(resp, key)
            return source

        result = "official"
        pool = ThreadPoolExecutor(max_workers=2)
        pending = {pool.submit(probe, s) for s in ("official", "bmclapi")}
        deadline = time.time() + timeout
        while pending and time.time() < deadline:
            done, pending = wait(pending, timeout=deadline - time.time(), return_when=FIRST_COMPLETED)
            winner = next((f.result() for f in done if f.exception() is None), None)
            if winner:
                result = winner
                break
        pool.shutdown(wait=False)
        _auto_source["result"] = result
        return result


class Downloader:
    def __init__(self, source="auto", threads=16, retries=3, timeout=30):
        self.source = source
        self.threads = max(1, int(threads))
        self.retries = retries
        self.timeout = timeout

    def resolved_source(self):
        return detect_fastest_source() if self.source == "auto" else self.source

    def candidate_urls(self, url, alternates=()):
        """优先使用选定的下载源，失败后依次回退到官方地址、镜像地址和备用地址。"""
        urls = []
        for candidate in (mirror_url(url, self.resolved_source()), url, mirror_url(url, "bmclapi"), *alternates):
            if candidate not in urls:
                urls.append(candidate)
        return urls

    def _with_fallback(self, url, action, alternates=(), mirror=True):
        last_error = None
        for candidate in self.candidate_urls(url, alternates) if mirror else [url]:
            for attempt in range(self.retries):
                try:
                    return action(candidate)
                except HTTPStatusError as e:
                    last_error = e
                    if e.status in (400, 403, 404, 410):
                        break
                except Exception as e:
                    last_error = e
                time.sleep(0.5 * (attempt + 1))
        error = DownloadError("{} ({})".format(url, last_error))
        error.status = getattr(last_error, "status", None)
        raise error

    def fetch(self, url, mirror=True):
        """mirror=False 用于镜像可能过期的元数据（如 Forge 版本列表）。"""
        return self._with_fallback(url, lambda u: _read_all(*http_request(u, self.timeout, compressed=True)),
                                   mirror=mirror)

    def fetch_json(self, url, mirror=True):
        return self._with_fallback(url, lambda u: json.loads(
            _read_all(*http_request(u, self.timeout, compressed=True)).decode("utf-8")), mirror=mirror)

    def post_json(self, url, payload):
        body = json.dumps(payload).encode("utf-8")
        return self._with_fallback(url, lambda u: json.loads(_read_all(*http_request(
            u, self.timeout, "POST", body, "application/json", compressed=True)).decode("utf-8")))

    def download(self, task):
        os.makedirs(os.path.dirname(task.path), exist_ok=True)
        tmp = task.path + ".part"

        def action(u):
            h = hashlib.sha1()
            resp, key = http_request(u, self.timeout)
            try:
                with open(tmp, "wb") as out:
                    for chunk in iter(lambda: resp.read(1 << 16), b""):
                        h.update(chunk)
                        out.write(chunk)
                _finish(resp, key)
                if task.sha1 and h.hexdigest() != task.sha1:
                    raise DownloadError("SHA1 校验失败")
                os.replace(tmp, task.path)
            except _CONNECTION_ERRORS:
                _drop_connection(key)
                raise
            finally:
                if os.path.exists(tmp):
                    os.remove(tmp)

        self._with_fallback(task.url, action, task.alternates)

    def download_many(self, tasks, progress=None):
        """并发下载所有缺失或损坏的文件，返回实际下载的文件数。"""
        unique = {}
        for t in tasks:
            unique.setdefault(os.path.normcase(os.path.abspath(t.path)), t)
        pending = [t for t in unique.values() if not t.is_valid()]
        total = len(pending)
        if not total:
            return 0

        failures = []
        done = 0
        with ThreadPoolExecutor(max_workers=self.threads) as pool:
            futures = {pool.submit(self.download, t): t for t in pending}
            for future in as_completed(futures):
                done += 1
                try:
                    future.result()
                except Exception as e:
                    failures.append((futures[future], e))
                if progress:
                    progress(done, total)
        if failures:
            task, err = failures[0]
            raise DownloadError("{} 个文件下载失败，例如 {}：{}".format(len(failures), os.path.basename(task.path), err))
        return total
