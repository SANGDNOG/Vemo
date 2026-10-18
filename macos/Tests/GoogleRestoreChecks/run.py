#!/usr/bin/env python3
"""Offline restore regression checks: temporary generated source, synthetic I/O, injected Date.

Run from repository root: python3 macos/Tests/GoogleRestoreChecks/run.py
Use --revision <commit> to reproduce a historical coordinator without checking it out.
No app launch, real browser/panel, network, Keychain access, or production writes.
"""
import argparse
import hashlib
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
MODEL = "macos/Sources/VoiceMemoTranscriber/GoogleConnectionModel.swift"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--revision", help="Read coordinator from git show; default: current source")
args = parser.parse_args()
source = (subprocess.check_output(["git", "show", f"{args.revision}:{MODEL}"], cwd=REPO, text=True)
          if args.revision else (REPO / MODEL).read_text())
print("COORDINATOR_SHA256:", hashlib.sha256(source.encode()).hexdigest(), flush=True)
text = source.replace("Bundle.main", "Probe.bundle").replace("import NativeCore\n", "").replace("NSOpenPanel", "ProbeOpenPanel")
# Keep every coordinator method, including retry classification/state, unchanged.
# Replace only browser/panel and I/O dependencies, following the existing lifecycle fixture.
start = text.index("    private static func openAuthorization(")
opening = text.index("{", start)
depth, end = 1, opening + 1
while depth:
    depth += (text[end] == "{") - (text[end] == "}")
    end += 1
text = text[:opening + 1] + "\n Probe.browserOpens += 1\n try Task.checkCancellation()\n }" + text[end:]
text = text[:text.index("/// Descriptor-relative access")]
if "func restoreIfNeeded() async" in text:
    # Baseline adapter intentionally ignores now; never changes the restore implementation.
    text += "\n@MainActor extension GoogleConnectionModel { func restoreIfNeeded(now: Date) async { await restoreIfNeeded() } }\n"
    print("BASELINE_ADAPTER: no-argument restore forwarded unchanged", flush=True)
policy = "import Foundation\nenum AutomaticRecordingPolicy {" + (REPO / "macos/Sources/VoiceMemoTranscriber/AppModel.swift").read_text().split("enum AutomaticRecordingPolicy {", 1)[1]
with tempfile.TemporaryDirectory(prefix="vemo-restore-regression-") as scratch:
    out = Path(scratch)
    config = (REPO / "macos/Sources/NativeCore/GoogleOAuth.swift").read_text().split("public struct GoogleOAuthClientConfiguration", 1)[1].split("public struct GoogleOAuthPKCE", 1)[0]
    (out / "Configuration.swift").write_text("import Foundation\npublic struct GoogleOAuthClientConfiguration" + config)
    (out / "Generated.swift").write_text(text)
    (out / "Policy.swift").write_text(policy)
    command = ["swiftc", "-swift-version", "6", "-parse-as-library", str(HERE / "Boundaries.swift"),
               str(out / "Configuration.swift"), str(out / "Generated.swift"), str(out / "Policy.swift"), str(HERE / "Checks.swift"),
               "-o", str(out / "checks")]
    compiled = subprocess.run(command, capture_output=True, text=True, timeout=60)
    print(compiled.stdout + compiled.stderr, end="")
    print("COMPILE_EXIT:", compiled.returncode, flush=True)
    if compiled.returncode:
        raise SystemExit(compiled.returncode)
    result = subprocess.run([str(out / "checks")], capture_output=True, text=True, timeout=20)
    print(result.stdout + result.stderr, end="")
    print("CHECKS_EXIT:", result.returncode)
    raise SystemExit(result.returncode)
