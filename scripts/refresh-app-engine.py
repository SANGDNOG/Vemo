#!/usr/bin/env python3
"""Refresh only the engine in a staged runtime, without network/dependency installs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

PROJECT = Path(__file__).resolve().parents[1]


def source_manifest(package):
    return {path.relative_to(package).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(package.rglob('*.py'))}


def refresh(project, runtime, build_python):
    project, runtime = project.resolve(), runtime.resolve()
    python = runtime / 'bin/python3'
    environment = os.environ.copy()
    for key in ('PYTHONHOME', 'PYTHONPATH', 'VIRTUAL_ENV'):
        environment.pop(key, None)
    environment['PYTHONDONTWRITEBYTECODE'] = '1'
    environment['PIP_NO_INDEX'] = '1'
    environment['PIP_DISABLE_PIP_VERSION_CHECK'] = '1'
    metadata_path = runtime / 'vemo-runtime.json'
    metadata = json.loads(metadata_path.read_text())
    expected = source_manifest(project / 'src/voice_memo_transcriber')
    with tempfile.TemporaryDirectory(prefix='vemo-engine-') as temporary:
        snapshot = Path(temporary)
        # Never reuse setuptools' build/lib, which may contain deleted/old modules.
        shutil.copytree(project / 'src', snapshot / 'src',
                        ignore=shutil.ignore_patterns('__pycache__', '*.egg-info'))
        for name in ('pyproject.toml', 'LICENSE'):
            shutil.copy2(project / name, snapshot / name)
        wheels = snapshot / 'wheels'
        wheels.mkdir()
        subprocess.run([str(build_python), '-c',
                        'import setuptools, wheel; from importlib.metadata import version; '
                        'assert int(version("setuptools").split(".")[0]) >= 75, "setuptools>=75 required"; '
                        'from setuptools.build_meta import build_wheel; build_wheel("wheels")'],
                       cwd=snapshot, env=environment, check=True)
        wheel, = wheels.glob('*.whl')
        with zipfile.ZipFile(wheel) as archive:
            wheel_manifest = {name.removeprefix('voice_memo_transcriber/'): hashlib.sha256(archive.read(name)).hexdigest()
                              for name in archive.namelist()
                              if name.startswith('voice_memo_transcriber/') and name.endswith('.py')}
        if wheel_manifest != expected:
            raise RuntimeError('engine wheel does not match current checkout sources')
        site = Path(subprocess.check_output(
            [str(python), '-I', '-c', 'import sysconfig; print(sysconfig.get_path("purelib"))'],
            env=environment, text=True).strip()).resolve()
        if not site.is_relative_to(runtime):
            raise RuntimeError('runtime site-packages escapes staged runtime')
        # Remove old versions and deleted modules, without touching dependencies.
        for path in [site / 'voice_memo_transcriber', *site.glob('voice_memo_transcriber-*.dist-info')]:
            if path.exists():
                shutil.rmtree(path)
        subprocess.run([str(python), '-I', '-m', 'pip', 'install', '--no-index', '--no-deps',
                        '--no-compile', '--target', str(site), str(wheel)],
                       env=environment, check=True)
        if source_manifest(site / 'voice_memo_transcriber') != expected:
            raise RuntimeError('installed engine does not match current checkout sources')
        if source_manifest(project / 'src/voice_memo_transcriber') != expected:
            raise RuntimeError('checkout sources changed during engine refresh; rebuild')
        # Do not ship the temporary build path written by pip.
        for info in site.glob('voice_memo_transcriber-*.dist-info'):
            (info / 'direct_url.json').unlink(missing_ok=True)
        metadata['engine_wheel_sha256'] = hashlib.sha256(wheel.read_bytes()).hexdigest()
        metadata['engine_source_sha256'] = expected
        metadata_path.write_text(json.dumps(metadata, indent=2) + '\n')
    print('Staged engine matches current checkout (%d Python sources).' % len(expected))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('runtime', type=Path)
    parser.add_argument('--build-python', default=os.environ.get('VEMO_BUILD_PYTHON', 'python3'))
    args = parser.parse_args()
    refresh(PROJECT, args.runtime, args.build_python)
