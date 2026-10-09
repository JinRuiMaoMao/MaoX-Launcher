"""账号：离线、外置登录（authlib-injector / Yggdrasil）、微软正版登录（设备码流程）。

账号以 dict 形式保存在配置文件中：
    离线   {"type": "offline", "name", "uuid"}
    外置   {"type": "authlib", "api", "server_name", "username", "name", "uuid", "access_token", "client_token"}
    微软   {"type": "msa", "name", "uuid", "refresh_token", "access_token", "expires_at", "xuid"}
"""
import base64
import hashlib
import json
import os
import ssl
import time
import urllib.error
import urllib.request
import uuid as uuidlib
from urllib.parse import urlencode, urljoin

from .core import offline_uuid
from .download import USER_AGENT, DownloadTask

AUTHLIB_INJECTOR_META = [
    "https://authlib-injector.yushi.moe/artifact/latest.json",
    "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json",
]
LITTLESKIN_API = "https://littleskin.cn/api/yggdrasil"

MS_DEVICE_CODE = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
MS_TOKEN = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
MS_SCOPE = "XboxLive.signin offline_access"
XBL_AUTH = "https://user.auth.xboxlive.com/user/authenticate"
XSTS_AUTH = "https://xsts.auth.xboxlive.com/xsts/authorize"
MC_LOGIN = "https://api.minecraftservices.com/authentication/login_with_xbox"
MC_PROFILE = "https://api.minecraftservices.com/minecraft/profile"
MSA_APP_GUIDE = "https://learn.microsoft.com/zh-cn/entra/identity-platform/quickstart-register-app"

XSTS_ERRORS = {
    2148916233: "这个微软账号还没有创建 Xbox 档案，请先登录 xbox.com 完成创建",
    2148916235: "Xbox Live 在你所在的国家或地区不可用",
    2148916236: "该账号需要完成成人验证（韩国）",
    2148916237: "该账号需要完成成人验证（韩国）",
    2148916238: "未成年账号需要由家长添加到微软家庭组后才能登录",
}
TYPE_NAMES = {"offline": "离线账号", "authlib": "外置登录", "msa": "微软账号"}
_ssl_context = ssl.create_default_context()


class AccountError(Exception):
    pass


def _request(url, data=None, headers=None, method=None, timeout=20):
    """返回 (状态码, 响应体, 响应头)，4xx / 5xx 不抛异常。"""
    all_headers = {"User-Agent": USER_AGENT, "Accept": "application/json"}
    all_headers.update(headers or {})
    req = urllib.request.Request(url, data=data, headers=all_headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=_ssl_context) as resp:
            return resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers
    except (urllib.error.URLError, OSError) as e:
        raise AccountError("网络连接失败：{}".format(getattr(e, "reason", e)))


def _json_request(url, payload=None, form=None, headers=None, method=None):
    headers = dict(headers or {})
    data = None
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    elif form is not None:
        data = urlencode(form).encode("utf-8")
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    status, body, _ = _request(url, data, headers, method)
    try:
        return status, json.loads(body.decode("utf-8")) if body else {}
    except ValueError:
        return status, {}


def offline_account(name):
    return {"type": "offline", "name": name, "uuid": offline_uuid(name)}


def describe(account):
    if account["type"] == "authlib":
        return account.get("server_name") or "外置登录"
    return TYPE_NAMES.get(account["type"], "")


# ---------------------------------------------------------------------- 外置登录

def resolve_yggdrasil(url):
    """解析外置登录服务器地址（支持 API 地址指示 ALI），返回 (API 根地址, 元数据)。"""
    url = url.strip()
    if not url:
        raise AccountError("请填写认证服务器地址")
    if "://" not in url:
        url = "https://" + url
    status, body, headers = _request(url)
    location = headers.get("X-Authlib-Injector-API-Location") if headers else None
    if location:
        api = urljoin(url, location)
        if api.rstrip("/") != url.rstrip("/"):
            url = api
            status, body, _ = _request(url)
    try:
        meta = json.loads(body.decode("utf-8"))
    except ValueError:
        meta = None
    if status != 200 or not isinstance(meta, dict) or "signaturePublickey" not in meta:
        raise AccountError("这不是有效的外置登录（Yggdrasil）服务器地址")
    return url.rstrip("/"), meta


def _yggdrasil_error(status, data):
    message = data.get("errorMessage") or data.get("error") or "HTTP {}".format(status)
    if data.get("error") == "ForbiddenOperationException":
        if "credentials" in message.lower() or "password" in message.lower():
            return AccountError("邮箱或密码错误")
        if "token" in message.lower():
            return AccountError("登录已失效，请重新登录")
    return AccountError(message)


