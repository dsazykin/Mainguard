#!/usr/bin/env bash
# =============================================================================
# The macOS .app bundle — dev-time today, Velopack-shaped by design.
# =============================================================================
# A bare executable is a second-class citizen on macOS: no Dock identity, no Notification
# Center attribution, no mainguard:// registration, no login items, nothing to notarize. This
# script produces a real bundle in the exact layout Velopack packs for macOS (the publish
# output inside Contents/MacOS with the entry executable named by CFBundleExecutable), so the
# eventual `vpk pack --runtime osx-arm64` lane ships THE SAME shape — see README.md here for
# the packaging plan and what still needs an Apple Developer ID (signing + notarization).
#
# Two modes:
#   default   — DEV bundle: the project's APPHOST is copied in under the bundle name and every
#               other build-output file is SYMLINKED beside it, so NSBundle.mainBundle resolves
#               to this bundle (app-menu name, Dock identity, notification attribution — an
#               exec-into-dotnet launcher loses all of that: mainBundle derives from the real
#               executable path) while a plain `dotnet build` still updates the running app
#               through the symlinks. Requires this repo checkout to exist.
#   --publish — SELF-CONTAINED bundle: `dotnet publish -r osx-arm64` into Contents/MacOS, the
#               apphost copied to the bundle name. This is the Velopack input shape.
#
# The bundle executable is named "Mainguard"/"Mainguard Client" — deliberately NOT the project
# apphost name: current macOS pins an executable name to its first-run location and SIGKILLs a
# same-named apphost anywhere else (see AGENTS.md § Developing on macOS); a fresh name sidesteps
# the pin entirely (verified: renamed apphosts always run).
#
# Usage: build/macos-bundle/make-bundle.sh [--head pro|client] [--publish] [--out DIR]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

HEAD="pro"
PUBLISH=0
OUT="$HERE/out"
while [ $# -gt 0 ]; do
  case "$1" in
    --head) HEAD="$2"; shift 2 ;;
    --publish) PUBLISH=1; shift ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$HEAD" in
  pro)    PROJECT="Mainguard.Pro.App";    APPNAME="Mainguard";        BUNDLEID="com.mainguard.app" ;;
  client) PROJECT="Mainguard.Client.App"; APPNAME="Mainguard Client"; BUNDLEID="com.mainguard.client" ;;
  *) echo "--head must be pro or client" >&2; exit 2 ;;
esac

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO/$PROJECT/$PROJECT.csproj" | head -1)"
APP="$OUT/$APPNAME.app"
MACOS="$APP/Contents/MacOS"
RESOURCES="$APP/Contents/Resources"

rm -rf "$APP"
mkdir -p "$MACOS" "$RESOURCES"

# ---- Info.plist -----------------------------------------------------------------------------
# CFBundleURLTypes: the mainguard:// scheme belongs to the PRO head only (deep links target the
# agent platform); registering it from both would make LaunchServices' choice ambiguous.
URLTYPES=""
if [ "$HEAD" = "pro" ]; then
  URLTYPES='  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>Mainguard deep link</string>
      <key>CFBundleURLSchemes</key><array><string>mainguard</string></array>
    </dict>
  </array>'
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APPNAME</string>
  <key>CFBundleDisplayName</key><string>$APPNAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLEID</string>
  <key>CFBundleExecutable</key><string>$APPNAME</string>
  <key>CFBundleIconFile</key><string>Mainguard.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION:-0.0.0}</string>
  <key>CFBundleVersion</key><string>${VERSION:-0.0.0}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright © Mainguard</string>
$URLTYPES
</dict>
</plist>
PLIST

cp "$HERE/assets/Mainguard.icns" "$RESOURCES/Mainguard.icns"

# ---- The executable -------------------------------------------------------------------------
if [ "$PUBLISH" = "1" ]; then
  echo "publishing $PROJECT (osx-arm64, self-contained) into the bundle…"
  dotnet publish "$REPO/$PROJECT/$PROJECT.csproj" -c Release -r osx-arm64 --self-contained true \
    -o "$MACOS" >/dev/null
  # The Velopack shape: the entry executable carries the bundle name (fresh name — see header).
  mv "$MACOS/$PROJECT" "$MACOS/$APPNAME"
else
  BUILDOUT="$REPO/$PROJECT/bin/Release/net10.0"
  if [ ! -f "$BUILDOUT/$PROJECT.dll" ]; then
    echo "no Release build at $BUILDOUT — run 'dotnet build -c Release' first" >&2
    exit 1
  fi
  # The apphost is COPIED (a renamed copy always runs — the name-pinning note in the header)
  # so mainBundle is this bundle; everything else is a symlink into the build output, so a
  # rebuild updates the app in place. New output FILES need a re-bundle (cheap; re-run this).
  cp "$BUILDOUT/$PROJECT" "$MACOS/$APPNAME"
  for entry in "$BUILDOUT"/* ; do
    name="$(basename "$entry")"
    [ "$name" = "$PROJECT" ] && continue
    ln -s "$entry" "$MACOS/$name"
  done
fi

# Ad-hoc sign the bundle so identity-sensitive services (TCC, notifications) treat it as one app.
codesign --force --deep -s - "$APP" 2>/dev/null || true

echo "OK: $APP (${VERSION:-unversioned}, $([ "$PUBLISH" = "1" ] && echo publish || echo dev) mode)"
