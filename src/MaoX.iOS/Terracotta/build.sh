#!/bin/bash
# Builds Terracotta (https://github.com/burningtnt/Terracotta, AGPL-3.0) as libterracotta.dylib for iOS (macOS + Xcode only).
# The official v0.4.2 source is patched with ios.patch and its src/lib.rs replaced by lib.rs from this directory.
#   build.sh <output dir>
# Set CARGO_TARGET_DIR to reuse build outputs between runs.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$1"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

VERSION=0.4.2
TOOLCHAIN=nightly-2026-02-09
TARGET=aarch64-apple-ios

WORK="$(mktemp -d)"
curl -fsSL --retry 3 "https://github.com/burningtnt/Terracotta/archive/refs/tags/v$VERSION.tar.gz" | tar xz -C "$WORK"
SRC="$WORK/Terracotta-$VERSION"
patch -d "$SRC" -p1 < "$HERE/ios.patch"
cp "$HERE/lib.rs" "$SRC/src/lib.rs"

# EasyTier only filters network interfaces on Android/Linux/macOS/FreeBSD/Windows; iOS takes the Android path
EASYTIER_TAG=v2.5.0-terracotta.2
mkdir "$WORK/EasyTier"
curl -fsSL --retry 3 "https://github.com/burningtnt/EasyTier/archive/refs/tags/$EASYTIER_TAG.tar.gz" | tar xz -C "$WORK/EasyTier" --strip-components 1
sed -i '' 's/#\[cfg(any(target_os = "android", target_env = "ohos"))\]/#[cfg(any(target_os = "android", target_os = "ios", target_env = "ohos"))]/' \
    "$WORK/EasyTier/easytier/src/common/network.rs"
grep -q 'target_os = "ios"' "$WORK/EasyTier/easytier/src/common/network.rs"
cat >> "$SRC/Cargo.toml" <<EOF

[patch."https://github.com/burningtnt/EasyTier.git"]
easytier = { path = "$WORK/EasyTier/easytier" }
EOF

rustup toolchain install "$TOOLCHAIN" --profile minimal --component rust-src
rustup target add --toolchain "$TOOLCHAIN" "$TARGET"

export IPHONEOS_DEPLOYMENT_TARGET=15.0
export TERRACOTTA_VERSION="$VERSION"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$SRC/target}"
export RUSTFLAGS="-Zunstable-options -Cpanic=unwind"
# libc 0.2.190 limited mach_task_self to macOS, which breaks num_threads on iOS
(cd "$SRC" && cargo +"$TOOLCHAIN" generate-lockfile && cargo +"$TOOLCHAIN" update -p libc --precise 0.2.189)
(cd "$SRC" && cargo +"$TOOLCHAIN" build --release --lib --target "$TARGET" \
    -Z build-std=core,std,alloc,proc_macro,panic_abort \
    -Z build-std-features=default,optimize_for_size)

cp "$CARGO_TARGET_DIR/$TARGET/release/libterracotta.dylib" "$OUT/"
install_name_tool -id @rpath/libterracotta.dylib "$OUT/libterracotta.dylib"
nm -gU "$OUT/libterracotta.dylib" | grep ' _terracotta_'
rm -rf "$WORK"
