#!/usr/bin/env python3
"""Explicit online bootstrap. Operational inference never calls this implicitly."""
import argparse,json,os,sys,threading,time
from pathlib import Path
from voice_memo_transcriber.models import PINS,OPTIONAL_PINS,write_manifest
from voice_memo_transcriber.artifacts import sha256

def observed_bytes(root):
    total=0
    for p in Path(root).rglob('*'):
        try:
            if p.is_file() and not p.is_symlink() and ('.cache' not in p.parts or p.name.endswith('.incomplete')):total+=p.stat().st_size
        except OSError:pass
    return total

def verified_files(local,siblings):
    files=[]
    for sibling in siblings:
        relative=sibling.rfilename;path=Path(local)/relative
        if Path(relative).is_absolute() or '..' in Path(relative).parts or path.is_symlink() or not path.is_file() or (sibling.size is not None and path.stat().st_size!=sibling.size):raise ValueError('file_integrity')
        actual=sha256(path)
        lfs=getattr(sibling,'lfs',None)
        expected=(lfs.get('sha256') if isinstance(lfs,dict) else getattr(lfs,'sha256',None)) if lfs else None
        if expected and actual!=expected:raise ValueError('upstream_lfs_hash_mismatch')
        if relative.endswith('.safetensors') and not expected:raise ValueError('upstream_lfs_hash_missing')
        files.append({'path':relative,'size':path.stat().st_size,'sha256':actual})
    return files

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--model-root',type=Path,required=True);parser.add_argument('--disable-xet',action='store_true');parser.add_argument('--notes-only',action='store_true');args=parser.parse_args()
    if args.disable_xet:os.environ['HF_HUB_DISABLE_XET']='1'
    from huggingface_hub import HfApi,snapshot_download
    args.model_root.mkdir(parents=True,exist_ok=True)
    try:entries=json.loads((args.model_root/'models.json').read_text())['models']
    except (OSError,ValueError,KeyError):entries={}
    def emit(name,downloaded,total,state,error=None):
        payload={'model':name,'downloaded_bytes':downloaded,'total_bytes':total,'state':state}
        if error:payload['error']=error
        print(json.dumps({'kind':'model_progress','payload':payload}),flush=True)
    for name,pin in (OPTIONAL_PINS if args.notes_only else PINS).items():
        local=args.model_root/name;local.mkdir(parents=True,exist_ok=True);total=None;done=threading.Event()
        emit(name,observed_bytes(local),None,'resolving')
        try:
            info=HfApi().model_info(pin['repo_id'],revision=pin['revision'],files_metadata=True)
            if info.sha!=pin['revision']:raise ValueError('revision_mismatch')
            sizes=[s.size for s in info.siblings]
            total=sum(sizes) if all(s is not None for s in sizes) else None
            def monitor():
                previous=-1;last_change=time.monotonic()
                while not done.wait(1):
                    count=observed_bytes(local)
                    if count!=previous:last_change=time.monotonic();previous=count
                    emit(name,count,total,'stalled' if time.monotonic()-last_change>60 else 'downloading')
            watcher=threading.Thread(target=monitor,daemon=True);watcher.start()
            snapshot_download(pin['repo_id'],revision=pin['revision'],local_dir=str(local))
            done.set();watcher.join()
            emit(name,observed_bytes(local),total,'verifying')
            files=verified_files(local,info.siblings)
            entries[name]={**pin,'files':files};write_manifest(args.model_root,entries)
            emit(name,observed_bytes(local),total,'ready')
        except Exception as exc:
            done.set()
            # Exception strings may contain transport URLs/tokens; expose only class.
            emit(name,observed_bytes(local),total,'failed',{'code':'model_download_failed','message':type(exc).__name__,'recoverable':True})
            return 1
    return 0
if __name__=='__main__':sys.exit(main())