def yggdrasil_login(api, username, password):
    """返回 (令牌数据, 可用角色列表)。若只有一个角色会自动选中。"""
    client_token = uuidlib.uuid4().hex
    status, data = _json_request(api + "/authserver/authenticate", {
        "agent": {"name": "Minecraft", "version": 1}, "username": username, "password": password,
        "clientToken": client_token, "requestUser": True})
    if status != 200:
        raise _yggdrasil_error(status, data)
    profiles = data.get("availableProfiles") or []
    if not profiles:
        raise AccountError("这个账号下还没有角色，请先在皮肤站创建一个角色")
    return data, profiles


def yggdrasil_select(api, token_data, profile):
    """多角色时选择其中一个，返回新的令牌数据。"""
    status, data = _json_request(api + "/authserver/refresh", {
        "accessToken": token_data["accessToken"], "clientToken": token_data["clientToken"],
        "selectedProfile": {"id": profile["id"], "name": profile["name"]}, "requestUser": True})
    if status != 200:
        raise _yggdrasil_error(status, data)
    return data


def authlib_account(api, meta, username, token_data, profile):
    return {
        "type": "authlib", "api": api, "server_name": (meta.get("meta") or {}).get("serverName") or "外置登录",
        "username": username, "name": profile["name"], "uuid": profile["id"],
        "access_token": token_data["accessToken"], "client_token": token_data["clientToken"],
    }


def _authlib_refresh(account):
    status, _ = _json_request(account["api"] + "/authserver/validate", {
        "accessToken": account["access_token"], "clientToken": account["client_token"]})
    if status in (200, 204):
        return False
    status, data = _json_request(account["api"] + "/authserver/refresh", {
        "accessToken": account["access_token"], "clientToken": account["client_token"], "requestUser": True})
    if status != 200:
        raise AccountError("{} 的登录已过期，请删除账号后重新登录".format(account["name"]))
    account["access_token"] = data["accessToken"]
    selected = data.get("selectedProfile") or {}
    if selected.get("name"):
        account["name"] = selected["name"]
    return True


def ensure_authlib_injector(dl, tools_dir, log=print):
    """下载 authlib-injector（官方或 BMCLAPI 镜像），返回 jar 路径。"""
    dest = os.path.join(tools_dir, "authlib-injector.jar")
    if os.path.isfile(dest):
        return dest
    urls = list(AUTHLIB_INJECTOR_META)
    if dl.resolved_source() == "bmclapi":
        urls.reverse()
    last = None
    for url in urls:
        try:
            meta = dl.fetch_json(url, mirror=False)
            download = meta["download_url"]
            if "bmclapi" in url and "bmclapi" not in download:
                download = url.rsplit("/artifact/", 1)[0] + "/artifact/{}/authlib-injector-{}.jar".format(
                    meta["build_number"], meta["version"])
            log("正在下载 authlib-injector {}...".format(meta.get("version", "")))
            tmp = dest + ".download"
            dl.download_many([DownloadTask(download, tmp)])
            with open(tmp, "rb") as f:
                digest = hashlib.sha256(f.read()).hexdigest()
            expected = (meta.get("checksums") or {}).get("sha256")
            if expected and digest != expected:
                os.remove(tmp)
                raise AccountError("authlib-injector 校验失败")
            os.replace(tmp, dest)
            return dest
        except Exception as e:
            last = e
    raise AccountError("下载 authlib-injector 失败：{}".format(last))


# ---------------------------------------------------------------------- 微软登录

def msa_device_code(client_id):
    status, data = _json_request(MS_DEVICE_CODE, form={"client_id": client_id, "scope": MS_SCOPE})
    if status != 200:
        raise AccountError("无法发起微软登录：{}".format(data.get("error_description") or data.get("error")
                                                     or "HTTP {}".format(status)))
    return data


def msa_poll(client_id, device_code):
    """轮询一次设备码登录结果：成功返回令牌数据，用户还没完成时返回 None。"""
    status, data = _json_request(MS_TOKEN, form={
        "client_id": client_id, "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
        "device_code": device_code})
    if status == 200:
        return data
    error = data.get("error")
    if error in ("authorization_pending", "slow_down"):
        return None
    if error == "authorization_declined":
        raise AccountError("你拒绝了登录授权")
    if error == "expired_token":
        raise AccountError("登录代码已过期，请重新开始")
    raise AccountError("微软登录失败：{}".format(data.get("error_description") or error or status))


def _msa_refresh_token(client_id, refresh_token):
    status, data = _json_request(MS_TOKEN, form={
        "client_id": client_id, "grant_type": "refresh_token", "refresh_token": refresh_token, "scope": MS_SCOPE})
    if status != 200:
        raise AccountError("微软账号登录已过期，请删除账号后重新登录")
    return data


