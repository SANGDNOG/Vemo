#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
app_path="${1:-$project_root/dist/Vemo.app}"
dmg_path="${2:-$project_root/dist/Vemo-0.1.0-preview.1-arm64.dmg}"
if [ -e "$dmg_path" ]; then
    printf '%s\n' "DMG destination exists: $dmg_path" >&2
    exit 1
fi
codesign --verify --deep --strict "$app_path"
staging="$(mktemp -d "${TMPDIR:-/tmp}/vemo-dmg.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
ditto "$app_path" "$staging/Vemo.app"
ln -s /Applications "$staging/Applications"
cp "$project_root/docs/INSTALL.txt" "$staging/INSTALL.txt"
cp "$project_root/LICENSE" "$project_root/THIRD_PARTY_NOTICES.md" "$staging/"
hdiutil create -volname Vemo -srcfolder "$staging" -format UDZO -ov "$dmg_path"
hdiutil verify "$dmg_path"
(cd "$(dirname "$dmg_path")" && shasum -a 256 "$(basename "$dmg_path")" > SHA256SUMS)
printf '%s\n' "$dmg_path"
