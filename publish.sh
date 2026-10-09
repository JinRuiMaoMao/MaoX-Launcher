#!/bin/bash
# 在 Mac 上打包 MaoX Launcher（需要 .NET 10 SDK）：./publish.sh
# 生成 dist/osx-arm64/MaoX Launcher.app、dist/osx-x64/MaoX Launcher.app 和对应的 zip，并做 ad-hoc 签名。
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Directory.Build.props | head -n 1)

for RID in osx-arm64 osx-x64; do
  OUT="build/publish/$RID"
  rm -rf "$OUT"
  dotnet publish src/MaoX/MaoX.csproj -nologo -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -o "$OUT"

  APP="dist/$RID/MaoX Launcher.app"
  rm -rf "$APP"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$OUT"/. "$APP/Contents/MacOS/"
  cp packaging/macos/MaoX.icns "$APP/Contents/Resources/"
  sed "s/__VERSION__/$VERSION/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
  chmod +x "$APP/Contents/MacOS/MaoXLauncher"
  codesign --force --deep --sign - "$APP"

  ZIP="MaoX-Launcher-macOS-${RID#osx-}.zip"
  (cd "dist/$RID" && rm -f "../$ZIP" && ditto -c -k --keepParent "MaoX Launcher.app" "../$ZIP")
  echo "完成：dist/$ZIP"
done
