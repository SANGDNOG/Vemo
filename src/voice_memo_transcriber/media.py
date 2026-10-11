import hashlib,math,os,stat,uuid,wave
from pathlib import Path

def validate_owned(root,path):
    root=Path(root).resolve(); path=Path(path)
    if not path.is_absolute():raise ValueError('owned_path_not_absolute')
    if '..' in path.parts:raise ValueError('owned_path_traversal')
    try:path.resolve().relative_to(root)
    except ValueError:raise ValueError('owned_path_outside_state')
    current=path
    while current.resolve()!=root:
        if current.is_symlink():raise ValueError('owned_path_symlink')
        current=current.parent
    if not path.is_file():raise ValueError('owned_path_not_file')
    return path

def stable_copy(root,source,destination,revision_before,revision_after):
    source=validate_owned(root,source)
    if revision_before!=revision_after or not isinstance(revision_before,dict):raise ValueError('snapshot_changed')
    destination=Path(destination);destination.parent.mkdir(parents=True,exist_ok=True)
    fd=os.open(source,os.O_RDONLY|os.O_NOFOLLOW);tmp=destination.with_name(destination.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        before=os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_size!=revision_before.get('size') or before.st_mtime_ns!=revision_before.get('mtime_ns'):raise ValueError('snapshot_changed')
        with os.fdopen(fd,'rb',closefd=False) as src,open(tmp,'xb') as out:
            for block in iter(lambda:src.read(1024*1024),b''):out.write(block)
            out.flush();os.fsync(out.fileno())
        after=os.fstat(fd)
        if (before.st_size,before.st_mtime_ns,before.st_ino)!=(after.st_size,after.st_mtime_ns,after.st_ino):raise ValueError('snapshot_changed')
        os.replace(tmp,destination)
    finally:
        os.close(fd)
        if tmp.exists():tmp.unlink()
    return destination

def pcm_identity(path):
    with wave.open(str(path),'rb') as f:
        if (f.getnchannels(),f.getsampwidth(),f.getframerate(),f.getcomptype())!=(1,2,16000,'NONE'):raise ValueError('PCM must be mono 16-bit 16kHz')
        h=hashlib.sha256(b'pcm-s16le-mono-16000-v1\0');frames=f.getnframes();count=0
        while True:
            block=f.readframes(65536)
            if not block:break
            count+=len(block)//2;h.update(block)
        if count!=frames or not frames:raise ValueError('empty_or_truncated_audio')
        return h.hexdigest(),frames/16000,frames

def chunk_ranges(frames,rate=16000,seconds=300):
    size=int(rate*seconds)
    if frames<1 or size<1 or seconds>1200:raise ValueError('invalid_chunk_bounds')
    return [(i,min(frames,i+size)) for i in range(0,frames,size)]

def extract_chunk(pcm,path,start,end):
    with wave.open(str(pcm),'rb') as src,wave.open(str(path),'wb') as out:
        out.setparams(src.getparams());src.setpos(start);out.writeframes(src.readframes(end-start))

def global_segments(segments,offset,duration):
    result=[]
    for segment in segments:
        a,b=float(segment['start_s']),float(segment['end_s'])
        if not math.isfinite(a) or not math.isfinite(b) or a<0 or b<a or b>duration+.05 or not isinstance(segment['text'],str):raise ValueError('invalid_alignment_bounds')
        result.append({'start_s':offset+a,'end_s':offset+min(b,duration),'text':segment['text']})
    return result
