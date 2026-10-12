import json
import threading
from pathlib import Path
from .artifacts import atomic_json,sha256
PINS={
 'asr':{'repo_id':'Qwen/Qwen3-ASR-1.7B','revision':'7278e1e70fe206f11671096ffdd38061171dd6e5'},
 'aligner':{'repo_id':'Qwen/Qwen3-ForcedAligner-0.6B','revision':'c7cbfc2048c462b0d63a45797104fc9db3ad62b7'},
 'title':{'repo_id':'mlx-community/Qwen3-4B-Instruct-2507-4bit','revision':'50d427756c6b1b2fe0c0a10f67fbda1fc8e82c1b'}}

# Optional notes inference must never block ASR, alignment or title processing.
OPTIONAL_PINS={
 'notes':{'repo_id':'mlx-community/Qwen3.5-27B-4bit','revision':'45797d2985a12c55e6473686e9ea91b95e959553'}}
ALL_PINS={**PINS,**OPTIONAL_PINS}

class ModelInventory:
    """Read-only hashing on a daemon; all engine DB writes stay on its owner thread."""
    def __init__(self,root):
        self.root=Path(root);self.result={'models_ready':False,'notes_ready':False,'model_status':{name:{'state':'verifying','revision':pin['revision']} for name,pin in ALL_PINS.items()}}
        self.running=False;self.start()
    def start(self):
        if self.running:return
        self.running=True
        def run():
            try:self.result=model_status(self.root)
            except Exception:self.result={'models_ready':False,'notes_ready':False,'model_status':{name:{'state':'invalid','revision':pin['revision']} for name,pin in ALL_PINS.items()}}
            finally:self.running=False
        threading.Thread(target=run,daemon=True).start()
    def snapshot(self):return self.result

_verified={}

def verified_hash(path,expected):
    stat=path.stat();signature=(str(path),stat.st_dev,stat.st_ino,stat.st_size,stat.st_mtime_ns,stat.st_ctime_ns,expected)
    if signature in _verified:return True
    actual=sha256(path);after=path.stat()
    if actual!=expected or (stat.st_dev,stat.st_ino,stat.st_size,stat.st_mtime_ns,stat.st_ctime_ns)!=(after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns,after.st_ctime_ns):return False
    if len(_verified)>10000:_verified.clear()
    _verified[signature]=True;return True

def write_manifest(root,entries):
    atomic_json(Path(root)/'models.json',{'schema_version':1,'models':entries})

def model_status(root,verify_hashes=True):
    root=Path(root)
    try:manifest=json.loads((root/'models.json').read_text()); entries=manifest['models']
    except (OSError,ValueError,KeyError):entries={}
    if not isinstance(entries,dict):entries={}
    states={}
    for name,pin in ALL_PINS.items():
        entry=entries.get(name,{})
        if not isinstance(entry,dict):entry={}
        ready=all(entry.get(k)==v for k,v in pin.items())
        files=entry.get('files',[])
        if not isinstance(files,list):files=[]
        names={f.get('path') for f in files if isinstance(f,dict) and isinstance(f.get('path'),str)}
        ready=ready and 'config.json' in names and 'tokenizer_config.json' in names and any(str(n).endswith('.safetensors') for n in names)
        for item in files:
            try:
                relative=Path(item['path']); path=root/name/relative
                if relative.is_absolute() or '..' in relative.parts or path.is_symlink() or not path.is_file() or path.stat().st_size!=item['size'] or (verify_hashes and not verified_hash(path,item['sha256'])):ready=False
            except (OSError,KeyError,TypeError):ready=False
        states[name]={'state':'ready' if ready else 'missing_or_invalid','revision':pin['revision']}
    return {'models_ready':all(states[name]['state']=='ready' for name in PINS),'notes_ready':states['notes']['state']=='ready','model_status':states}
