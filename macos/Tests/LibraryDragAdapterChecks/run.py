#!/usr/bin/env python3
"""Real providers/store and unchanged shipping adapters; synthetic SwiftUI drop boundary only."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import os
ROOT = Path(__file__).resolve().parents[3]
CORE = ROOT / 'macos/Sources/NativeCore'
VIEWS = ROOT / 'macos/Sources/VoiceMemoTranscriber/FolderViews.swift'
MODEL = ROOT / 'macos/Sources/VoiceMemoTranscriber/AppModel.swift'
METHODS = ['libraryDragPayload', 'canDropLibraryItem', 'dropLibraryItem', 'changeLibrary', 'flushLibraryChanges', 'reconcileLibrarySelection']
CORE_NAMES = ['LibraryFolderStore.swift', 'LibraryFolderHierarchy.swift', 'LibraryDragPayload.swift', 'LibraryQuery.swift', 'WireTypes.swift', 'Validation.swift']
def block(source, pattern):
    match = re.search(pattern, source, re.M)
    if not match: raise ValueError('Missing declaration: ' + pattern)
    opening = source.index('{', match.end()); depth = 0
    for end in range(opening, len(source)):
        if source[end] == '{': depth += 1
        elif source[end] == '}':
            depth -= 1
            if depth == 0: return source[match.start():end + 1] + '\n'
    raise ValueError('Unclosed declaration: ' + pattern)
def sha(data): return hashlib.sha256(data if isinstance(data, bytes) else data.encode()).hexdigest()
def execute(out, label, code, sources):
    swift = out / (label + '.swift'); binary = out / label; swift.write_text(code)
    command = ['swiftc', '-swift-version', '6', '-parse-as-library', *map(str, sources), str(swift), '-o', str(binary)]
    compiled = subprocess.run(command, text=True, capture_output=True, timeout=90, cwd=ROOT)
    (out / (label + '-build.log')).write_text(compiled.stdout + compiled.stderr)
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr, file=sys.stderr)
        return {'compile_exit': compiled.returncode, 'compile_command': command}, ''
    environment = os.environ.copy(); environment['VEMO_DRAG_CHECK_ROOT'] = str(out / 'states')
    run = subprocess.run([str(binary)], env=environment, text=True, capture_output=True, timeout=45, cwd=ROOT)
    output = run.stdout + run.stderr; (out / (label + '.log')).write_text(output)
    print(output, end='', flush=True)
    counts = re.search(r'RESULT: (\d+) assertions passed; (\d+) failed', output)
    return {'compile_exit': 0, 'run_exit': run.returncode, 'compile_command': command, 'run_command': [str(binary)], 'passed': int(counts[1]) if counts else None, 'failed': int(counts[2]) if counts else None}, output
BASELINE = '''
import Foundation
import UniformTypeIdentifiers
import Darwin
/* SESSION */
@main struct Baseline {
 @MainActor static func main() async throws {
  let value = LibraryDragPayload(kind: .folder, id: "same-folder", sessionID: "legitimate-session")
  let session = VemoLibraryDragSession()
  let original = session.provider(for: value)
  let bytes: Data = try await withCheckedThrowingContinuation { continuation in
   _ = original.loadDataRepresentation(forTypeIdentifier: UTType.vemoLibraryItem.identifier) { data, error in
    if let data { continuation.resume(returning: data) }
    else { continuation.resume(throwing: error ?? NSError(domain: "missing", code: 1)) }
   }
  }
  let reconstructed = NSItemProvider()
  reconstructed.registerDataRepresentation(forTypeIdentifier: UTType.vemoLibraryItem.identifier, visibility: .all) { completion in completion(bytes, nil); return nil }
  let decoded = try JSONDecoder().decode(LibraryDragPayload.self, from: bytes)
  var passed = 0; var failed = 0
  func check(_ yes: Bool, _ title: String) { if yes { passed += 1; print("PASS: " + title) } else { failed += 1; print("FAIL: " + title) } }
  check(decoded == value, "base-only reconstruction contains legitimate exact payload bytes")
  check(session.payload(for: [original]) == value, "original provider accepted as baseline control")
  check(reconstructed.registeredTypeIdentifiers == [UTType.vemoLibraryItem.identifier], "reconstruction preserves only declared base UTI")
  check(session.payload(for: [reconstructed]) == value, "legitimate base-only reconstruction accepted without unknown namescope token")
  print("original types: \\(original.registeredTypeIdentifiers)")
  print("reconstructed types: \\(reconstructed.registeredTypeIdentifiers)")
  print("RESULT: \\(passed) assertions passed; \\(failed) failed")
  fflush(stdout); exit(failed == 0 ? 0 : 1)
 }
}
'''
def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--baseline-only', action='store_true')
    parser.add_argument('--mutation-check', action='store_true', help='bounded negative controls in generated sources only')
    args = parser.parse_args(); out = args.output_dir.resolve(); out.mkdir(parents=True, exist_ok=True)
    views = VIEWS.read_text()
    session = block(views, r'^extension UTType\b') + block(views, r'^@MainActor final class VemoLibraryDragSession\b')
    if args.baseline_only:
        payload = out / 'baseline-LibraryDragPayload.swift'; payload.write_bytes((CORE / 'LibraryDragPayload.swift').read_bytes())
        sources = [CORE / n for n in ['LibraryFolderStore.swift', 'LibraryFolderHierarchy.swift', 'Validation.swift']] + [payload]
        result, output = execute(out, 'baseline', BASELINE.replace('/* SESSION */', session), sources)
        result['session_sha256'] = sha(session); result['payload_sha256'] = sha(payload.read_bytes())
        result['expected_red_observed'] = result.get('run_exit') == 1 and result.get('failed') == 1 and 'FAIL: legitimate base-only reconstruction accepted without unknown namescope token' in output
        result['scope'] = 'synthetic base-only reconstruction with real NSItemProvider and legitimate bytes; actual AppKit pasteboard bridge/UI not exercised'
        (out / 'baseline-result.json').write_text(json.dumps(result, indent=2) + '\n')
        return 1 if result['expected_red_observed'] else 2
    model = MODEL.read_text()
    chunks = {n: block(model, r'^    (?:private )?func ' + n + r'\b') for n in METHODS}
    chunks['visibleJobs'] = block(model, r'^    var visibleJobs:')
    chunks['sessionID'] = re.search(r'^    let libraryDragSessionID = .*$', model, re.M).group() + '\n'
    chunks['session'] = session
    chunks['delegate'] = block(views, r'^@MainActor struct VemoLibraryDropDelegate\b')
    chunks['scroll_surface'] = block(views, r'^    final class Surface: NSView')
    template = Path(__file__).with_name('Harness.swift.template').read_text()
    code = template.replace('/* MODEL_METHODS */', ''.join(chunks[n] for n in METHODS + ['visibleJobs', 'sessionID'])).replace('/* SESSION */', session).replace('/* DELEGATE */', chunks['delegate']).replace('/* SCROLL */', '@MainActor enum ScrollFixtureHelper {\n' + chunks['scroll_surface'] + '}\n').replace('/* SCROLL_CHECKS */', """
        let unrelated = NSScrollView(frame: NSRect(x: 0, y: 0, width: 120, height: 120))
        unrelated.scrollerStyle = .legacy; unrelated.drawsBackground = true; unrelated.autohidesScrollers = false
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 120, height: 120))
        scroll.scrollerStyle = .legacy; scroll.drawsBackground = true; scroll.autohidesScrollers = false
        let document = NSView(frame: NSRect(x: 0, y: 0, width: 120, height: 400))
        let surface = ScrollFixtureHelper.Surface(frame: NSRect(x: 0, y: 0, width: 120, height: 400))
        document.addSubview(surface); scroll.documentView = document; surface.configure()
        check(surface.enclosingScrollView === scroll && scroll.scrollerStyle == .overlay && !scroll.drawsBackground && scroll.autohidesScrollers, "real NSScrollView named fixture receives scoped transparent overlay autohide appearance")
        check(surface.hitTest(NSPoint(x: 10, y: 10)) == nil, "decorative scroll fixture surface never intercepts hit testing")
        check(unrelated.scrollerStyle == .legacy && unrelated.drawsBackground && !unrelated.autohidesScrollers, "scroll helper leaves unrelated scroll fixture unchanged")
        let standalone = ScrollFixtureHelper.Surface(); standalone.configure()
        check(standalone.enclosingScrollView == nil && standalone.hitTest(.zero) == nil, "standalone decorative helper safely has no scroll ancestor")
    """)
    sources = []
    hashes = {}
    for name in CORE_NAMES:
        data = (CORE / name).read_bytes(); hashes[name] = sha(data)
        dest = out / name; dest.write_bytes(data); sources.append(dest)
    (out / 'tested-extracts.json').write_text(json.dumps(chunks, indent=2) + '\n')
    result, output = execute(out, 'current', code, sources)
    result['mutations'] = {}
    if args.mutation_check and result.get('run_exit') == 0:
        variants = [
            ('no-payload-equality', 'decoded == expected else', 'true else', 'wrong-drag loaded bytes never write or change view selection'),
            ('no-size-limit', 'data.count <= 4096, payload == expected', 'payload == expected', '4097-byte valid JSON rejected above loaded-data limit'),
            ('finish-newer-drag', '        guard payload == expected else { return false }', '        // negative control: unconditional finish', 'older verification and conditional finish cannot consume newer same-id drag'),
        ]
        for label, old, new, expected in variants:
            if code.count(old) != 1: raise ValueError('Mutation target changed: ' + label)
            negative, output = execute(out, label, code.replace(old, new), sources)
            negative['expected_failure'] = expected
            negative['verified'] = negative.get('run_exit') == 1 and 'FAIL: ' + expected in output
            result['mutations'][label] = negative
    result['extract_sha256'] = {n: sha(v) for n, v in chunks.items()}
    result['core_sha256'] = hashes
    result['template_sha256'] = sha(template)
    result['scope'] = 'real NSItemProvider + core/store + unmodified extracted session/delegate/AppModel methods; synthetic DropInfo/Binding; no actual AppKit drag bridge or GUI'
    result['negative_callback_window_ms'] = 75
    result['source_unchanged_after_run'] = all(sha((CORE / n).read_bytes()) == h for n, h in hashes.items()) and VIEWS.read_text() == views and MODEL.read_text() == model
    result['result'] = 'PASS' if result.get('run_exit') == 0 and result['source_unchanged_after_run'] and all(item['verified'] for item in result['mutations'].values()) else 'FAIL'
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    print('RESULT:', result['result'])
    return 0 if result['result'] == 'PASS' else 1
if __name__ == '__main__': sys.exit(main())
