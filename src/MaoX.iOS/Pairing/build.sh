#!/bin/bash
# Builds libmaoxpair.dylib (device-initiated pairing for iOS 27+, on idevice / MIT) for iOS (macOS + Xcode only).
# Only the experimental built-in JIT IPA ships it; Native/pairing.m loads it at runtime.
#   build.sh <output dir>
# Set CARGO_TARGET_DIR to reuse build outputs between runs.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$1"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

TARGET=aarch64-apple-ios
rustup target add "$TARGET"
rustc --version

export IPHONEOS_DEPLOYMENT_TARGET=15.0
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$HERE/target}"
(cd "$HERE" && cargo build --release --lib --target "$TARGET")

cp "$CARGO_TARGET_DIR/$TARGET/release/libmaoxpair.dylib" "$OUT/"
install_name_tool -id @rpath/libmaoxpair.dylib "$OUT/libmaoxpair.dylib"
nm -gU "$OUT/libmaoxpair.dylib" | grep ' _maox_pair_'
lipo -info "$OUT/libmaoxpair.dylib"
otool -L "$OUT/libmaoxpair.dylib"
ls -la "$OUT/libmaoxpair.dylib"
