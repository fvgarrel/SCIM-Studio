#!/usr/bin/env bash
# Wraps a published macOS build in "SCIM Studio.app" and puts the app in a disk image,
# beside a link to /Applications to drag it onto.
#
#   packaging/macos/bundle.sh <publish folder> <version> <disk image to write>
#
# Runs on macOS only: it needs codesign, plutil and hdiutil.
set -euo pipefail

publish="$1"
version="$2"
image="$3"
here="$(cd "$(dirname "$0")" && pwd)"

# Info.plist takes numbers only; 1.2.1-alpha.0.3 becomes 1.2.1.
short="${version%%[-+]*}"

stage="$(mktemp -d)"
app="$stage/SCIM Studio.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ScimStudio"
cp "$here/ScimStudio.icns" "$app/Contents/Resources/"
sed -e "s/{VERSION}/$short/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

# Without a Developer ID the signature is ad hoc - but one over the whole bundle, so macOS sees
# an intact app it asks about once, rather than a damaged one it refuses to open.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

ln -s /Applications "$stage/Applications"
hdiutil create -volname "SCIM Studio" -srcfolder "$stage" -ov -format UDZO "$image"
rm -rf "$stage"
