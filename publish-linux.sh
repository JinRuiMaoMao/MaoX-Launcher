#!/bin/bash
# 在 Linux 上打包 MaoX Launcher（需要 .NET 10 SDK）：./publish-linux.sh
# 生成 dist/MaoX-Launcher-Linux-x64 和 dist/MaoX-Launcher-Linux-arm64（单个可执行文件，自包含）。
set -euo pipefail
cd "$(dirname "$0")"

for RID in linux-x64 linux-arm64; do
  bash packaging/fetch-terracotta.sh "$RID" "build/terracotta/$RID/terracotta.pkg.tar.gz"
  OUT="build/publish/$RID"
  rm -rf "$OUT"
  dotnet publish src/MaoX/MaoX.csproj -nologo -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -o "$OUT"
  ARCH=${RID#linux-}
  DEST="dist/MaoX-Launcher-Linux-$ARCH"
  mkdir -p dist
  cp "$OUT/MaoXLauncher" "$DEST"
  chmod +x "$DEST"
  echo "完成：$DEST"
done
