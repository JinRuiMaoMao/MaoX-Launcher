import json
import os
import sys

if getattr(sys, "frozen", False):
    # 打包成 exe 后，数据保存在 exe 所在的文件夹
    BASE_DIR = os.path.dirname(os.path.abspath(sys.executable))
else:
    BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONFIG_PATH = os.path.join(BASE_DIR, "launcher_config.json")

DEFAULTS = {
    "username": "Steve",
    "minecraft_dir": os.path.join(BASE_DIR, ".minecraft"),
    "java_path": "",
    "max_memory": 4096,
    "download_source": "auto",
    "download_threads": 16,
    "window_width": 854,
    "window_height": 480,
    "version_isolation": True,
    "jvm_args": "",
    "last_version": "",
    "accounts": [],
    "account_index": 0,
    "msa_client_id": "",
    "after_launch": "keep",
    "background": "",
}


def load_config():
    cfg = json.loads(json.dumps(DEFAULTS))
    if os.path.isfile(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, encoding="utf-8") as f:
                data = json.load(f)
            cfg.update({k: v for k, v in data.items() if k in DEFAULTS})
        except (OSError, ValueError):
            pass
    return cfg


def save_config(cfg):
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
