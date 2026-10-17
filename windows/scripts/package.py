#!/usr/bin/env python3
"""Package the frozen 0.1.0 Windows preview; no builds, downloads or inference.

Run from any directory with Python 3.9+: python3 windows/scripts/package.py
Use --verify-only to check inputs without replacing distributions. Root may rerun
after updating the two documentation files. Final archive hashes are written to
windows/artifacts/license-evidence/{distribution-manifest.json,SHA256SUMS.txt}.
"""
import argparse
import hashlib
import json
import os
import pathlib
import posixpath
import re
import shutil
import stat
import sys
import tempfile
import urllib.parse
import zipfile

PUBLICATION = 'windows/artifacts/publication-20261018-native'
PUBLICATION_SHA256 = '5941f231e20e820ba3f0499e029dadf92a093a559639c58567867b9687dfa29f'
ARCHES = ('x64', 'arm64')
BUNDLES = {prefix + 'win-' + arch for arch in ARCHES
           for prefix in ('', 'provision-', 'validation-')}


def load_json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate JSON key: ' + key)
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=unique)


def digest(stream):
    sha = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b''):
        sha.update(block)
    return sha.hexdigest()


def file_digest(path):
    with path.open('rb') as stream:
        return digest(stream)


def record(data):
    return {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def validate_paths(names):
    """Reject ambiguous Windows extraction paths, including directory aliases."""
    seen, nodes = set(), {}
    reserved = {'con', 'prn', 'aux', 'nul', 'conin$', 'conout$'}
    reserved.update(prefix + n for prefix in ('com', 'lpt') for n in '123456789¹²³')
    for name in names:
        if not isinstance(name, str) or not name:
            raise ValueError('Empty/non-string path')
        parts = name.split('/')
        for part in parts:
            if (not part or part in ('.', '..') or part.endswith((' ', '.'))
                    or any(c in '<>:"\\|?*' or ord(c) < 32 for c in part)
                    or part.split('.')[0].casefold() in reserved):
                raise ValueError('Unsafe Windows path: ' + repr(name))
        key = name.casefold()
        if key in seen:
            raise ValueError('Duplicate Windows path: ' + name)
        seen.add(key)
        for i in range(1, len(parts) + 1):
            prefix = '/'.join(parts[:i])
            kind = 'file' if i == len(parts) else 'directory'
            prior = nodes.setdefault(prefix.casefold(), (prefix, kind))
            if prior != (prefix, kind):
                raise ValueError('Case alias or file/directory collision: ' + name)


def check_node(path, directory=False):
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
        raise ValueError('Link/reparse point rejected: ' + str(path))
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)):
        raise ValueError('Unexpected filesystem entry: ' + str(path))


def safe_file(root, relative):
    validate_paths([relative])
    check_node(root, directory=True)
    path = root
    parts = relative.split('/')
    for i, part in enumerate(parts):
        path = path / part
        check_node(path, directory=i < len(parts) - 1)
    return path


def tree_files(root):
    check_node(root, directory=True)
    found = []
    for parent, dirs, files in os.walk(root, followlinks=False):
        for name in dirs:
            check_node(pathlib.Path(parent) / name, directory=True)
        for name in files:
            path = pathlib.Path(parent) / name
            check_node(path)
            found.append(path.relative_to(root).as_posix())
    validate_paths(found)
    return set(found)


def verify_tree(root, entries):
    validate_paths([entry['path'] for entry in entries])
    if tree_files(root) != {entry['path'] for entry in entries}:
        raise ValueError('Unexpected/missing files in ' + str(root))
    for entry in entries:
        path = safe_file(root, entry['path'])
        if path.stat().st_size != entry['bytes'] or file_digest(path) != entry['sha256']:
            raise ValueError('File size/SHA-256 mismatch: ' + str(path))


