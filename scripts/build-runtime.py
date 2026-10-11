#!/usr/bin/env python3
"""Build a relocatable Apple Silicon Python runtime. No user's venv is copied."""
import argparse,hashlib,json,os,pathlib,shutil,subprocess,tarfile,urllib.request

PROJECT=pathlib.Path(__file__).resolve().parents[1]
ASSET='cpython-3.12.15+20261009-aarch64-apple-darwin-install_only.tar.gz'
URL='https://github.com/astral-sh/python-build-standalone/releases/download/20261009/'+ASSET.replace('+','%2B')
SHA256='83dcb04777c798bd95f3687acf9ae84b09860272ee6bdfb05be8bcf9447f75b8'
def run(args,**kwargs):subprocess.run([str(x) for x in args],check=True,**kwargs)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--destination',type=pathlib.Path,default=PROJECT/'build/portable/python');parser.add_argument('--reuse',action='store_true');args=parser.parse_args()
    destination=args.destination.resolve();archive=PROJECT/'build/downloads/cpython-3.12.15-20261009-arm64.tar.gz';archive.parent.mkdir(parents=True,exist_ok=True)
    if not archive.exists():
        partial=archive.with_suffix('.partial')
        with urllib.request.urlopen(URL,timeout=60) as source,partial.open('wb') as output:shutil.copyfileobj(source,output)
        if hashlib.sha256(partial.read_bytes()).hexdigest()!=SHA256:raise RuntimeError('standalone Python checksum mismatch')
        partial.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest()!=SHA256:raise RuntimeError('standalone Python checksum mismatch')
    if destination.exists():
        if not args.reuse:raise RuntimeError('destination exists; pass --reuse for this build runtime')
    else:
        staging=destination.parent/('.python-extract-'+str(os.getpid()));staging.mkdir(parents=True)
        try:
            with tarfile.open(archive) as source:
                for member in source.getmembers():
                    path=(staging/member.name).resolve()
                    if not path.is_relative_to(staging.resolve()):raise RuntimeError('unsafe archive member')
                    if member.issym() or member.islnk():
                        linked=((path.parent/member.linkname) if member.issym() else (staging/member.linkname)).resolve()
                        if not linked.is_relative_to(staging.resolve()):raise RuntimeError('unsafe archive link')
                source.extractall(staging)
            (staging/'python').rename(destination)
        finally:shutil.rmtree(staging)
    environment=os.environ.copy()
    for key in ('PYTHONHOME','PYTHONPATH','VIRTUAL_ENV'):environment.pop(key,None)
    environment['PYTHONDONTWRITEBYTECODE']='1'
    python=destination/'bin/python3';run([python,'-c','import sys,platform; assert sys.version_info[:3]==(3,12,15); assert platform.machine()=="arm64"'],env=environment)
    lock=PROJECT/'requirements-runtime.lock';requirements=lock if lock.exists() else PROJECT/'requirements.lock'
    run([python,'-m','pip','install','--index-url','https://pypi.org/simple','-r',requirements],env=environment)
    wheels=PROJECT/'build/wheels';wheels.mkdir(parents=True,exist_ok=True)
    run([python,'-m','pip','wheel','--no-deps','--wheel-dir',wheels,PROJECT],env=environment)
    wheel=wheels/'voice_memo_transcriber-0.2.0-py3-none-any.whl'
    run([python,'-m','pip','install','--no-deps','--force-reinstall',wheel],env=environment)
    run([python,'-m','pip','check'],env=environment)
    for path in list(destination.rglob('direct_url.json')):path.unlink()
    for path in list(destination.rglob('__pycache__')):
        if path.is_dir():shutil.rmtree(path)
    # Pip console scripts contain absolute build-machine shebangs. The app uses
    # python -m exclusively; ship only the relocatable interpreter and aliases.
    for path in (destination/'bin').iterdir():
        if path.name not in {'python','python3','python3.12'} and (path.is_file() or path.is_symlink()):path.unlink()
    licenses=destination/'licenses';licenses.mkdir(exist_ok=True)
    shutil.copy2(PROJECT/'LICENSE',licenses/'Vemo-MIT.txt');shutil.copy2(PROJECT/'THIRD_PARTY_NOTICES.md',licenses/'THIRD_PARTY_NOTICES.md')
    shutil.copytree(PROJECT/'third-party/python-standalone',licenses/'python-standalone',dirs_exist_ok=True)
    freeze=subprocess.check_output([str(python),'-m','pip','freeze'],env=environment,text=True);(destination/'requirements-resolved.txt').write_text(freeze)
    metadata={'python_version':'3.12.15','standalone_release':'20261009','archive_name':ASSET,'archive_sha256':SHA256,'source_url':URL,'engine_wheel_sha256':hashlib.sha256(wheel.read_bytes()).hexdigest(),'models_bundled':False,'license_locations':['lib/python3.12/LICENSE.txt','lib/python3.12/site-packages/*.dist-info/licenses','licenses']}
    (destination/'vemo-runtime.json').write_text(json.dumps(metadata,indent=2)+'\n');print(destination)
if __name__=='__main__':main()
