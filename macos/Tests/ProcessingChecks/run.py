#!/usr/bin/env python3
"""Compile real AppModel processing methods against a synthetic actor transport.

No SwiftPM, engine process, inference, downloads, or application state access.
Use --mutation-check to prove the old paused-notes behavior fails these checks.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / 'macos/Sources/VoiceMemoTranscriber/AppModel.swift'
CORE = ROOT / 'macos/Sources/NativeCore'
METHODS = ('command', 'manualProcessingTitle', 'processingCommand', 'processRecording', 'generateNotes')


def extract(source, name, declaration='func'):
    # All tested declarations are AppModel members at four-space indentation.
    start = re.search(r'^    (?:private )?' + declaration + r' ' + re.escape(name) + r'\b', source, re.M)
    if not start:
        raise ValueError('Missing source declaration: ' + name)
    opening = source.index('{', start.end())
    depth = 0
    for end in range(opening, len(source)):
        if source[end] == '{':
            depth += 1
        elif source[end] == '}':
            depth -= 1
            if depth == 0:
                return source[start.start():end + 1] + '\n'
    raise ValueError('Unclosed source declaration: ' + name)


def execute(folder, label, harness):
    swift = folder / (label + '.swift')
    binary = folder / label
    swift.write_text(harness)
    compile_command = ['swiftc', '-swift-version', '6', '-parse-as-library', str(CORE / 'WireTypes.swift'), str(CORE / 'Validation.swift'), str(swift), '-o', str(binary)]
    built = subprocess.run(compile_command, cwd=ROOT, text=True, capture_output=True, timeout=60)
    (folder / (label + '-build.log')).write_text(built.stdout + built.stderr)
    if built.returncode:
        print(built.stdout + built.stderr, file=sys.stderr)
        raise RuntimeError(label + ' failed to compile')
    tested = subprocess.run([str(binary)], cwd=ROOT, text=True, capture_output=True, timeout=30)
    (folder / (label + '.log')).write_text(tested.stdout + tested.stderr)
    print(tested.stdout, end='')
    if tested.stderr:
        print(tested.stderr, file=sys.stderr, end='')
    counts = re.search(r'RESULT: (\d+) assertions passed; (\d+) failed', tested.stdout)
    if counts is None:
        raise RuntimeError(label + ' produced no test summary')
    return {'compile_exit': built.returncode, 'run_exit': tested.returncode, 'assertions_passed': int(counts[1]), 'assertions_failed': int(counts[2]), 'compile_command': compile_command, 'run_command': [str(binary)]}, tested.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, help='retain generated source, logs, and evidence; defaults to a temporary directory')
    parser.add_argument('--mutation-check', action='store_true')
    args = parser.parse_args()
    source = SOURCE.read_text()
    exact = {name: extract(source, name) for name in METHODS}
    exact['overallStatus'] = extract(source, 'overallStatus', 'var')
    eligibility = re.search(r'^    var canGenerateNotes:.*$', source, re.M)
    if not eligibility:
        raise ValueError('Missing canGenerateNotes')
    copied = ''.join(exact.values()) + eligibility.group() + '\n'
    template = Path(__file__).with_name('Harness.swift.template').read_text()
    assert template.count('/* APPMODEL_METHODS */') == 1
    temporary = None
    if args.output_dir:
        folder = args.output_dir.resolve()
        folder.mkdir(parents=True, exist_ok=True)
    else:
        temporary = tempfile.TemporaryDirectory(prefix='Vemo-ProcessingChecks-')
        folder = Path(temporary.name)
    try:
        manifest = {'appmodel_sha256': hashlib.sha256(source.encode()).hexdigest(), 'method_sha256': {k: hashlib.sha256(v.encode()).hexdigest() for k, v in exact.items()}, 'core_sha256': {name: hashlib.sha256((CORE / name).read_bytes()).hexdigest() for name in ['WireTypes.swift', 'Validation.swift']}}
        (folder / 'tested-methods.txt').write_text(copied)
        current, output = execute(folder, 'current', template.replace('/* APPMODEL_METHODS */', copied))
        manifest['current'] = current
        if current['run_exit'] != 0:
            (folder / 'result.json').write_text(json.dumps(manifest, indent=2) + '\n')
            return 1
        if args.mutation_check:
            old = '    func generateNotes() { guard let job = selected, canGenerateNotes else { return }; command("generate_notes",["job_id":.string(job.id)]) }\n'
            mutation = copied.replace(exact['generateNotes'], old)
            assert mutation != copied
            mutated, output = execute(folder, 'old-notes-mutation', template.replace('/* APPMODEL_METHODS */', mutation))
            manifest['old_notes_mutation'] = mutated
            if mutated['run_exit'] != 1 or 'FAIL: paused notes must send resume before generate_notes' not in output:
                (folder / 'result.json').write_text(json.dumps(manifest, indent=2) + '\n')
                print('FAIL: old generateNotes mutation did not reproduce the intended regression', file=sys.stderr)
                return 1
            print('PASS: old generateNotes mutation reproduced missing resume')
        # Only extracted methods need remain stable; unrelated AppModel edits may coexist.
        latest = SOURCE.read_text()
        manifest['tested_methods_unchanged_after_run'] = all(extract(latest, name, 'var' if name == 'overallStatus' else 'func') == text for name, text in exact.items()) and re.search(r'^    var canGenerateNotes:.*$', latest, re.M).group() == eligibility.group()
        manifest['result'] = 'PASS' if manifest['tested_methods_unchanged_after_run'] else 'SOURCE_CHANGED'
        (folder / 'result.json').write_text(json.dumps(manifest, indent=2) + '\n')
        print('RESULT:', manifest['result'])
        return 0 if manifest['tested_methods_unchanged_after_run'] else 1
    finally:
        if temporary:
            temporary.cleanup()


if __name__ == '__main__':
    sys.exit(main())
