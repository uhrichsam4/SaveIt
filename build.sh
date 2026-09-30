#!/bin/bash
# Builds a universal (arm64 + x86_64) SaveIt.app, ad-hoc signs it and zips it to dist/SaveIt-macOS.zip.
#
#   ./build.sh             build dist/SaveIt.app + dist/SaveIt-macOS.zip
#   ./build.sh --install   also install to ~/Applications/SaveIt.app and relaunch it
#                          (set SAVEIT_NO_LAUNCH=1 to install without launching)
set -euo pipefail
cd "$(dirname "$0")"

INSTALL=0
for a in "$@"; do
    case "$a" in
        --install) INSTALL=1 ;;
        *) echo "unknown option: $a" >&2; exit 2 ;;
    esac
done

# Build each architecture for macOS 14 explicitly, then merge with lipo.
# (swift build --arch arm64 --arch x86_64 works too, but warns that x86_64 is deprecated for the
#  host SDK's default target; explicit triples keep the deployment target at 14.0.)
# Each slice is copied out right away: newer SwiftPM builds share one output directory.
mkdir -p .build/universal
SLICES=()
for triple in arm64-apple-macosx14.0 x86_64-apple-macosx14.0; do
    swift build -c release --triple "$triple" 2>&1
    cp "$(swift build -c release --triple "$triple" --show-bin-path)/SaveIt" ".build/universal/SaveIt-$triple"
    lipo -info ".build/universal/SaveIt-$triple"
    SLICES+=(".build/universal/SaveIt-$triple")
done
BIN=".build/universal/SaveIt"
lipo -create "${SLICES[@]}" -output "$BIN"
lipo -info "$BIN"

[ -f Resources/AppIcon.icns ] || swift scripts/make_icon.swift

DIST="dist"
APP_OUT="$DIST/SaveIt.app"
ZIP="$DIST/SaveIt-macOS.zip"
rm -rf "$APP_OUT" "$ZIP"
mkdir -p "$APP_OUT/Contents/MacOS" "$APP_OUT/Contents/Resources"
cp "$BIN" "$APP_OUT/Contents/MacOS/SaveIt"
cp Resources/Info.plist "$APP_OUT/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP_OUT/Contents/Resources/AppIcon.icns"
xattr -cr "$APP_OUT"
codesign --force --deep -s - "$APP_OUT"
ditto -c -k --keepParent "$APP_OUT" "$ZIP"
echo "Built $APP_OUT"
echo "Zipped $ZIP ($(du -h "$ZIP" | cut -f1))"

[ "$INSTALL" = "1" ] || exit 0

APP="$HOME/Applications/SaveIt.app"
OLD_APP="$HOME/Applications/Snag.app"     # previous name of this app (our own build output)

# Quit running instances (current and old name) before replacing bundles.
for name in SaveIt Snag; do
    if pgrep -x "$name" >/dev/null; then
        pkill -x "$name" || true
        for _ in $(seq 1 50); do pgrep -x "$name" >/dev/null || break; sleep 0.1; done
    fi
done

mkdir -p "$HOME/Applications"
rm -rf "$APP"
ditto "$APP_OUT" "$APP"
echo "Installed $APP"

if [ -d "$OLD_APP" ]; then
    rm -rf "$OLD_APP"
    echo "Removed old $OLD_APP"
fi

if [ "${SAVEIT_NO_LAUNCH:-0}" != "1" ]; then
    open "$APP"
    echo "Launched SaveIt"
fi
