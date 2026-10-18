import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import venv

PROJECT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('refresh_app_engine', PROJECT / 'scripts/refresh-app-engine.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class AppEnginePackagingTests(unittest.TestCase):
    def test_every_non_demo_build_refreshes_before_signing(self):
        script = (PROJECT / 'scripts/build-app.sh').read_text()
        copy = script.index('    ditto "$runtime_path"')
        refresh = script.index('    python3 "$project_root/scripts/refresh-app-engine.py"')
        self.assertLess(copy, refresh)
        self.assertLess(refresh, script.index('\nfi', copy))
        self.assertLess(refresh, script.index('python3 "$project_root/scripts/sign-app.py"'))

    def test_stale_runtime_replaced_with_all_current_sources(self):
        build_python = os.environ.get('VEMO_BUILD_PYTHON', sys.executable)
        available = subprocess.run([build_python, '-c',
                                    'import setuptools, wheel; from importlib.metadata import version; '
                                    'assert int(version("setuptools").split(".")[0]) >= 75'],
                                   capture_output=True)
        if available.returncode:
            self.skipTest('Set VEMO_BUILD_PYTHON to an offline interpreter with setuptools>=75 and wheel')
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary) / 'runtime'
            venv.EnvBuilder(with_pip=True).create(runtime)
            python = runtime / 'bin/python3'
            site = Path(subprocess.check_output([str(python), '-c',
                        'import sysconfig; print(sysconfig.get_path("purelib"))'], text=True).strip())
            package = site / 'voice_memo_transcriber'
            package.mkdir()
            (package / 'dates.py').write_text('raise RuntimeError("stale engine")\n')
            (package / 'deleted.py').write_text('# obsolete module\n')
            for version in ('0.1.0', '0.2.0'):
                (site / ('voice_memo_transcriber-' + version + '.dist-info')).mkdir()
            dependency = site / 'untouched_dependency.py'
            dependency.write_text('# dependency sentinel\n')
            metadata = runtime / 'vemo-runtime.json'
            metadata.write_text(json.dumps({'engine_wheel_sha256': 'stale', 'archive_sha256': 'preserved'}))
            wheel_hashes = []
            run = subprocess.run

            def capture_wheel(command, **kwargs):
                result = run(command, **kwargs)
                if 'cwd' in kwargs:
                    wheel, = (kwargs['cwd'] / 'wheels').glob('*.whl')
                    wheel_hashes.append(hashlib.sha256(wheel.read_bytes()).hexdigest())
                return result

            with patch.object(helper.subprocess, 'run', side_effect=capture_wheel):
                helper.refresh(PROJECT, runtime, build_python)
            expected = helper.source_manifest(PROJECT / 'src/voice_memo_transcriber')
            self.assertEqual(helper.source_manifest(package), expected)
            self.assertFalse((package / 'deleted.py').exists())
            self.assertEqual(len(list(site.glob('voice_memo_transcriber-*.dist-info'))), 1)
            self.assertEqual(dependency.read_text(), '# dependency sentinel\n')
            actual = json.loads(metadata.read_text())
            self.assertEqual(actual['engine_source_sha256'], expected)
            self.assertEqual(actual['engine_wheel_sha256'], wheel_hashes[0])
            self.assertEqual(actual['archive_sha256'], 'preserved')
            self.assertFalse(list(site.glob('voice_memo_transcriber-*.dist-info/direct_url.json')))
            subprocess.run([str(python), '-I', '-c',
                            'from voice_memo_transcriber.dates import recording_day; '
                            'assert recording_day("2026-10-17T00:30:00+09:00", "UTC+09:00") == "2026-10-17"'],
                           check=True)