def verify_archive(path, expected):
    validate_paths(expected)
    with zipfile.ZipFile(path) as archive:
        infos = archive.infolist()
        validate_paths([info.orig_filename for info in infos])
        if {info.orig_filename for info in infos} != set(expected):
            raise ValueError('Unexpected/missing ZIP entries: ' + str(path))
        for info in infos:
            mode = stat.S_IFMT(info.external_attr >> 16)
            if (info.is_dir() or mode not in (0, stat.S_IFREG)
                    or info.external_attr & 0x400 or info.flag_bits & 1):
                raise ValueError('Non-regular/encrypted ZIP entry: ' + info.orig_filename)
            entry = expected[info.orig_filename]
            if info.file_size != entry['bytes']:
                raise ValueError('ZIP size mismatch: ' + info.orig_filename)
            with archive.open(info) as stream:
                if digest(stream) != entry['sha256']:
                    raise ValueError('ZIP SHA-256 mismatch: ' + info.orig_filename)


def verify_markdown_links(path):
    """Check inline/reference file targets in every Markdown member, not URL/anchor availability."""
    counts = {'markdown_files': 0, 'relative_links': 0}
    missing = []
    destination = r'(?:<([^>\n]+)>|([^\s)]+))'
    patterns = (r'\[[^\]\n]*\]\(\s*' + destination,
                r'(?m)^[ \t]{0,3}\[[^\]\n]+\]:[ \t]*' + destination)
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        for name in sorted(names):
            if not name.lower().endswith('.md'):
                continue
            counts['markdown_files'] += 1
            text = archive.read(name).decode('utf-8-sig')
            for pattern in patterns:
                for match in re.finditer(pattern, text):
                    target = match.group(1) or match.group(2)
                    url = urllib.parse.urlsplit(target)
                    if url.scheme or url.netloc or not url.path:
                        continue
                    counts['relative_links'] += 1
                    local = urllib.parse.unquote(url.path)
                    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(name), local))
                    if (local.startswith('/') or resolved == '..' or resolved.startswith('../')
                            or (resolved not in names and not any(n.startswith(resolved.rstrip('/') + '/') for n in names))):
                        missing.append(name + ' -> ' + target)
    if missing:
        raise ValueError('Missing/out-of-package Markdown targets:\n' + '\n'.join(missing))
    return counts


def verify_publication(repo):
    manifest_file = safe_file(repo, PUBLICATION + '/manifest.json')
    raw = manifest_file.read_bytes()
    if record(raw)['sha256'] != PUBLICATION_SHA256:
        raise ValueError('Frozen publication manifest SHA-256 changed; do not package')
    manifest = load_json(raw)
    if manifest['schema_version'] != 1 or set(manifest['bundles']) != BUNDLES:
        raise ValueError('Unexpected publication structure')
    publication = manifest_file.parent
    for name, bundle in manifest['bundles'].items():
        print('Verify publication ' + name, flush=True)
        rid = 'win-x64' if name.endswith('win-x64') else 'win-arm64'
        if (bundle['rid'] != rid or not bundle['self_contained']
                or bundle['pe']['machine'] != ('0x8664' if rid == 'win-x64' else '0xaa64')):
            raise ValueError('Architecture/self-contained metadata mismatch: ' + name)
        verify_tree(publication / name, bundle['files'])
        if bundle['archive'] != name + '.zip':
            raise ValueError('Unexpected publication archive name')
        archive = safe_file(publication, bundle['archive'])
        if file_digest(archive) != bundle['archive_sha256']:
            raise ValueError('Publication ZIP SHA-256 mismatch: ' + name)
        verify_archive(archive, {name + '/' + e['path']: e for e in bundle['files']})
    return publication, manifest, raw


