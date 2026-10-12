#!/bin/bash
# Builds the built-in JIT helper extension and the StikJIT framework it links (macOS + Xcode only).
#   build.sh <output dir> <app bundle identifier> <version>
# Output:
#   MaoXJITHelper.appex   -> the app's PlugIns/
#   StikJIT.framework     -> the app's Frameworks/ (the helper finds it at @executable_path/../../Frameworks)
#   stikjit-origin.txt    -> "prebuilt" or "source"
# STIKJIT_FROM=auto (default) uses the official release binary and builds the same commit from source only when
# this Xcode's Swift compiler cannot import it; STIKJIT_FROM=prebuilt / source forces one of them.
set -euo pipefail

STIKJIT_VERSION=1.9.0
STIKJIT_COMMIT=32287268fa5824f9edce4cb359f5833ce0cf7b00
STIKJIT_SHA256=806664393770c68e75f2b6429955bfdd88cfaad09fec2ba70f8ed615ff90c060

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$1"
APP_ID="$2"
VERSION="$3"
FROM="${STIKJIT_FROM:-auto}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

SDK="$(xcrun -sdk iphoneos --show-sdk-path)"
xcrun -sdk iphoneos swiftc --version

prebuilt_framework_dir() {
    curl -fsSL --retry 3 -o "$WORK/StikJIT.xcframework.zip" \
        "https://github.com/StikDebug/StikJIT/releases/download/$STIKJIT_VERSION/StikJIT.xcframework.zip"
    echo "$STIKJIT_SHA256  $WORK/StikJIT.xcframework.zip" | shasum -a 256 -c - >&2
    unzip -q "$WORK/StikJIT.xcframework.zip" -d "$WORK/prebuilt"
    head -2 "$WORK/prebuilt/StikJIT.xcframework/ios-arm64/StikJIT.framework/Modules/StikJIT.swiftmodule/arm64-apple-ios.swiftinterface" >&2
    echo "$WORK/prebuilt/StikJIT.xcframework/ios-arm64"
}

# StikJIT's own release recipe (.github/workflows/build.yml at that commit) with this Xcode
source_framework_dir() {
    git clone -q --depth 1 --branch "$STIKJIT_VERSION" https://github.com/StikDebug/StikJIT.git "$WORK/src" >&2
    local head
    head="$(git -C "$WORK/src" rev-parse HEAD)"
    if [ "$head" != "$STIKJIT_COMMIT" ]; then
        echo "StikJIT tag $STIKJIT_VERSION is $head, expected $STIKJIT_COMMIT" >&2
        exit 1
    fi
    command -v xcodegen >/dev/null || brew install xcodegen >&2
    (cd "$WORK/src" && xcodegen generate --quiet >&2 && xcodebuild archive -quiet \
        -project StikJIT.xcodeproj -scheme StikJIT -destination 'generic/platform=iOS' \
        -archivePath "$WORK/src/build/StikJIT-iOS" \
        SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES CODE_SIGNING_ALLOWED=NO >&2)
    echo "$WORK/src/build/StikJIT-iOS.xcarchive/Products/Library/Frameworks"
}

APPEX="$OUT/MaoXJITHelper.appex"

# $1: directory containing StikJIT.framework
compile_helper() {
    rm -rf "$APPEX"
    mkdir -p "$APPEX"
    xcrun -sdk iphoneos swiftc -target arm64-apple-ios17.4 -sdk "$SDK" -O -parse-as-library -application-extension \
        -module-name MaoXJITHelper -F "$1" -framework StikJIT \
        -Xlinker -e -Xlinker _NSExtensionMain \
        -Xlinker -application_extension \
        -Xlinker -rpath -Xlinker @executable_path/../../Frameworks \
        "$HERE/MaoXJITHelper.swift" -o "$APPEX/MaoXJITHelper"
}

FRAMEWORKS=""
ORIGIN=""
if [ "$FROM" != source ]; then
    FRAMEWORKS="$(prebuilt_framework_dir)"
    if compile_helper "$FRAMEWORKS" 2>"$WORK/prebuilt.log"; then
        ORIGIN=prebuilt
    else
        cat "$WORK/prebuilt.log"
        if [ "$FROM" = prebuilt ]; then
            exit 1
        fi
        echo "::warning::This Xcode cannot use the StikJIT $STIKJIT_VERSION release binary; building $STIKJIT_COMMIT from source"
    fi
fi
if [ -z "$ORIGIN" ]; then
    FRAMEWORKS="$(source_framework_dir)"
    compile_helper "$FRAMEWORKS"
    ORIGIN=source
