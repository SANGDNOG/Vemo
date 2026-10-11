import hashlib
import json
import os
import uuid
from pathlib import Path
from .titles import display_title

def sha256(path):
    h=hashlib.sha256()
    with open(path,'rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''): h.update(block)
    return h.hexdigest()

def sync_dir(path):
    fd=os.open(path,os.O_RDONLY)
    try: os.fsync(fd)
    finally: os.close(fd)

def atomic_json(path,value):
    path=Path(path); path.parent.mkdir(parents=True,exist_ok=True)
    tmp=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        with open(tmp,'x',encoding='utf-8') as f:
            json.dump(value,f,ensure_ascii=False,allow_nan=False); f.flush(); os.fsync(f.fileno())
        os.replace(tmp,path); sync_dir(path.parent)
    finally:
        if tmp.exists(): tmp.unlink()

def subtitle_time(seconds,vtt=False):
    millis=round(seconds*1000); h,m=divmod(millis//1000,3600); m,s=divmod(m,60)
    return f'{h:02d}:{m:02d}:{s:02d}{"." if vtt else ","}{millis%1000:03d}'

def build_publication(root,job,transcript,models):
    title=display_title(job['topic'],job['recording_date'])
    revision={'number':job['revision'],'pcm_hash':job['pcm_hash'],'models':models,'recorded_at':job.get('recorded_at'),'date_provenance':job.get('date_provenance'),'original_timezone':job.get('original_timezone')}
    identity=json.dumps([job['uuid'],revision,title,transcript],sort_keys=True,ensure_ascii=False).encode()
    publication_id=hashlib.sha256(identity).hexdigest()
    directory=Path(root)/'artifacts'/job['id']/'versions'/publication_id
    manifest_path=directory/'manifest.json'
    if manifest_path.exists():
        m=json.loads(manifest_path.read_text())
        if all(sha256(f['path'])==f['sha256'] for f in m['files']): return str(manifest_path)
        raise ValueError('local_artifact_conflict')
    directory.mkdir(parents=True,exist_ok=True)
    contents={title+'.txt':title+'\n\n'+transcript['text']+'\n',title+'.md':'# '+title+'\n\n'+transcript['text']+'\n'}
    if transcript['alignment_status']=='completed' and transcript['segments']:
        contents[title+'.srt']='\n\n'.join(f'{i+1}\n{subtitle_time(s["start_s"])} --> {subtitle_time(s["end_s"])}\n{s["text"]}' for i,s in enumerate(transcript['segments']))+'\n'
        contents[title+'.vtt']='WEBVTT\n\n'+'\n\n'.join(f'{subtitle_time(s["start_s"],True)} --> {subtitle_time(s["end_s"],True)}\n{s["text"]}' for s in transcript['segments'])+'\n'
    files=[]
    for name,content in contents.items():
        path=directory/name
        encoded=content.encode('utf-8')
        if path.exists():
            if path.read_bytes()!=encoded: raise ValueError('local_artifact_conflict')
        else:
            with open(path,'xb') as f: f.write(encoded); f.flush(); os.fsync(f.fileno())
        files.append({'name':name,'path':str(path),'sha256':sha256(path)})
    manifest={'schema_version':1,'publication_id':publication_id,'job_id':job['id'],'uuid':job['uuid'],'recording_date':job['recording_date'],'title':title,'revision':revision,'files':files}
    atomic_json(manifest_path,manifest)
    return str(manifest_path)

def valid_receipt(manifest,receipt):
    return isinstance(receipt,dict) and receipt.get('publication_id')==manifest['publication_id'] and isinstance(receipt.get('output_manifest_path'),str) and Path(receipt['output_manifest_path']).is_absolute() and receipt.get('artifact_hashes')=={f['name']:f['sha256'] for f in manifest['files']}
