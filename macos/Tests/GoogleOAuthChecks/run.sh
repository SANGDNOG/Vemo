#!/bin/sh
set -eu
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/vemo-google-oauth.XXXXXX")
trap 'rm -rf "$scratch"' EXIT HUP INT TERM
swiftc -parse-as-library -swift-version 6 -warnings-as-errors \
    "$repo_root"/macos/Sources/NativeCore/GoogleOAuth*.swift \
    "$repo_root"/macos/Tests/GoogleOAuthChecks/main.swift \
    -o "$scratch/GoogleOAuthChecks"
"$scratch/GoogleOAuthChecks"
