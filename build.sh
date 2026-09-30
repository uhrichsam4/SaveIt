#!/bin/bash
# Builds SaveIt in release mode, replaces ~/Applications/SaveIt.app (ad-hoc signed) and launches it.
# Set SAVEIT_NO_LAUNCH=1 to skip launching at the end.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release 2>&1
BIN="$(swift build -c release --show-bin-path)/SaveIt"

APP="$HOME/Applications/SaveIt.app"
OLD_APP="$HOME/Applications/Snag.app"     # previous name of this app (our own build output)
STAGE_DIR="$(mktemp -d)"
STAGE="$STAGE_DIR/SaveIt.app"
mkdir -p "$STAGE/Contents/MacOS" "$STAGE/Contents/Resources"
cp "$BIN" "$STAGE/Contents/MacOS/SaveIt"
cp Resources/Info.plist "$STAGE/Contents/Info.plist"
if [ -f Resources/AppIcon.icns ]; then cp Resources/AppIcon.icns "$STAGE/Contents/Resources/AppIcon.icns"; fi
codesign --force --deep -s - "$STAGE"

# Quit running instances (current and old name) before replacing bundles.
for name in SaveIt Snag; do
    if pgrep -x "$name" >/dev/null; then
        pkill -x "$name" || true
        for _ in $(seq 1 50); do pgrep -x "$name" >/dev/null || break; sleep 0.1; done
    fi
done

mkdir -p "$HOME/Applications"
rm -rf "$APP"
mv "$STAGE" "$APP"
rmdir "$STAGE_DIR" 2>/dev/null || true
echo "Built $APP"

if [ -d "$OLD_APP" ]; then
    rm -rf "$OLD_APP"
    echo "Removed old $OLD_APP"
fi

if [ "${SAVEIT_NO_LAUNCH:-0}" != "1" ]; then
    open "$APP"
    echo "Launched SaveIt"
fi