def verify_licenses(repo, publication):
    index_file = safe_file(repo, 'windows/licenses/index.json')
    index = load_json(index_file.read_bytes())
    if index['schema_version'] != 1 or index['publication_manifest_sha256'] != PUBLICATION_SHA256:
        raise ValueError('License inventory is for a different publication')
    verify_tree(index_file.parent, index['files'] + [{'path': 'index.json', **record(index_file.read_bytes())}])
    required = {'xunit.runner.visualstudio/3.1.4'}
    for deps in publication.glob('*/*.deps.json'):
        for name, item in load_json(deps.read_bytes())['libraries'].items():
            if item['type'] == 'package':
                required.add(item['path'])
            elif item['type'] == 'runtimepack':
                required.add(name.removeprefix('runtimepack.').lower())
    packages = {p['package']: p for p in index['packages']}
    if set(packages) != required or len(packages) != len(index['packages']):
        raise ValueError('License inventory does not cover the exact dependency set')
    files = {f['path'] for f in index['files']}
    for package in packages.values():
        if not package['license_files'] or not set(package['license_files']) <= files:
            raise ValueError('Missing dependency license: ' + package['package'])
    for item in index['additional_asset_evidence']:
        path = safe_file(publication, item['published_path'])
        if file_digest(path) != item['sha256']:
            raise ValueError('Additional bundled dependency hash mismatch')
    return index


def quickstart(arch):
    if arch == 'x64':
        provision = r'.\windows\provision-win-x64\Vemo.Provision.exe --destination C:\VemoModels --arch x64 --whisper turbo --notes 9b --download-ffmpeg'
        extra = '일반 Intel/AMD Windows PC용입니다. FFmpeg도 검증된 x64 LGPL 버전으로 받습니다.'
    else:
        provision = r'.\windows\provision-win-arm64\Vemo.Provision.exe --destination C:\VemoModels --arch arm64 --whisper turbo --notes 9b --ffmpeg C:\Tools\ffmpeg.exe'
        extra = ('Windows ARM64 PC용입니다. 위 FFmpeg 경로는 직접 준비한 네이티브 ARM64 실행 파일로 바꾸세요. '
                 '검증된 ARM64 FFmpeg 다운로드 핀이 없어 자동 설치나 x64 에뮬레이션을 선택하지 않습니다. '
                 '`--wav-only`는 별도 음성 테스트용이며 앱 전체 처리에는 FFmpeg가 필요합니다.')
    return f'''# Vemo Windows 0.1.0 기능 프리뷰 — {arch}

ZIP 전체를 쓰기 가능한 로컬 폴더에 풀어 주세요. 파일을 ZIP 안에서 바로 실행하거나 DLL을 따로 옮기지 마세요.
이 배포에는 앱·CPU 설치 도구·검증 도구와 .NET 런타임이 들어 있습니다. .NET 별도 설치는 필요하지 않습니다.
모델 가중치·음성/노트 추론 런타임·개인 인증정보는 포함하지 않습니다. 서명되지 않은 포터블 프리뷰입니다.

## 처음 사용하기

먼저 [Microsoft Visual C++ v14 재배포 패키지](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 PC 아키텍처에 맞게 설치하세요. 별도 음성·노트 런타임이 필요로 하며 Vemo의 .NET 포함과 별개입니다. 설치 파일은 Microsoft 공식 문서에서 받습니다.

1. 압축을 푼 폴더에서 PowerShell을 열고 아래 명령으로 CPU 기본 모델(turbo + 9B)을 받으세요. 여러 GB의 다운로드와 충분한 메모리/저장 공간이 필요합니다. 새 설치 폴더를 사용하세요.

```powershell
{provision}
```

{extra}

2. `windows/win-{arch}/Vemo.App.exe`를 실행하세요.
3. **Settings → Import preset settings…**에서 `C:\\VemoModels\\settings.json`을 선택하고 ZIP 저장 폴더를 지정한 뒤 **Save settings**를 누르세요.
4. 녹음이 끝난 파일을 가져오세요. 처리가 완료되면 원본 녹음·전사·노트 세 파일을 담은 ZIP이 저장됩니다. 원본 파일은 유지합니다.

작은 음성 모델은 `--whisper small`, 큰 노트 모델은 `--notes 27b`로 선택할 수 있습니다. 27B에는 더 많은 메모리와 시간이 필요합니다.
기존 설치 경로 선택, 폴더 이동, 휴지통, 재시도 및 설정은 [Windows 사용 안내](windows/README.md)를 읽어 주세요.
실제 검증 범위와 알려진 제한은 [기능 프리뷰 검증 기록](docs/FUNCTIONAL_PREVIEW.md)에 있습니다. 이 패키징 작업 자체는 Windows 실행·GUI·CUDA·모델 품질을 검증하지 않습니다.

## 선택 사항: 네이티브 코어 검증

압축을 푼 폴더에서 실행합니다. 모델을 다운로드하거나 GUI를 열지 않습니다. 일부 파일시스템 테스트에는 심볼릭 링크 생성 권한이 필요합니다.

```powershell
powershell -NoProfile -File .\\windows\\run-acceptance.ps1 -BundleRoot .\\windows -Runtime win-{arch}
```

`windows/manifest.json`은 변경하지 않은 원본 게시 매니페스트이며 양쪽 아키텍처를 모두 기재합니다. 이 ZIP에는 {arch}용 세 묶음만 들어 있습니다.
`PACKAGE-MANIFEST.json`은 이 ZIP의 실제 파일 목록/크기/SHA-256입니다. `SHA256SUMS.txt`는 그 매니페스트도 포함하며 자기 자신의 해시는 제외합니다.
해시는 파일 손상 검사용이며 코드 서명을 대신하지 않습니다. [재배포 의존성 라이선스](windows/THIRD-PARTY-NOTICES.md)도 함께 보관하세요.
'''.encode('utf-8')


