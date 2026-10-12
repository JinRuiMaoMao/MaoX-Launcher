#!/bin/bash
# Builds the iOS game runtime dylibs (macOS + Xcode only).
#   build.sh <Amethyst Frameworks dir> <output dir>
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
FRAMEWORKS="$(cd "$1" && pwd)"
OUT="$2"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

SDK="$(xcrun -sdk iphoneos --show-sdk-path)"
CC="xcrun -sdk iphoneos clang"
CFLAGS=(-target arm64-apple-ios15.0 -isysroot "$SDK" -O2 -fcommon -fobjc-arc -fmodules
        -Wno-deprecated-declarations -Wno-unused-command-line-argument
        -I"$HERE" -I"$HERE/external" -I"$HERE/external/fishhook" -I"$HERE/external/mach"
        -F"$SDK/System/Cryptexes/OS/System/Library/Frameworks")
LDFLAGS=(-dynamiclib -Wl,-rpath,@loader_path -Wl,-rpath,@executable_path/Frameworks)

# ANGLE desktop GL wrapper (Minecraft 1.17+)
$CC "${CFLAGS[@]}" -ObjC "${LDFLAGS[@]}" \
    -install_name @rpath/libtinygl4angle.dylib \
    -F"$FRAMEWORKS" -framework libEGL -framework libGLESv2 -framework Foundation \
    "$HERE/external/gl4es/string_utils.c" "$HERE/external/gl4es/tinygl4angle.c" \
    -o "$OUT/libtinygl4angle.dylib"

# Caciocavallo support library; links the JRE's libawt_headless at runtime (copied next to it)
TMP="$(mktemp -d)"
$CC "${CFLAGS[@]}" -dynamiclib -install_name @rpath/libawt_headless.dylib \
    "$HERE/awt_xawt/empty.m" -o "$TMP/libawt_headless.dylib"
$CC "${CFLAGS[@]}" -dynamiclib -install_name @rpath/libawt_xawt.dylib -Wl,-rpath,@loader_path \
    "$HERE/awt_xawt/xawt_fake.m" -L"$TMP" -lawt_headless \
    -o "$OUT/libawt_xawt.dylib"
rm -rf "$TMP"

# Game runtime: JVM launcher, GLFW bridge, input, JIT / dyld helpers, built-in JIT helper bridge, on-device pairing
$CC "${CFLAGS[@]}" "${LDFLAGS[@]}" \
    -install_name @rpath/libmaoxgame.dylib \
    -framework Foundation -framework UIKit -framework QuartzCore -framework CoreGraphics -framework IOKit \
    -framework BackgroundTasks -framework UserNotifications -framework OSLog \
    "$HERE/game.m" "$HERE/hooks.m" "$HERE/utils.m" "$HERE/egl_bridge.m" "$HERE/input_bridge_v3.m" "$HERE/jit_helper.m" \
    "$HERE/pairing.m" \
    "$HERE/ctxbridges/gl_bridge.m" "$HERE/dyld_bypass_validation.m" "$HERE/dyld_patch_platform.m" \
    "$HERE/external/fishhook/fishhook.c" "$HERE/external/mach/mach_excServer.c" \
    -o "$OUT/libmaoxgame.dylib"

echo "Built: $(ls "$OUT")"
