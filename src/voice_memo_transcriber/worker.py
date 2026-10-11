"""One local operation per process; models are never resident together."""
import json,os,sys,wave
from pathlib import Path
from .artifacts import atomic_json,sync_dir
from .media import stable_copy,pcm_identity,validate_owned
from .titles import token_chunks

def check_network_event(event,args):
    if event in {'socket.connect','socket.connect_ex','socket.getaddrinfo','socket.sendto'}:
        raise PermissionError('operational inference is offline')

def prepare(request):
    import subprocess
    root=Path(request['state_root']).resolve()
    audio=stable_copy(root,request['source'],request['audio_path'],request['revision_before'],request['revision_after'])
    pcm=Path(request['pcm_path']);tmp=pcm.with_name(pcm.stem+'.partial.wav')
    # No shell/URL source; the decoder reads only our stable owned copy.
    ffmpeg=Path(request['ffmpeg_path'])
    if not ffmpeg.is_absolute() or not ffmpeg.is_file():raise ValueError('ffmpeg_missing')
    arguments=([str(ffmpeg),'normalize',str(audio),str(tmp)] if ffmpeg.name=='VemoAudio' else
        [str(ffmpeg),'-nostdin','-v','error','-protocol_whitelist','file,pipe','-i',str(audio),'-map','0:a:0','-vn','-ac','1','-ar','16000','-c:a','pcm_s16le','-y',str(tmp)])
    try:
        subprocess.run(arguments,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,close_fds=True,timeout=600,check=True)
        h,duration,frames=pcm_identity(tmp)
        with open(tmp,'rb') as f:os.fsync(f.fileno())
        os.replace(tmp,pcm);sync_dir(pcm.parent)
    finally:
        if tmp.exists():tmp.unlink()
    return {'pcm_hash':h,'duration_s':duration,'frames':frames,'audio_path':str(audio),'pcm_path':str(pcm)}

def local_model(request,name):
    path=Path(request['model_root']).resolve()/name
    if not path.is_dir() or path.is_symlink() or not (path/'config.json').is_file() or not list(path.glob('*.safetensors')):
        raise ValueError('local_model_missing')
    return str(path)

def audio_array(path):
    import numpy as np
    with wave.open(str(path),'rb') as f:
        if (f.getnchannels(),f.getsampwidth(),f.getframerate())!=(1,2,16000):raise ValueError('invalid_audio_format')
        return np.frombuffer(f.readframes(f.getnframes()),dtype='<i2').astype(np.float32)/32768.0

def asr(request):
    import mlx.core as mx
    from mlx_qwen3_asr import transcribe
    audio=audio_array(request['audio_path'])
    if len(audio)>16000*1200:raise ValueError('asr_chunk_exceeds_20min')
    # This user's recordings are Korean-first with some English. In the real
    # mixed fixture autodetection dropped Korean; the Korean prompt preserved
    # both languages verbatim. It selects the decoding prompt, not translation.
    result=transcribe(audio,model=local_model(request,'asr'),language='Korean',dtype=mx.float16,return_timestamps=False,return_chunks=True,verbose=False)
    if result.truncated:raise ValueError('asr_generation_truncated')
    return {'text':result.text,'language':result.language,'truncated':result.truncated}

def align(request):
    import mlx.core as mx
    from mlx_qwen3_asr import ForcedAligner
    audio=audio_array(request['audio_path'])
    if len(audio)>16000*300:raise ValueError('alignment_chunk_exceeds_5min')
    # Silence has no word timestamps and is an honest completed empty alignment.
    if not request['text'].strip():return {'segments':[]}
    language=request.get('language')
    if not language or language=='unknown':raise ValueError('alignment_language_unknown')
    items=ForcedAligner(local_model(request,'aligner'),dtype=mx.float16).align(audio,request['text'],language)
    return {'segments':[{'start_s':float(w.start_time),'end_s':float(w.end_time),'text':w.text} for w in items]}

def title_unit(state,hint,final,encode):
    """One hint or final generation. State is committed by the queue after return."""
    chunks=state['chunks'];index=state['index'];hints=list(state['hints']);round_number=state['round']
    if not chunks or any(len(c)>3072 for c in chunks) or index<0 or index>=len(chunks) or len(hints)!=index or round_number>16:raise ValueError('invalid_title_checkpoint')
    if len(chunks)==1:return {'topic':final(chunks[0],64)}
    hints.append(hint(chunks[index],96));index+=1
    if index==len(chunks):
        chunks=token_chunks(encode('\n'.join(hints)));hints=[];index=0;round_number+=1
    return {'title_checkpoint':{'round':round_number,'chunks':chunks,'hints':hints,'index':index},'completed_units':index,'total_units':len(chunks)}

def title(request):
    from mlx_lm import load,generate
    from mlx_lm.sample_utils import make_sampler
    model,tokenizer=load(local_model(request,'title'),trust_remote_code=False)
    transcript=json.loads(Path(request['artifact_path']).read_text())['text']
    tokens=tokenizer.encode(transcript,add_special_tokens=False)
    state=request.get('title_checkpoint') or {'round':0,'chunks':token_chunks(tokens),'hints':[],'index':0}
    def produce(chunk,budget,hint=False):
        instruction=('녹취의 모든 핵심 주제 단서를 간결하게 적으세요. 날짜나 설명 없이 단서만 출력하세요.' if hint else '녹취의 중심 주제로 한국어 제목 하나만 출력하세요. 최대 40자. 날짜, 따옴표, 설명을 쓰지 마세요. 영어 고유명사는 유지하세요.')
        text=tokenizer.decode(chunk)
        messages=[{'role':'system','content':instruction},{'role':'user','content':text}]
        prompt=tokenizer.apply_chat_template(messages,tokenize=True,add_generation_prompt=True)
        return generate(model,tokenizer,prompt=prompt,max_tokens=budget,sampler=make_sampler(temp=0),verbose=False)
    return title_unit(state,lambda c,b:produce(c,b,True),lambda c,b:produce(c,b),lambda text:tokenizer.encode(text,add_special_tokens=False))

def main():
    if len(sys.argv)!=3:return 2
    os.environ.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_DATASETS_OFFLINE='1',TOKENIZERS_PARALLELISM='false')
    # Defense beyond cache flags: no accidental Hub request can leave this process.
    sys.addaudithook(check_network_event)
    try:
        request=json.loads(Path(sys.argv[1]).read_text());op=request['op']
        if op not in {'prepare','asr','align','title'}:raise ValueError('unknown_operation')
        result={'prepare':prepare,'asr':asr,'align':align,'title':title}[op](request)
        atomic_json(sys.argv[2],result);return 0
    except Exception as exc:
        print('local_worker_failed: '+type(exc).__name__,file=sys.stderr);return 1
if __name__=='__main__':sys.exit(main())