def minecraft_login(ms_token):
    """微软令牌 -> Xbox Live -> XSTS -> Minecraft，返回账号 dict（不含 refresh_token）。"""
    status, xbl = _json_request(XBL_AUTH, {
        "Properties": {"AuthMethod": "RPS", "SiteName": "user.auth.xboxlive.com", "RpsTicket": "d=" + ms_token},
        "RelyingParty": "http://auth.xboxlive.com", "TokenType": "JWT"})
    if status != 200:
        raise AccountError("Xbox Live 登录失败（HTTP {}）".format(status))
    status, xsts = _json_request(XSTS_AUTH, {
        "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl["Token"]]},
        "RelyingParty": "rp://api.minecraftservices.com/", "TokenType": "JWT"})
    if status != 200:
        raise AccountError(XSTS_ERRORS.get(xsts.get("XErr"), "Xbox 授权失败（{}）".format(xsts.get("XErr") or status)))
    claims = (xsts.get("DisplayClaims") or {}).get("xui") or [{}]
    uhs = claims[0].get("uhs") or ((xbl.get("DisplayClaims") or {}).get("xui") or [{}])[0].get("uhs")
    status, mc = _json_request(MC_LOGIN, {"identityToken": "XBL3.0 x={};{}".format(uhs, xsts["Token"])})
    if status == 403:
        raise AccountError("这个 Client ID 还没有获得 Minecraft 接口权限。新注册的应用需要先向微软提交申请"
                           "（https://aka.ms/mce-reviewappid），审核通过后才能登录")
    if status != 200:
        raise AccountError("Minecraft 登录失败（HTTP {}）".format(status))
    status, profile = _json_request(MC_PROFILE, headers={"Authorization": "Bearer " + mc["access_token"]})
    if status == 404:
        raise AccountError("这个微软账号还没有购买 Minecraft: Java 版")
    if status != 200:
        raise AccountError("获取 Minecraft 档案失败（HTTP {}）".format(status))
    return {
        "type": "msa", "name": profile["name"], "uuid": profile["id"], "access_token": mc["access_token"],
        "expires_at": time.time() + int(mc.get("expires_in", 86400)), "xuid": claims[0].get("xid", ""),
    }


def msa_account(ms_tokens):
    account = minecraft_login(ms_tokens["access_token"])
    account["refresh_token"] = ms_tokens.get("refresh_token", "")
    return account


def _msa_refresh(account, client_id):
    if account.get("expires_at", 0) - 600 > time.time():
        return False
    if not client_id:
        raise AccountError("微软账号登录已过期，需要在设置中填写 Client ID 才能自动续期")
    tokens = _msa_refresh_token(client_id, account["refresh_token"])
    fresh = minecraft_login(tokens["access_token"])
    fresh["refresh_token"] = tokens.get("refresh_token") or account["refresh_token"]
    account.update(fresh)
    return True


# ---------------------------------------------------------------------- 启动

def prepare_launch(account, cfg, dl, tools_dir, log=print):
    """必要时刷新令牌，返回 (启动参数 auth, 账号信息是否有更新)。"""
    kind = account["type"]
    if kind == "offline":
        return {"name": account["name"], "uuid": account["uuid"], "token": account["uuid"],
                "user_type": "msa"}, False
    if kind == "msa":
        changed = _msa_refresh(account, cfg.get("msa_client_id", "").strip())
        return {"name": account["name"], "uuid": account["uuid"], "token": account["access_token"],
                "user_type": "msa", "xuid": account.get("xuid")}, changed
    if kind == "authlib":
        changed = _authlib_refresh(account)
        jar = ensure_authlib_injector(dl, tools_dir, log)
        status, body, _ = _request(account["api"])
        prefetched = base64.b64encode(body).decode("ascii") if status == 200 else None
        jvm_args = ["-javaagent:{}={}".format(jar, account["api"]), "-Dauthlibinjector.side=client"]
        if prefetched:
            jvm_args.append("-Dauthlibinjector.yggdrasil.prefetched=" + prefetched)
        return {"name": account["name"], "uuid": account["uuid"], "token": account["access_token"],
                "user_type": "mojang", "jvm_args": jvm_args}, changed
    raise AccountError("未知的账号类型")


def skin_url(account):
    """返回账号当前皮肤的地址，离线账号或没有皮肤时返回 None。"""
    if account["type"] == "msa":
        status, data = _json_request(MC_PROFILE, headers={"Authorization": "Bearer " + account["access_token"]})
        skins = data.get("skins") or [] if status == 200 else []
        active = next((s for s in skins if s.get("state") == "ACTIVE"), skins[0] if skins else None)
        return active.get("url") if active else None
    if account["type"] == "authlib":
        status, data = _json_request("{}/sessionserver/session/minecraft/profile/{}".format(
            account["api"], account["uuid"]))
        if status != 200:
            return None
        for prop in data.get("properties") or []:
            if prop.get("name") == "textures":
                textures = json.loads(base64.b64decode(prop["value"]).decode("utf-8")).get("textures", {})
                return (textures.get("SKIN") or {}).get("url")
    return None


def fetch_skin(account):
    url = skin_url(account)
    if not url:
        return None
    status, body, _ = _request(url)
    return body if status == 200 and body[:8] == b"\x89PNG\r\n\x1a\n" else None
