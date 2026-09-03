#!/bin/sh
set -eu
PROJECT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
SOURCE="$PROJECT_DIR/native/macos/netdiag_native.c"
mkdir -p "$PROJECT_DIR/native/bin/darwin-x64" "$PROJECT_DIR/native/bin/darwin-arm64"
cc -O2 -Wall -Wextra -mmacosx-version-min=10.13 -arch x86_64 -arch arm64 "$SOURCE" -o "$PROJECT_DIR/native/bin/netdiag-native-universal"
cp "$PROJECT_DIR/native/bin/netdiag-native-universal" "$PROJECT_DIR/native/bin/darwin-x64/netdiag-native"
cp "$PROJECT_DIR/native/bin/netdiag-native-universal" "$PROJECT_DIR/native/bin/darwin-arm64/netdiag-native"
chmod +x "$PROJECT_DIR/native/bin/darwin-x64/netdiag-native" "$PROJECT_DIR/native/bin/darwin-arm64/netdiag-native"