def snapshots(repo, publication, index):
    inputs = {
        'windows/README.md': 'windows/README.md',
        'docs/FUNCTIONAL_PREVIEW.md': 'docs/FUNCTIONAL_PREVIEW.md',
        'windows/THIRD-PARTY-NOTICES.md': 'windows/THIRD-PARTY-NOTICES.md',
        'windows/model-catalogue.json': 'windows/model-catalogue.json',
        'windows/runtime-catalogue.json': 'windows/runtime-catalogue.json',
        'windows/run-acceptance.ps1': PUBLICATION + '/run-acceptance.ps1',
        'windows/manifest.json': PUBLICATION + '/manifest.json',
        'windows/licenses/index.json': 'windows/licenses/index.json',
    }
    for item in index['files']:
        name = 'windows/licenses/' + item['path']
        inputs[name] = name
    result = {target: safe_file(repo, source).read_bytes() for target, source in inputs.items()}
    for name in ('model-catalogue.json', 'runtime-catalogue.json'):
        if result['windows/' + name] != safe_file(publication, name).read_bytes():
            raise ValueError('Current catalogue differs from frozen publication: ' + name)
    for arch in ARCHES:
        if result['windows/runtime-catalogue.json'] != safe_file(publication, 'provision-win-' + arch + '/runtime-catalogue.json').read_bytes():
            raise ValueError('Current catalogue differs from frozen provisioner: ' + arch)
    if result['windows/run-acceptance.ps1'] != safe_file(repo, 'windows/scripts/run-acceptance.ps1').read_bytes():
        raise ValueError('Current acceptance wrapper differs from frozen publication')
    if record(result['windows/manifest.json'])['sha256'] != PUBLICATION_SHA256:
        raise ValueError('Publication changed during packaging')
    for entry in index['files']:
        if record(result['windows/licenses/' + entry['path']]) != {k: entry[k] for k in ('bytes', 'sha256')}:
            raise ValueError('License changed during packaging')
    return result


