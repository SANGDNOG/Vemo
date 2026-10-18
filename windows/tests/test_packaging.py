"""Bounded stdlib regression checks; all temporary fixtures stay in this evidence directory."""
import hashlib
import importlib.util
import json
import pathlib
import tempfile
import unittest
import warnings
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'windows/scripts/package.py'
MODULE = None
if SCRIPT.is_file():
    spec = importlib.util.spec_from_file_location('vemo_package', SCRIPT)
    MODULE = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(MODULE)
ABC = {'bytes': 3, 'sha256': 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'}

class PackagingChecks(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(MODULE, 'Packaging implementation is not present yet')
        self.tmp = tempfile.TemporaryDirectory(dir=pathlib.Path(__file__).parent)
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)

    def test_windows_paths_reject_traversal_devices_and_aliases(self):
        MODULE.validate_paths(['windows/win-x64/Vemo.App.exe', 'docs/FUNCTIONAL_PREVIEW.md'])
        for name in ['', '/root', '../secret', 'a/../b', 'a//b', './b', 'a\\b', 'C:secret',
                     'a/NUL.txt', 'COM1', 'a/lpt².txt', 'a/file.', 'a/file ', 'a/ab\0cd', 'a/*']:
            with self.subTest(name=name), self.assertRaises(ValueError):
                MODULE.validate_paths([name])
        for names in [['a/File', 'a/file'], ['a', 'a/b'], ['A/b', 'a'], ['a/B/one', 'a/b/two']]:
            with self.subTest(names=names), self.assertRaises(ValueError):
                MODULE.validate_paths(names)

    def test_manifest_duplicate_keys_rejected(self):
        with self.assertRaises(ValueError):
            MODULE.load_json(b'{"sha256":"first","sha256":"second"}')

    def test_current_snapshot_matches_actual_published_catalogue_layout(self):
        publication = ROOT / MODULE.PUBLICATION
        index = MODULE.load_json((ROOT / 'windows/licenses/index.json').read_bytes())
        data = MODULE.snapshots(ROOT, publication, index)
        self.assertEqual(data['windows/model-catalogue.json'], (publication / 'model-catalogue.json').read_bytes())
        self.assertEqual(data['windows/runtime-catalogue.json'], (publication / 'provision-win-x64/runtime-catalogue.json').read_bytes())

    def test_tree_rejects_tamper_extra_files_and_links(self):
        (self.root / 'ok.txt').write_bytes(b'abc')
        MODULE.verify_tree(self.root, [{'path': 'ok.txt', **ABC}])
        (self.root / 'ok.txt').write_bytes(b'abd')
        with self.assertRaises(ValueError):
            MODULE.verify_tree(self.root, [{'path': 'ok.txt', **ABC}])
        (self.root / 'ok.txt').write_bytes(b'abc')
        (self.root / 'private.json').write_text('{}')
        with self.assertRaises(ValueError):
            MODULE.verify_tree(self.root, [{'path': 'ok.txt', **ABC}])
        (self.root / 'private.json').unlink()
        (self.root / 'alias').symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(ValueError):
            MODULE.safe_file(self.root, 'alias/ok.txt')
        with self.assertRaises(ValueError):
            MODULE.verify_tree(self.root, [{'path': 'ok.txt', **ABC}])

    def test_zip_checks_bytes_extra_members_duplicates_and_symlinks(self):
        archive = self.root / 'fixture.zip'
        expected = {'bundle/ok.txt': ABC}
        cases = [([('bundle/ok.txt', b'abc')], True),
                 ([('bundle/ok.txt', b'abd')], False),
                 ([('bundle/ok.txt', b'abc'), ('private.json', b'{}')], False),
                 ([('bundle/ok.txt', b'abc'), ('bundle/OK.txt', b'abc')], False),
                 ([('../bundle/ok.txt', b'abc')], False)]
        for entries, valid in cases:
            with zipfile.ZipFile(archive, 'w') as z:
                for name, data in entries:
                    z.writestr(name, data)
            if valid:
                MODULE.verify_archive(archive, expected)
            else:
                with self.assertRaises(ValueError):
                    MODULE.verify_archive(archive, expected)
        info = zipfile.ZipInfo('bundle/ok.txt')
        info.create_system = 3
        info.external_attr = 0o120777 << 16
        with zipfile.ZipFile(archive, 'w') as z:
            z.writestr(info, b'abc')
        with self.assertRaises(ValueError):
            MODULE.verify_archive(archive, expected)

    def test_packaged_provision_notice_keeps_bytes_and_gets_exact_shared_licenses(self):
        for arch in ('x64', 'arm64'):
            with self.subTest(arch=arch):
                publication = self.root / arch
                provision = 'provision-win-' + arch
                (publication / provision).mkdir(parents=True)
                notice = b'[license inventory](licenses/index.json)\n'
                (publication / provision / 'THIRD-PARTY-NOTICES.md').write_bytes(notice)
                manifest = {'bundles': {prefix + 'win-' + arch: {'files': []}
                                       for prefix in ('', 'provision-', 'validation-')}}
                manifest['bundles'][provision]['files'] = [{
                    'path': 'THIRD-PARTY-NOTICES.md', 'bytes': len(notice),
                    'sha256': hashlib.sha256(notice).hexdigest()}]
                shared = {'windows/README.md': b'[details](../docs/FUNCTIONAL_PREVIEW.md)',
                          'docs/FUNCTIONAL_PREVIEW.md': b'[Windows](../windows/README.md)',
                          'docs/design/NATIVE_WHITE.md': b'Native White fixture',
                          'windows/THIRD-PARTY-NOTICES.md': notice,
                          'windows/licenses/index.json': b'{"fixture":true}',
                          'windows/licenses/component/LICENSE.md': b'public license fixture'}
                archive = self.root / (arch + '.zip')
                MODULE.write_distribution(archive, arch, publication, manifest, shared)
                with zipfile.ZipFile(archive) as z:
                    self.assertEqual(z.read('windows/' + provision + '/THIRD-PARTY-NOTICES.md'), notice)
                    entries = {e['path']: e for e in json.loads(z.read('PACKAGE-MANIFEST.json'))['files']}
                    for relative in ('index.json', 'component/LICENSE.md'):
                        target = 'windows/' + provision + '/licenses/' + relative
                        self.assertIn(target, z.namelist())
                        expected = shared['windows/licenses/' + relative]
                        self.assertEqual(z.read(target), expected)
                        self.assertEqual(entries[target]['bytes'], len(expected))
                        self.assertEqual(entries[target]['sha256'], hashlib.sha256(expected).hexdigest())
                self.assertEqual((publication / provision / 'THIRD-PARTY-NOTICES.md').read_bytes(), notice)

    def test_recursive_markdown_check_rejects_missing_nested_target(self):
        self.assertTrue(callable(getattr(MODULE, 'verify_markdown_links', None)),
                        'Packager must check every Markdown file, including nested notices')
        archive = self.root / 'links.zip'
        documents = {'README.md': b'[details](docs/guide.md)\n[web](https://example.org/)\n',
                     'docs/guide.md': b'[copy][license]\n[license]: ../windows/licenses/copy%20notice.txt#terms\n',
                     'windows/provision-win-x64/THIRD-PARTY-NOTICES.md': b'[license](licenses/index.json)\n'}
        for include_nested in (False, True):
            with zipfile.ZipFile(archive, 'w') as z:
                for name, data in documents.items():
                    z.writestr(name, data)
                z.writestr('windows/licenses/copy notice.txt', b'license')
                if include_nested:
                    z.writestr('windows/provision-win-x64/licenses/index.json', b'{}')
            if include_nested:
                self.assertEqual(MODULE.verify_markdown_links(archive),
                                 {'markdown_files': 3, 'relative_links': 3})
            else:
                with self.assertRaisesRegex(ValueError, 'provision-win-x64/THIRD-PARTY-NOTICES.md'):
                    MODULE.verify_markdown_links(archive)

if __name__ == '__main__':
    unittest.main(verbosity=2)
