#!/bin/bash
# 下载未经修改的陶瓦官方安装包，供 publish 嵌入启动器。不提交到 git。
set -euo pipefail
RID="${1:?rid}"
DEST="${2:?dest}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/src/MaoX.Core/Multiplayer.cs"
VERSION="$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$SRC" | head -n 1)"
case "$RID" in
  win-x64) CLASSIFIER=windows-x86_64 ;;
  win-arm64) CLASSIFIER=windows-arm64 ;;
  osx-arm64) CLASSIFIER=macos-arm64 ;;
  osx-x64) CLASSIFIER=macos-x86_64 ;;
  linux-x64) CLASSIFIER=linux-x86_64 ;;
  linux-arm64) CLASSIFIER=linux-arm64 ;;
  *) echo "未知 RID：$RID" >&2; exit 1 ;;
esac
HASH="$(python3 - "$SRC" "$CLASSIFIER" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
m = re.search(r'\["%s"\] = new\(\s*"([0-9a-f]{128})"' % re.escape(sys.argv[2]), text)
if not m:
    sys.exit("missing hash")
print(m.group(1))
PY
)"
NAME="terracotta-${VERSION}-${CLASSIFIER}-pkg.tar.gz"
GITHUB="https://github.com/burningtnt/Terracotta/releases/download/v${VERSION}/${NAME}"
MIRRORS=(
  "https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v${VERSION}/${NAME}"
  "https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v${VERSION}/${NAME}"
  "https://ghfast.top/${GITHUB}"
  "https://gh-proxy.com/${GITHUB}"
)
if [ "${CI:-}" = "true" ]; then
  URLS=("$GITHUB" "${MIRRORS[@]}")
else
  URLS=("${MIRRORS[@]}" "$GITHUB")
fi

sha_of() {
  if command -v sha512sum >/dev/null 2>&1; then
    sha512sum "$1" | awk '{print $1}'
  else
    shasum -a 512 "$1" | awk '{print $1}'
  fi
}

if [ -f "$DEST" ] && [ "$(sha_of "$DEST")" = "$HASH" ]; then
  exit 0
fi

mkdir -p "$(dirname "$DEST")"
TMP="$DEST.part"
for url in "${URLS[@]}"; do
  echo "> 下载陶瓦 $url"
  rm -f "$TMP"
  if curl -fL --retry 2 --connect-timeout 20 --max-time 180 -o "$TMP" "$url"; then
    size=$(wc -c < "$TMP" | tr -d ' ')
    if [ "$size" -gt 1048576 ] && [ "$(sha_of "$TMP")" = "$HASH" ]; then
      mv "$TMP" "$DEST"
      exit 0
    fi
    echo "  失败：大小或校验不符"
  else
    echo "  失败：下载出错"
  fi
  rm -f "$TMP"
done
echo "无法下载陶瓦联机 $NAME" >&2
exit 1