fi
echo "$ORIGIN" > "$OUT/stikjit-origin.txt"
echo "StikJIT $STIKJIT_VERSION ($ORIGIN)"

sed -e "s/@APP_ID@/$APP_ID/g" -e "s/@VERSION@/$VERSION/g" "$HERE/Info.plist" > "$APPEX/Info.plist"
# The keys Xcode adds to every bundle it builds, so the helper looks like any other app extension to iOS
SDK_VERSION="$(xcrun -sdk iphoneos --show-sdk-version)"
SDK_BUILD="$(xcrun -sdk iphoneos --show-sdk-build-version)"
XCODE_VERSION="$(xcodebuild -version | awk '/^Xcode/ {print $2}')"
XCODE_BUILD="$(xcodebuild -version | awk '/Build version/ {print $3}')"
IFS=. read -r XC_MAJOR XC_MINOR XC_PATCH <<< "$XCODE_VERSION"
plist="$APPEX/Info.plist"
plutil -insert DTPlatformName -string iphoneos "$plist"
plutil -insert DTPlatformVersion -string "$SDK_VERSION" "$plist"
plutil -insert DTPlatformBuild -string "$SDK_BUILD" "$plist"
plutil -insert DTSDKName -string "iphoneos$SDK_VERSION" "$plist"
plutil -insert DTSDKBuild -string "$SDK_BUILD" "$plist"
plutil -insert DTXcode -string "$(printf '%d%d%d' "$XC_MAJOR" "${XC_MINOR:-0}" "${XC_PATCH:-0}")" "$plist"
plutil -insert DTXcodeBuild -string "$XCODE_BUILD" "$plist"
plutil -insert DTCompiler -string com.apple.compilers.llvm.clang.1_0 "$plist"
plutil -insert BuildMachineOSBuild -string "$(sw_vers -buildVersion)" "$plist"
plutil -insert UIRequiredDeviceCapabilities -array "$plist"
plutil -insert UIRequiredDeviceCapabilities.0 -string arm64 "$plist"
plutil -lint "$plist"
plutil -p "$plist"

# The framework goes into the app's Frameworks, where sideloaders re-sign it. installd refuses an embedded
# framework without an Info.plist, and the release binary has none: add a minimal one, nothing else changes.
FW="$OUT/StikJIT.framework"
rm -rf "$FW"
ditto "$FRAMEWORKS/StikJIT.framework" "$FW"
rm -rf "$FW/Headers" "$FW/Modules" "$FW/_CodeSignature"
if [ ! -f "$FW/Info.plist" ]; then
    plutil -create xml1 "$FW/Info.plist"
    plutil -insert CFBundleIdentifier -string "$APP_ID.StikJIT" "$FW/Info.plist"
    plutil -insert CFBundleExecutable -string StikJIT "$FW/Info.plist"
    plutil -insert CFBundleName -string StikJIT "$FW/Info.plist"
    plutil -insert CFBundlePackageType -string FMWK "$FW/Info.plist"
    plutil -insert CFBundleInfoDictionaryVersion -string 6.0 "$FW/Info.plist"
    plutil -insert CFBundleShortVersionString -string "$STIKJIT_VERSION" "$FW/Info.plist"
    plutil -insert CFBundleVersion -string "$STIKJIT_VERSION" "$FW/Info.plist"
    plutil -insert MinimumOSVersion -string 17.4 "$FW/Info.plist"
    plutil -insert CFBundleSupportedPlatforms -array "$FW/Info.plist"
    plutil -insert CFBundleSupportedPlatforms.0 -string iPhoneOS "$FW/Info.plist"
fi
cp "$HERE/LICENSE.StikJIT-MPL-2.0.txt" "$HERE/LICENSE.idevice-MIT.txt" "$HERE/THIRD_PARTY_NOTICES.txt" "$FW/"

# Static checks: what the helper links and where it looks for it
bin="$APPEX/MaoXJITHelper"
lipo -info "$bin"
otool -L "$bin"
otool -L "$bin" | grep -q "@rpath/StikJIT.framework/StikJIT"
otool -l "$bin" | grep -A2 LC_RPATH | grep -q "@executable_path/../../Frameworks"
nm -u "$bin" | grep -q "_NSExtensionMain"
strings "$bin" | grep -q "MaoXJITHelperHandler"
lipo -info "$FW/StikJIT" | grep -q arm64
echo "Built: $(ls "$OUT")"
