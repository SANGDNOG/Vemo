#!/usr/bin/env python3
"""Sign nested Mach-O files first, without following runtime symlinks."""
import pathlib, subprocess, sys
app, identity = pathlib.Path(sys.argv[1]), sys.argv[2]
magic = {b'\xfe\xed\xfa\xce', b'\xce\xfa\xed\xfe', b'\xfe\xed\xfa\xcf', b'\xcf\xfa\xed\xfe', b'\xca\xfe\xba\xbe', b'\xbe\xba\xfe\xca', b'\xca\xfe\xba\xbf', b'\xbf\xba\xfe\xca'}
files = []
for path in app.rglob('*'):
    if path.is_symlink() or not path.is_file(): continue
    with path.open('rb') as source:
        if source.read(4) in magic: files.append(path)
for path in sorted(files, key=lambda p: len(p.parts), reverse=True) + [app]:
    subprocess.run(['codesign', '--force', '--sign', identity, '--timestamp=none', str(path)], check=True, stdout=subprocess.DEVNULL)
print(f'Signed {len(files)} native components and app', file=sys.stderr)
