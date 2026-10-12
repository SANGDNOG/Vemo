"""Deterministic external-model boundary used only by tests."""
import json,sys,pathlib,time,os
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
from voice_memo_transcriber.artifacts import atomic_json
from voice_memo_transcriber.media import stable_copy,pcm_identity
request=json.loads(pathlib.Path(sys.argv[1]).read_text());op=request['op'];uid=request.get('uuid','')
if request.get('sleep'):time.sleep(request['sleep'])
if ('fail-asr' in uid and op=='asr') or ('fail-align-title' in uid and op in {'align','title'}):sys.exit(2)
if op=='prepare':
    stable_copy(request['state_root'],request['source'],request['audio_path'],request['revision_before'],request['revision_after'])
    pathlib.Path(request['pcm_path']).write_bytes(pathlib.Path(request['audio_path']).read_bytes())
    h,d,n=pcm_identity(request['pcm_path']);result={'pcm_hash':h,'duration_s':d,'frames':n,'audio_path':request['audio_path'],'pcm_path':request['pcm_path']}
elif op=='asr':result={'text':('한국어 English 42. '*150 if uid=='checkpoint-notes' else '한국어 English 42'),'language':'Korean','truncated':False}
elif op=='align':result={'segments':[{'start_s':0,'end_s':request['duration_s'],'text':request['text']}]}
elif op=='title':
    if uid=='title-checkpoint' and 'title_checkpoint' not in request:
        result={'title_checkpoint':{'round':0,'chunks':[[1],[2]],'hints':['처음'],'index':1},'completed_units':1,'total_units':2}
    elif uid=='title-checkpoint':result={'topic':'마지막 구간 주제'}
    else:result={'topic':'테스트 주제'}
elif op=='notes':
    from voice_memo_transcriber.notes import notes_unit
    source=json.loads(pathlib.Path(request['artifact_path']).read_text())['text']
    def produce(text,budget):
        if uid=='fail-notes':return '{}'
        quote=text[:80]
        return {'summary':[{'text':'핵심 요약','quotes':[quote]}],'highlights':[{'quote':quote}],'topics':[{'keyword':'테스트','summary':'키워드 정리','quotes':[quote]}]}
    if request.get('notes_protocol')=='study_v1':
        from voice_memo_transcriber.notes import validate_notes
        if uid=='interrupted-notes':time.sleep(.15)
        if uid=='malformed-notes':result=[]
        elif uid=='fail-notes':result={'notes_protocol':'study_v1','notes_error_code':'notes_failed','completed_units':None,'total_units':None}
        elif uid.startswith('typed-notes-'):
            result={'notes_protocol':'study_v1','notes_error_code':uid.removeprefix('typed-notes-'),'completed_units':None,'total_units':None}
        else:
            notes=validate_notes(produce(source,8192),source)
            if uid=='wrong-source-notes':notes['source_sha256']='0'*64
            result={'notes_protocol':'study_v1','notes':notes,'completed_units':None,'total_units':None}
    else:result=[] if uid=='malformed-notes' else notes_unit(source,request.get('notes_checkpoint'),produce)

else:sys.exit(3)
atomic_json(sys.argv[2],result)
