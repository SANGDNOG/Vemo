#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
configuration="${CONFIGURATION:-release}"
app_path="${1:-$project_root/dist/Vemo.app}"
bundle_id="${BUNDLE_ID:-io.github.sangdnog.vemo}"
app_version="${APP_VERSION:-0.1.0}"
app_build="${APP_BUILD:-1}"
demo_mode="${DEMO_MODE:-0}"
runtime_path="${RUNTIME_PATH:-$project_root/build/portable/python}"
if [ -e "$app_path" ]; then
    printf '%s\n' "Destination exists; choose a fresh build path: $app_path" >&2
    exit 1
fi
if [ "$demo_mode" != 1 ] && [ ! -f "$runtime_path/vemo-runtime.json" ]; then
    printf '%s\n' "Run scripts/build-runtime.py first." >&2
    exit 1
fi
swift build --package-path "$project_root/macos" -c "$configuration" >&2
binary_dir="$(swift build --package-path "$project_root/macos" -c "$configuration" --show-bin-path)"
mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Resources"
cp "$binary_dir/VoiceMemoTranscriber" "$app_path/Contents/MacOS/Vemo"
cp "$binary_dir/VemoAudio" "$app_path/Contents/MacOS/VemoAudio"
cp "$project_root/assets/Vemo.icns" "$app_path/Contents/Resources/Vemo.icns"
cp "$project_root/LICENSE" "$project_root/THIRD_PARTY_NOTICES.md" "$app_path/Contents/Resources/"
if [ "$demo_mode" != 1 ]; then
    ditto "$runtime_path" "$app_path/Contents/Resources/runtime"
fi
python3 "$project_root/scripts/write-app-plist.py" "$app_path" "$bundle_id" "$app_version" "$app_build" "$demo_mode"
signing_config="$HOME/Library/Application Support/VoiceMemoTranscriber/signing/identity.sha1"
if [ -n "${SIGNING_IDENTITY:-}" ]; then
    signing_identity="$SIGNING_IDENTITY"
elif [ "$bundle_id" = local.sangdong.VoiceMemoTranscriber ] && [ -f "$signing_config" ]; then
    signing_identity="$(tr -d '\r\n' < "$signing_config")"
    if [[ ! "$signing_identity" =~ ^[A-Fa-f0-9]{40}$ ]]; then
        printf '%s\n' "Invalid local signing identity configuration: $signing_config" >&2
        exit 1
    fi
else
    signing_identity="-"
fi
# Sign nested runtime code before sealing the outer app. Never change identities on failure.
python3 "$project_root/scripts/sign-app.py" "$app_path" "$signing_identity"
codesign --verify --deep --strict --verbose=2 "$app_path" >&2
printf '%s\n' "$app_path"
