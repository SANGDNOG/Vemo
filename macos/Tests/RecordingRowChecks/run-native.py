#!/usr/bin/env python3
"""Shipping recording List/input extracts, actual native Surface, synthetic model only."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
ROOT = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output-dir', type=Path, required=True)
a = parser.parse_args(); out = a.output_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
def block(s, marker):
    start = s.index(marker); opening = s.index('{',start); depth = 0
    for end in range(opening,len(s)):
        if s[end] == '{': depth += 1
        elif s[end] == '}':
            depth -= 1
            if depth == 0: return s[start:end+1]
    raise ValueError(marker)
source = ROOT/'macos/Sources/VoiceMemoTranscriber'
views = (source/'LibraryView.swift').read_text()
folders = (source/'FolderViews.swift').read_text()
listing = block(views, '                    List(model.visibleJobs, selection: $model.selection)')
methods = block(views,'    private func recordingRow(')
if '    private func recordingInput(' in views:
    methods += '\n'+block(views,'    private func recordingInput(')
methods += '\n'+block(views,'    private func dragProvider(')
chunks = {'list':listing, 'methods':methods, 'session':block(folders,'@MainActor final class VemoLibraryDragSession'), 'uti':block(folders,'extension UTType'), 'scroll':block(folders,'struct VemoScrollAppearance:')}
code = Path(__file__).with_name('Native.swift.template').read_text()
for key,value in chunks.items(): code = code.replace('/* '+key.upper()+' */',value)
generated = out/'Native.swift'; generated.write_text(code)
sources = [source/'LibraryItemInteraction.swift']
for name in ['LibraryFolderStore.swift','LibraryFolderHierarchy.swift','LibraryDragPayload.swift','Validation.swift']:
    sources.append(ROOT/'macos/Sources/NativeCore'/name)
command = ['swiftc','-swift-version','6','-parse-as-library',*map(str,sources),str(generated),'-o',str(out/'checks')]
b = subprocess.run(command,capture_output=True,text=True,timeout=90)
(out/'build.log').write_text(b.stdout+b.stderr)
r = {'compile_exit':b.returncode,'extract_sha256':{k:hashlib.sha256(v.encode()).hexdigest() for k,v in chunks.items()},'scope':'shipping List/recording row/input/session/scroll extracts; actual Surface, synthetic model and owned offscreen window; direct local mouse method calls, process-local captured drag start; no global input, live apps, engine, private data, or actual drag bridge'}
if b.returncode == 0:
    run = subprocess.run([str(out/'checks')],capture_output=True,text=True,timeout=15)
    output = run.stdout+run.stderr; (out/'native.log').write_text(output); print(output); r['run_exit']=run.returncode
else: print(b.stdout+b.stderr)
(out/'result.json').write_text(json.dumps(r,indent=2)+'\n')
raise SystemExit(r.get('run_exit',1))
