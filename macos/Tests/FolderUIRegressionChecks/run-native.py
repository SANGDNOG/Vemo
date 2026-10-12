#!/usr/bin/env python3
"""Actual native interaction source in hosting; direct local method events, never global input."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import re
spec = importlib.util.spec_from_file_location('hosting_runner', Path(__file__).with_name('run.py'))
runner = importlib.util.module_from_spec(spec); spec.loader.exec_module(runner)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', type=Path, required=True)
parser.add_argument('--negative-control', action='store_true', help='generated product-copy mouseUp omits drag guard; expected failure')
args = parser.parse_args(); out = args.output_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
source_dir = runner.ROOT/'macos/Sources/VoiceMemoTranscriber'
interaction = (source_dir/'LibraryItemInteraction.swift').read_bytes()
original_interaction = interaction
if args.negative_control:
    target = b'guard pressedAt != nil, !dragged, bounds.contains'
    if interaction.count(target) != 1:
        raise ValueError('Product mouseUp negative-control target changed')
    interaction = interaction.replace(target, b'guard pressedAt != nil, bounds.contains')
interaction_path = out/'LibraryItemInteraction.swift'; interaction_path.write_bytes(interaction)
views = (source_dir/'FolderViews.swift').read_text()
session = runner.block(views, r'^extension UTType\b') + runner.block(views, r'^@MainActor final class VemoLibraryDragSession\b')
row = runner.block(views, r'^struct VemoFolderRow: View\b') + runner.block(views, r'^struct VemoDragSource: ViewModifier\b') + runner.block((source_dir/'VemoTheme.swift').read_text(), r'^enum VemoTheme\b')
template = Path(__file__).with_name('NativeInteraction.swift.template').read_text()
generated = out/'NativeChecks.swift'; generated.write_text(template.replace('/* SESSION_SOURCE */',session).replace('/* ROW_SOURCE */',row))
sources = [interaction_path, Path(__file__).with_name('SyntheticDraggingInfo.swift')]
hashes = {'LibraryItemInteraction.swift': hashlib.sha256(interaction).hexdigest(), 'session_extract': hashlib.sha256(session.encode()).hexdigest(), 'row_extract': hashlib.sha256(row.encode()).hexdigest(), 'template': hashlib.sha256(template.encode()).hexdigest()}
for name in ['LibraryFolderStore.swift', 'LibraryFolderHierarchy.swift', 'LibraryDragPayload.swift', 'Validation.swift']:
    data = (runner.ROOT/'macos/Sources/NativeCore'/name).read_bytes()
    dest = out/name; dest.write_bytes(data); sources.append(dest); hashes[name] = hashlib.sha256(data).hexdigest()
binary=out/'native-interaction-checks'
cmd=['swiftc','-swift-version','6','-parse-as-library',*map(str,sources),str(generated),'-o',str(binary)]
build=subprocess.run(cmd,text=True,capture_output=True,timeout=90,cwd=runner.ROOT)
(out/'build.log').write_text(build.stdout+build.stderr)
result={'compile_exit':build.returncode,'compile_command':cmd,'source_hashes':hashes,'negative_control':args.negative_control,'original_interaction_sha256':hashlib.sha256(original_interaction).hexdigest(),'scope':'product native source snapshot (generated-copy mouseUp mutation only for explicitly requested negative control) + session/row extracts; offscreen real hosting, direct local NSEvent method calls, synthetic NSDraggingInfo with unique fixture pasteboards; process-only interception of AppKit drag-session-start returns inert session, no actual native drag/global input/live apps or durable app save'}
if build.returncode:
    print(build.stdout+build.stderr)
else:
    try:
        run=subprocess.run([str(binary)],text=True,capture_output=True,timeout=15,cwd=runner.ROOT)
        output=run.stdout+run.stderr; result['run_exit']=run.returncode
        counts=re.search(r'RESULT: (\d+) assertions passed; (\d+) failed',output)
        if counts: result.update(passed=int(counts[1]),failed=int(counts[2]))
    except subprocess.TimeoutExpired as error:
        output=(error.stdout or b'').decode()+(error.stderr or b'').decode(); result['timeout_seconds']=15
    (out/'native.log').write_text(output);print(output,end='')
(out/'result.json').write_text(json.dumps(result,indent=2)+'\n')
raise SystemExit(0 if result.get('run_exit')==0 else 1)