def write_distribution(path, arch, publication, manifest, shared):
    payload = dict(shared)
    payload['README.md'] = quickstart(arch)
    # The frozen provisioner notice uses licenses/... relative to its own folder.
    # Supplement that folder with exact shared copies; never rewrite published bytes.
    for name, data in shared.items():
        if name.startswith('windows/licenses/'):
            target = 'windows/provision-win-' + arch + '/' + name.removeprefix('windows/')
            if target in payload:
                raise ValueError('Duplicate package destination: ' + target)
            payload[target] = data
    expected = {name: record(data) for name, data in payload.items()}
    bundles = [prefix + 'win-' + arch for prefix in ('', 'provision-', 'validation-')]
    for name in bundles:
        for entry in manifest['bundles'][name]['files']:
            target = 'windows/' + name + '/' + entry['path']
            if target in payload:
                raise ValueError('Duplicate package destination: ' + target)
            payload[target] = publication / name / entry['path']
            expected[target] = {k: entry[k] for k in ('bytes', 'sha256')}
    validate_paths(payload)
    package_manifest = json_bytes({
        'schema_version': 1, 'name': 'Vemo-Windows', 'version': '0.1.0-preview',
        'architecture': arch, 'publication_manifest_sha256': PUBLICATION_SHA256,
        'bundles': bundles, 'scope': 'File integrity only; no new runtime or model tests.',
        'files': [{'path': name, **expected[name]} for name in sorted(expected)],
    })
    payload['PACKAGE-MANIFEST.json'] = package_manifest
    expected['PACKAGE-MANIFEST.json'] = record(package_manifest)
    checksums = ''.join(expected[n]['sha256'] + '  ' + n + '\n' for n in sorted(expected)).encode('utf-8')
    payload['SHA256SUMS.txt'] = checksums
    expected['SHA256SUMS.txt'] = record(checksums)
    with zipfile.ZipFile(path, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name in sorted(payload):
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 18, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            data = payload[name]
            with archive.open(info, 'w', force_zip64=True) as dest:
                if isinstance(data, bytes):
                    dest.write(data)
                else:
                    source = safe_file(publication, data.relative_to(publication).as_posix())
                    with source.open('rb') as stream:
                        shutil.copyfileobj(stream, dest, 1024 * 1024)
    verify_archive(path, expected)
    markdown = verify_markdown_links(path)
    return {'path': 'dist/' + path.name, 'architecture': arch, 'bytes': path.stat().st_size,
            'sha256': file_digest(path), 'file_count': len(expected),
            'markdown_links': markdown,
            'package_manifest_sha256': record(package_manifest)['sha256']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    repo = pathlib.Path(__file__).resolve().parents[2]
    publication, manifest, _ = verify_publication(repo)
    index = verify_licenses(repo, publication)
    shared = snapshots(repo, publication, index)
    print('Verified all six bundles/archives and licenses for %d dependencies.' % len(index['packages']), flush=True)
    if args.verify_only:
        return
    evidence = repo / 'windows/artifacts/license-evidence'
    evidence.mkdir(exist_ok=True)
    check_node(evidence, directory=True)
    dist = repo / 'dist'
    dist.mkdir(exist_ok=True)
    check_node(dist, directory=True)
    outputs = []
    with tempfile.TemporaryDirectory(prefix='package-', dir=evidence) as temp:
        staging = pathlib.Path(temp)
        for arch in ARCHES:
            name = 'Vemo-Windows-0.1.0-preview-' + arch + '.zip'
            destination = dist / name
            if destination.exists() or destination.is_symlink():
                check_node(destination)
            print('Package ' + name, flush=True)
            outputs.append(write_distribution(staging / name, arch, publication, manifest, shared))
        for output in outputs:
            name = pathlib.PurePosixPath(output['path']).name
            os.replace(staging / name, dist / name)
        report = {'schema_version': 1, 'publication_manifest_sha256': PUBLICATION_SHA256,
                  'license_index_sha256': record(shared['windows/licenses/index.json'])['sha256'],
                  'source_script_sha256': file_digest(pathlib.Path(__file__).resolve()),
                  'verification': {'publication_file_hashes': True, 'publication_archive_hashes': True,
                                   'archive_member_hashes_and_windows_paths': True,
                                   'markdown_relative_targets': True,
                                   'license_coverage': True, 'new_native_execution': False},
                  'documentation': {name: record(shared[name]) for name in ('windows/README.md', 'docs/FUNCTIONAL_PREVIEW.md')},
                  'distributions': outputs}
        for name, data in [('distribution-manifest.json', json_bytes(report)),
                           ('SHA256SUMS.txt', ''.join(o['sha256'] + '  ' + o['path'] + '\n' for o in outputs).encode('utf-8'))]:
            target = evidence / name
            if target.exists() or target.is_symlink():
                check_node(target)
            (staging / name).write_bytes(data)
            os.replace(staging / name, target)
    for output in outputs:
        print(output['sha256'] + '  ' + output['path'])


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as error:
        print('Packaging failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
