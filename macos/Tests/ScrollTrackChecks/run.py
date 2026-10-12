#!/usr/bin/env python3
"""Compile the shipping scroll helper unchanged; render scroller primitives offscreen."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', type=Path, required=True)
args = parser.parse_args()
out = args.output_dir.resolve()
out.mkdir(parents=True, exist_ok=True)
source = (ROOT / 'macos/Sources/VoiceMemoTranscriber/FolderViews.swift').read_text()
start = source.index('struct VemoScrollAppearance:')
opening = source.index('{', start)
depth = 0
for end in range(opening, len(source)):
    if source[end] == '{': depth += 1
    elif source[end] == '}':
        depth -= 1
        if depth == 0: break
helper = source[start:end + 1]
code = Path(__file__).with_name('Harness.swift.template').read_text().replace('/* HELPER */', helper)
generated = out / 'ScrollTrackChecks.swift'
generated.write_text(code)
command = ['swiftc', '-swift-version', '6', '-parse-as-library', str(generated), '-o', str(out / 'checks')]
build = subprocess.run(command, capture_output=True, text=True, timeout=90)
(out / 'build.log').write_text(build.stdout + build.stderr)
result = {'compile_exit': build.returncode, 'command': command, 'helper_sha256': hashlib.sha256(helper.encode()).hexdigest()}
if build.returncode == 0:
    run = subprocess.run([str(out / 'checks')], capture_output=True, text=True, timeout=30)
    (out / 'checks.log').write_text(run.stdout + run.stderr)
    print(run.stdout + run.stderr)
    result['run_exit'] = run.returncode
else:
    print(build.stdout + build.stderr)
(out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
raise SystemExit(result.get('run_exit', build.returncode))
