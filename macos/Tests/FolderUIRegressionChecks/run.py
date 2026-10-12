#!/usr/bin/env python3
"""Bounded offscreen NSHostingView checks; no live app operation or synthesized input."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / 'macos/Sources/VoiceMemoTranscriber/FolderViews.swift'

def block(source, pattern):
    match = re.search(pattern, source, re.M)
    if not match:
        raise ValueError('Missing declaration: ' + pattern)
    opening = source.index('{', match.end())
    depth = 0
    for end in range(opening, len(source)):
        if source[end] == '{':
            depth += 1
        elif source[end] == '}':
            depth -= 1
            if depth == 0:
                return source[match.start():end + 1] + '\n'
    raise ValueError('Unclosed declaration: ' + pattern)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--always-scrollbars', action='store_true', help='process-only AppleShowScrollBars=Always; no global defaults edits')
    parser.add_argument('--sheet-lifecycle', action='store_true', help='owned offscreen NavigationSplitView windows with actual SwiftUI sheet lifecycle')
    parser.add_argument('--retain-sheet-control', action='store_true', help='generated lifecycle dismiss closure is no-op; expected residual-sheet failures')
    parser.add_argument('--negative-control', action='store_true', help='generated helper configure no-op; never changes product source')
    args = parser.parse_args()
    out = args.output_dir.resolve()
    out.mkdir(parents=True, exist_ok=True)
    source = SOURCE.read_text()
    helper = block(source, r'^struct VemoScrollAppearance: NSViewRepresentable\b')
    original_helper = helper
    if args.negative_control:
        configure = block(helper, r'^        func configure\(\)')
        helper = helper.replace(configure, '        func configure() {}\n')
    template_name = 'SheetLifecycle.swift.template' if args.sheet_lifecycle else 'Harness.swift.template'
    template = Path(__file__).with_name(template_name).read_text()
    if args.retain_sheet_control:
        if not args.sheet_lifecycle:
            parser.error('--retain-sheet-control requires --sheet-lifecycle')
        target = 'state.dismiss = { editor = false }'
        if template.count(target) != 1:
            raise ValueError('Lifecycle dismiss control target changed')
        template = template.replace(target, 'state.dismiss = {}')
    generated = out / 'HostingChecks.swift'
    generated.write_text(template.replace('/* SCROLL_HELPER */', helper))
    binary = out / 'hosting-checks'
    command = ['swiftc', '-swift-version', '6', '-parse-as-library', str(generated), '-o', str(binary)]
    build = subprocess.run(command, text=True, capture_output=True, timeout=90, cwd=ROOT)
    (out / 'build.log').write_text(build.stdout + build.stderr)
    result = {'compile_exit': build.returncode, 'compile_command': command,
              'helper_sha256': hashlib.sha256(helper.encode()).hexdigest(),
              'original_helper_sha256': hashlib.sha256(original_helper.encode()).hexdigest(),
              'negative_control': args.negative_control,
              'sheet_lifecycle': args.sheet_lifecycle,
              'retain_sheet_control': args.retain_sheet_control,
              'template_sha256': hashlib.sha256(template.encode()).hexdigest(),
              'source_sha256': hashlib.sha256(source.encode()).hexdigest(),
              'scope': ('owned offscreen ordered NSWindows; actual SwiftUI NavigationSplitView, animated tree and sheet; direct state changes, native hit-test queries and programmatic responder requests; no synthesized input, production AppModel, actual drag or live apps exercised' if args.sheet_lifecycle else 'offscreen NSWindows and real NSHostingView SwiftUI ScrollView/List; no production AppModel, sheet lifecycle, drag events, rendered GUI, or live apps exercised')}
    if build.returncode == 0:
        try:
            run_command = [str(binary)] + (['-AppleShowScrollBars', 'Always'] if args.always_scrollbars else [])
            result['run_command'] = run_command
            run = subprocess.run(run_command, text=True, capture_output=True, timeout=(25 if args.sheet_lifecycle else 15), cwd=ROOT)
            output = run.stdout + run.stderr
            result['run_exit'] = run.returncode
            counts = re.search(r'RESULT: (\d+) assertions passed; (\d+) failed', output)
            if counts:
                result.update(passed=int(counts[1]), failed=int(counts[2]))
        except subprocess.TimeoutExpired as error:
            output = (error.stdout or b'').decode() + (error.stderr or b'').decode()
            result['timeout_seconds'] = 25 if args.sheet_lifecycle else 15
        (out / 'hosting.log').write_text(output)
        print(output, end='')
    else:
        print(build.stdout + build.stderr, end='')
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    return 0 if result.get('run_exit') == 0 else 1

if __name__ == '__main__':
    raise SystemExit(main())
