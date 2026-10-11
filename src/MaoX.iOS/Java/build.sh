#!/bin/bash
# Builds the Java side of the iOS game runtime with JDK 8.
#   build.sh <Amethyst JavaApp/libs dir> <output dir>
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
LIBS="$(cd "$1" && pwd)"
OUT="$2"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
JAVAC="${JAVA_HOME:+$JAVA_HOME/bin/}javac"
JAR="${JAVA_HOME:+$JAVA_HOME/bin/}jar"

CP="$(find "$LIBS/lwjgl" "$LIBS/caciocavallo" "$LIBS/others" -name '*.jar' | tr '\n' ':')"
JFLAGS=(-source 8 -target 8 -nowarn -encoding UTF-8 -XDignore.symbol.file)

# lwjgl.jar = upstream LWJGL 3 (+ lwjglx for LWJGL 2 games) merged with the iOS GLFW overrides
mkdir -p "$WORK/lwjgl-classes" "$WORK/lwjgl"
"$JAVAC" "${JFLAGS[@]}" -cp "$CP" -d "$WORK/lwjgl-classes" $(find "$HERE/lwjgl" -name '*.java')
(cd "$WORK/lwjgl" && find "$LIBS/lwjgl" -name 'lwjgl*.jar' -exec "$JAR" -xf {} \; && rm -rf META-INF)
cp -R "$WORK/lwjgl-classes/." "$WORK/lwjgl/"
"$JAR" -cf "$OUT/lwjgl.jar" -C "$WORK/lwjgl" .

# maox-ios.jar = boot class + narrator stub
mkdir -p "$WORK/boot"
"$JAVAC" "${JFLAGS[@]}" -cp "$CP" -d "$WORK/boot" $(find "$HERE/boot" -name '*.java')
"$JAR" -cf "$OUT/maox-ios.jar" -C "$WORK/boot" .

# patchjna_agent.jar swaps in an iOS-aware com.sun.jna.Platform
mkdir -p "$WORK/jna"
"$JAVAC" "${JFLAGS[@]}" -cp "$CP" -d "$WORK/jna" $(find "$HERE/patchjna_agent" -name '*.java')
mv "$WORK/jna/com/sun/jna/Platform.class" "$WORK/jna/com/sun/jna/Platform.class.patch"
"$JAR" -cfm "$OUT/patchjna_agent.jar" "$HERE/patchjna_agent.txt" -C "$WORK/jna" .

rm -rf "$WORK"
find "$OUT" -name '*.jar' -size 0 | grep . && { echo "empty jar"; exit 1; } || true
echo "Built: $(ls "$OUT")"
