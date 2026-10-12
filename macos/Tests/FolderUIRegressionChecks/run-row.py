#!/usr/bin/env python3
"""Passive row hit-route comparison: HEAD gesture row versus current source; no events."""
import argparse
import importlib.util
import hashlib
import json
from pathlib import Path
import subprocess
import re
spec = importlib.util.spec_from_file_location('hosting_runner', Path(__file__).with_name('run.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', type=Path, required=True)
args = parser.parse_args()
out = args.output_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
views_path = 'macos/Sources/VoiceMemoTranscriber/FolderViews.swift'
views = {'head-gesture': subprocess.check_output(['git', 'show', '3f0b9c9:' + views_path], text=True, cwd=runner.ROOT), 'working-current': (runner.ROOT / views_path).read_text()}
theme = runner.block((runner.ROOT / 'macos/Sources/VoiceMemoTranscriber/VemoTheme.swift').read_text(), r'^enum VemoTheme\b')
template = Path(__file__).with_name('RowHitChecks.swift.template').read_text()
results = {}
for label, source in views.items():
    chunks = theme + runner.block(source, r'^struct VemoFolderRow: View\b') + runner.block(source, r'^struct VemoDragSource: ViewModifier\b')
    generated = out / (label + '.swift'); generated.write_text(template.replace('/* ROW_SOURCE */', chunks))
    binary = out / label
    cmd = ['swiftc', '-swift-version', '6', '-parse-as-library', str(generated), '-o', str(binary)]
    build = subprocess.run(cmd, capture_output=True, text=True, timeout=90, cwd=runner.ROOT)
    (out / (label + '-build.log')).write_text(build.stdout + build.stderr)
    result = {'compile_exit': build.returncode, 'compile_command': cmd, 'extract_sha256': hashlib.sha256(chunks.encode()).hexdigest()}
    if not build.returncode:
        run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10, cwd=runner.ROOT)
        output = run.stdout + run.stderr
        (out / (label + '.log')).write_text(output)
        print(label + '\n' + output)
        counts = re.search(r'RESULT: (\d+) assertions passed; (\d+) failed', output)
        result.update(run_exit=run.returncode, passed=int(counts[1]) if counts else None, failed=int(counts[2]) if counts else None)
    else:
        print(build.stdout + build.stderr)
    results[label] = result
results['scope'] = 'passive native hit routes and recognizer inventory; no mouse/key events, gesture firing, drop delivery, accessibility action, or product behavior success inferred'
(out / 'result.json').write_text(json.dumps(results, indent=2) + '\n')
raise SystemExit(0 if all(results[label].get('run_exit') == 0 for label in views) else 1)
