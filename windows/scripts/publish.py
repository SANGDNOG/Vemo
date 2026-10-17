#!/usr/bin/env python3
"""Build reviewable portable Windows bundles without downloading models/runtimes."""
import argparse
import datetime
import hashlib
import json
import pathlib
import shutil
import struct
import subprocess
import sys
import zipfile


def pe_metadata(path):
    with path.open('rb') as stream:
        if stream.read(2) != b'MZ':
            return None
        stream.seek(60)
        offset = struct.unpack('<I', stream.read(4))[0]
        stream.seek(offset)
        if stream.read(4) != b'PE\0\0':
            raise ValueError(f'Invalid PE signature: {path}')
        machine = struct.unpack('<H', stream.read(2))[0]
        optional = offset + 24
        stream.seek(optional)
        magic = struct.unpack('<H', stream.read(2))[0]
        stream.seek(optional + (112 if magic == 0x20b else 96) + 32)
        certificate_offset, certificate_size = struct.unpack('<II', stream.read(8))
        return {'machine': hex(machine), 'authenticode_directory_present': bool(certificate_offset and certificate_size)}


def digest(path):
    with path.open('rb') as stream:
        sha = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            sha.update(chunk)
        return sha.hexdigest()


def main():
    repo = pathlib.Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=pathlib.Path, default=repo / 'build/toolchains/dotnet/dotnet')
    parser.add_argument('--output', type=pathlib.Path, required=True, help='New or empty directory; existing publications are preserved.')
    parser.add_argument('--rids', nargs='+', choices=['win-x64', 'win-arm64'], default=['win-x64', 'win-arm64'])
    args = parser.parse_args()
    sdk = args.dotnet.expanduser().resolve(strict=True)
    output = args.output.expanduser().resolve()
    # Keep generated publications out of source/config paths and reject symlink destinations.
    if not output.is_relative_to(repo / 'windows/artifacts'):
        parser.error('--output must be under windows/artifacts')
    if output.exists() and any(output.iterdir()):
        parser.error('--output must be new or empty; use a new publication directory')
    output.mkdir(parents=True, exist_ok=True)
    logs = output / 'logs'
    logs.mkdir()

    def run(label, arguments):
        print(label, flush=True)
        with (logs / (label + '.log')).open('w', encoding='utf-8') as log:
            result = subprocess.run([str(sdk), *arguments], cwd=repo, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            print((logs / (label + '.log')).read_text(encoding='utf-8'), file=sys.stderr)
            raise RuntimeError(f'{label} failed ({result.returncode}); incomplete output is retained for diagnosis')

    run('tests', ['test', 'windows/Vemo.Core.Tests', '-c', 'Release', '--nologo'])
    run('provision-tests', ['test', 'windows/Vemo.Provision.Tests', '-c', 'Release', '--nologo'])
    bundles = {}
    for rid in dict.fromkeys(args.rids):
        for project, name in [('Vemo.App', rid), ('Vemo.Validation', 'validation-' + rid), ('Vemo.Provision', 'provision-' + rid)]:
            dest = output / name
            run('publish-' + name, ['publish', 'windows/' + project, '-c', 'Release', '-r', rid, '--self-contained', 'true', '-o', str(dest), '--nologo'])
            exe = dest / (project + '.exe')
            pe = pe_metadata(exe)
            expected = '0x8664' if rid == 'win-x64' else '0xaa64'
            if not pe or pe['machine'] != expected:
                raise RuntimeError(f'Unexpected executable architecture: {exe}: {pe}')
            runtime = json.loads((dest / (project + '.runtimeconfig.json')).read_text())
            if not runtime['runtimeOptions'].get('includedFrameworks') or not (dest / 'coreclr.dll').is_file():
                raise RuntimeError(f'Publish is not self-contained: {dest}')
            entries = []
            for path in sorted(dest.rglob('*')):
                if path.is_symlink():
                    raise RuntimeError(f'Unexpected link in publication: {path}')
                if path.is_file():
                    entries.append({'path': path.relative_to(dest).as_posix(), 'bytes': path.stat().st_size, 'sha256': digest(path)})
            archive = output / (name + '.zip')
            with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as zipped:
                for entry in entries:
                    zipped.write(dest / entry['path'], arcname=name + '/' + entry['path'])
            bundles[name] = {'rid': rid, 'exe': exe.name, 'pe': pe, 'self_contained': True, 'files': entries, 'archive': archive.name, 'archive_sha256': digest(archive)}
    for filename in ['README.md', 'model-catalogue.json', 'runtime-catalogue.json', 'THIRD-PARTY-NOTICES.md']:
        shutil.copyfile(repo / 'windows' / filename, output / filename)
    shutil.copyfile(repo / 'windows/scripts/run-acceptance.ps1', output / 'run-acceptance.ps1')
    manifest = {'schema_version': 1, 'created_at_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'actual_windows_execution': False, 'actual_model_inference': False, 'bundles': bundles}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'Publication prepared: {output}\nManifest: {output / "manifest.json"}\nWindows execution, inference quality and signing remain separate checks.')


if __name__ == '__main__':
    main()
