#!/usr/bin/env python3
"""Synthetic List selection boundary; no real model, files, or live application input."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
ROOT = Path(__file__).resolve().parents[3]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--output-dir', type=Path, required=True)
a = p.parse_args(); out = a.output_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
s = (ROOT/'macos/Sources/VoiceMemoTranscriber/LibraryView.swift').read_text()
start = s.index('    private func recordingRow('); opening = s.index('{', start); depth = 0
for end in range(opening, len(s)):
    if s[end] == '{': depth += 1
    elif s[end] == '}':
        depth -= 1
        if depth == 0: break
row = s[start:end+1]
template = Path(__file__).with_name('Harness.swift.template').read_text()
file = out/'RecordingChecks.swift'; file.write_text(template.replace('/* ROW */', row))
cmd = ['swiftc','-swift-version','6','-parse-as-library',str(file),'-o',str(out/'checks')]
b = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
(out/'build.log').write_text(b.stdout+b.stderr)
r = {'compile_exit': b.returncode, 'row_sha256': hashlib.sha256(row.encode()).hexdigest(), 'scope': 'shipping recordingRow body; synthetic Job/model and List modifier comparison; direct local hit-view event calls in owned offscreen window; no live UI, notes loading, production model, actual drag session or global input'}
if b.returncode == 0:
    try:
        run = subprocess.run([str(out/'checks')],capture_output=True,text=True,timeout=15)
        output = run.stdout+run.stderr; r['run_exit'] = run.returncode
    except subprocess.TimeoutExpired as e:
        output = (e.stdout or b'').decode()+(e.stderr or b'').decode(); r['timeout'] = 15
    (out/'checks.log').write_text(output); print(output)
else: print(b.stdout+b.stderr)
(out/'result.json').write_text(json.dumps(r,indent=2)+'\n')
raise SystemExit(r.get('run_exit',1))
