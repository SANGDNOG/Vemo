"""One-thread durable state machine. Readers and inference never touch SQLite."""
import json,math,os,subprocess,sys,time,uuid,threading,datetime as dt
from pathlib import Path
from .store import Store
from .dates import recording_day,validate_day
from .titles import sanitize_topic
from .protocol import parse_command
from .media import validate_owned,chunk_ranges,extract_chunk,global_segments
from .artifacts import atomic_json,build_publication,valid_receipt,sha256
from .models import model_status,ModelInventory
from .process import ManagedProcess

JOB_FIELDS=('id','uuid','topic','recording_date','recorded_at','duration_s','status','wait_reason','stage','completed_units','total_units','elapsed_seconds','artifact_path','audio_path','pcm_path','error','title_status','alignment_status','original_title')
RUNNING={'preparing','transcribing','aligning','titling'}

def on_ac_power():
    try:
        result=subprocess.run(['/usr/bin/pmset','-g','batt'],stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,timeout=2,close_fds=True)
        return result.returncode==0 and b"'AC Power'" in result.stdout
    except (OSError,subprocess.TimeoutExpired):return False

def failure(code,message=None):
    return {'code':code,'message':message or code,'recoverable':True}

class Engine:
    def __init__(self,state_root,model_root,emit,power_check=None,readiness=None,worker_command=None,ffmpeg_path='/opt/homebrew/bin/ffmpeg',worker_timeout=3600):
        self.root=Path(state_root).resolve();self.model_root=Path(model_root).resolve();self.store=Store(self.root)
        self.lifecycle_lock=threading.RLock()
        self.emit_callback=emit;self.sequence=0;self.active=None;self.download=None;self.closed=False;self.stopping=False
        self.worker_command=worker_command or [sys.executable,'-m','voice_memo_transcriber.worker']
        self.worker_timeout=worker_timeout;self.ffmpeg_path=ffmpeg_path;self.power_check=power_check or on_ac_power
        self.inventory=ModelInventory(self.model_root) if readiness is None else None
        self.readiness=readiness or self.inventory.snapshot;self.models=self.readiness()
        self.last_power=float("-inf");self.power=False;self.last_models=float("-inf");self.last_replay={};self.started={};self.changed=set()
        # In-flight work was never committed. Recover the same phase/chunk.
        with self.store.transaction():
            for job in self.store.jobs():
                if job['status'] in RUNNING:
                    job.update(status='waiting',wait_reason='restart',stage=job['phase']);self.store.put(job)
    def emit(self,kind,payload):
        self.sequence+=1;self.emit_callback({'sequence':self.sequence,'protocol_version':1,'kind':kind,'payload':payload})
    def view(self,job):
        view={key:job.get(key) for key in JOB_FIELDS}
        view['elapsed_seconds']=job.get('elapsed_seconds',0)+(time.monotonic()-self.started[job['id']] if job['id'] in self.started else 0)
        return view
    def save(self,job):
        self.store.put(job);self.changed.add(job['id'])
    def flush_changed(self):
        ids=self.changed;self.changed=set()
        for jid in sorted(ids):self.emit('job_changed',{'job':self.view(self.store.get(jid))})
    def snapshot(self):
        return {'jobs':[self.view(j) for j in self.store.jobs()],'engine_status':{**self.models,'paused':self.store.get_meta('paused',False),'ac_only':self.store.get_meta('ac_only',True)}}
    def command(self,command):
        # Also validate direct embedding callers; service parses byte limits first.
        self.changed=set()
        try:
            command=parse_command(json.dumps(command,ensure_ascii=False).encode())
            result=self.store.request(command['request_id'],command,lambda:self.apply(command['kind'],command['payload']))
        except (ValueError,TypeError,KeyError,OverflowError) as exc:
            self.changed=set();result={'ok':False,'error':failure('invalid_command',str(exc) if isinstance(exc,ValueError) else 'invalid payload')}
        self.flush_changed()
        if command.get('kind') in {'hello','snapshot'} and result['ok']:self.emit('snapshot',self.snapshot())
        self.emit('command_result',{'request_id':command.get('request_id',''),**result})
        if command.get('kind')=='shutdown' and result['ok']:self.stopping=True
        return result
    def validate_memo(self,memo):
        if not isinstance(memo,dict):raise ValueError('invalid_memo')
        for key in ('uuid','source_reference'):
            if not isinstance(memo.get(key),str) or not 0<len(memo[key])<=256 or any(ord(c)<32 for c in memo[key]):raise ValueError('invalid_'+key)
        if memo.get('completion_evidence') not in {'finished','active','unknown'}:raise ValueError('invalid_completion_evidence')
        if memo.get('source_kind') not in {'voice_memos','import'}:raise ValueError('invalid_source_kind')
        if memo.get('duration_s') is not None and (not isinstance(memo['duration_s'],(int,float)) or not math.isfinite(memo['duration_s']) or memo['duration_s']<0):raise ValueError('invalid_duration')
        revision=memo.get('media_revision')
        if revision is not None:self.validate_revision(revision)
        day=recording_day(memo.get('recorded_at'),memo.get('original_timezone'))
        return day
    def validate_revision(self,rev):
        if not isinstance(rev,dict) or any(type(rev.get(k)) is not int or rev[k]<0 for k in ('size','mtime_ns')):raise ValueError('invalid_media_revision')
    def discover(self,memos,explicit=False):
        if not isinstance(memos,list) or len(memos)>50000:raise ValueError('invalid_memos')
        days=[self.validate_memo(m) for m in memos]
        baseline=self.store.get_meta('baseline_seen',False)
        cutoff=self.store.get_meta('baseline_cutoff_epoch',None)
        if not explicit and cutoff is None:
            # Initial iCloud inventory may arrive over many scans. Persist the
            # boundary so late historical rows do not become automatic work.
            cutoff=time.time();self.store.set_meta('baseline_cutoff_epoch',cutoff)
        for memo,day in zip(memos,days):
            old=self.store.by_uuid(memo['uuid'])
            if old:
                previous=old.get('media_revision');revision=memo.get('media_revision');prior_day=old.get('recording_date')
                changed=revision is not None and previous is not None and revision!=previous
                old.update(source_reference=memo['source_reference'],completion_evidence=memo['completion_evidence'],original_title=memo.get('original_title'),media_revision=revision)
                if old.get('date_provenance')!='user_supplied':
                    if memo.get('recorded_at') is not None:
                        old.update(recorded_at=memo['recorded_at'],recording_date=day,date_provenance=memo.get('recorded_at_source','unknown'),original_timezone=memo.get('original_timezone'))
                if changed and (old['queued'] or old.get('pcm_hash')):
                    old.update(refresh_requested=True,queued=True,publication_id=None,manifest_path=None)
                if old.get('recording_date')!=prior_day and old.get('artifact_path') and old.get('topic'):
                    old.update(revision=old['revision']+1,publication_id=None,manifest_path=None,queued=True,status='waiting',phase='publish')
                if explicit and not old.get('pcm_hash'):old['queued']=True
                self.save(old);continue
            job={key:None for key in JOB_FIELDS}
            recorded=memo.get('recorded_at')
            queued=explicit or (baseline and cutoff is not None and recorded is not None and dt.datetime.fromisoformat(recorded.replace('Z','+00:00')).timestamp()>cutoff)
            job.update(id=uuid.uuid4().hex,uuid=memo['uuid'],recorded_at=memo.get('recorded_at'),recording_date=day,date_provenance=memo.get('recorded_at_source','unknown'),original_timezone=memo.get('original_timezone'),duration_s=memo.get('duration_s'),original_title=memo.get('original_title'),status='waiting' if queued else 'unprocessed',stage='snapshot',wait_reason=None,elapsed_seconds=0,title_status='pending',alignment_status='pending',queued=queued,phase='snapshot',source_reference=memo['source_reference'],completion_evidence=memo['completion_evidence'],source_kind=memo['source_kind'],media_revision=memo.get('media_revision'),pcm_hash=None,revision=0,asr_chunks=[],aligned_chunks=[],attempts={},refresh_requested=False,publication_id=None,manifest_path=None)
            self.save(job)
        if not explicit:self.store.set_meta('baseline_seen',True)
    def apply(self,kind,payload):
        if kind in {'hello','snapshot','shutdown'}:return {'ok':True}
        if kind=='discover':self.discover(payload['memos']);return {'ok':True}
        if kind=='import':self.discover([payload['memo']],True);return {'ok':True}
        if kind in {'pause','resume'}:
            self.store.set_meta('paused',kind=='pause');return {'ok':True}
        if kind=='set_policy':
            if type(payload.get('ac_only')) is not bool:raise ValueError('ac_only_must_be_boolean')
            self.store.set_meta('ac_only',payload['ac_only']);return {'ok':True}
        if kind=='download_models':
            self.store.set_meta('download_requested',True);return {'ok':True}
        if kind in {'enqueue','backfill'}:
            ids=payload['uuids']
            if not isinstance(ids,list) or not all(isinstance(v,str) for v in ids):raise ValueError('invalid_uuids')
            for uid in ids:
                job=self.store.by_uuid(uid)
                if not job:raise ValueError('unknown_uuid')
                if job['status']=='unprocessed':job.update(queued=True,status='waiting',wait_reason=None);self.save(job)
            return {'ok':True}
        job=self.store.get(payload['job_id'])
        if kind=='snapshot_ready':
            if job['phase']!='snapshot' or not job['queued']:raise ValueError('snapshot_not_requested')
            if 'error' in payload:
                job.update(status='waiting',wait_reason='source_copy',error=self.safe_error(payload['error']));self.save(job);return {'ok':True}
            before,after=payload['revision_before'],payload['revision_after'];self.validate_revision(before);self.validate_revision(after)
            if before!=after:raise ValueError('snapshot_changed')
            path=validate_owned(self.root,payload['owned_path'])
            st=path.stat()
            if st.st_size!=after['size'] or st.st_mtime_ns!=after['mtime_ns']:raise ValueError('snapshot_changed')
            candidate=self.root/'work'/job['id']/'candidates'/uuid.uuid4().hex;candidate.mkdir(parents=True,exist_ok=True)
            job.update(phase='prepare',status='waiting',wait_reason=None,prepare_data={'source':str(path),'revision_before':before,'revision_after':after,'audio_path':str(candidate/('audio'+path.suffix)),'pcm_path':str(candidate/'pcm.wav')})
            self.save(job);return {'ok':True}
        if kind=='retry':
            job.update(queued=True,error=None,wait_reason=None,status='waiting',attempts={})
            if job['phase']=='publish':job['last_publish_error']=None
            self.save(job);return {'ok':True}
        if kind in {'retitle','set_recording_date'}:
            if kind=='set_recording_date' and not job.get('artifact_path'):
                job.update(recording_date=validate_day(payload['recording_date']),date_provenance='user_supplied')
                self.save(job);return {'ok':True}
            if not job.get('artifact_path'):raise ValueError('transcript_not_ready')
            if self.active and self.active['job_id']==job['id']:raise ValueError('job_busy')
            if kind=='set_recording_date':
                job.update(recording_date=validate_day(payload['recording_date']),date_provenance='user_supplied')
                phase='publish' if job.get('topic') else 'title'
            elif 'topic' in payload:
                job.update(topic=sanitize_topic(payload['topic']),title_status='manual');phase='publish'
            elif payload.get('generate') is True:job.update(title_status='pending');job.pop('title_checkpoint',None);phase='title'
            else:raise ValueError('topic_or_generate_required')
            if job['phase'] in {'asr','align','title','prepare','snapshot'}:phase=job['phase']
            job.update(revision=job['revision']+1,publication_id=None,manifest_path=None,phase=phase,queued=True,status='waiting',wait_reason=None,error=None,attempts={})
            self.save(job);return {'ok':True}
        if kind=='publish_ack':
            if job.get('refresh_requested') or payload['publication_id']!=job.get('publication_id'):raise ValueError('stale_publication')
            if job['status']=='completed' and 'error' in payload:return {'ok':True}
            if 'error' in payload:
                job.update(status='saving',wait_reason='publication',error=self.safe_error(payload['error']),last_publish_error=True);self.save(job);return {'ok':True}
            manifest=json.loads(Path(job['manifest_path']).read_text())
            if not valid_receipt(manifest,payload.get('receipt')):raise ValueError('invalid_publication_receipt')
            if not all(sha256(f['path'])==f['sha256'] for f in manifest['files']):raise ValueError('local_artifact_conflict')
            job.update(status='completed',wait_reason=None,error=None,queued=False,receipt=payload['receipt'],last_publish_error=None)
            self.save(job);return {'ok':True}
        raise ValueError('unknown_command')
    def safe_error(self,error):
        if not isinstance(error,dict):return failure('broker_failed')
        code=error.get('code','broker_failed')
        if not isinstance(code,str) or len(code)>128:code='broker_failed'
        # Broker messages are displayed, never logged; cap incoming untrusted size.
        return {'code':code,'message':str(error.get('message',code))[:1024],'recoverable':error.get('recoverable') is True}
    def launch(self,job,op,data):
        directory=self.root/'work'/job['id']/'tasks'/uuid.uuid4().hex;directory.mkdir(parents=True,exist_ok=True)
        request={'op':op,'uuid':job['uuid'],'state_root':str(self.root),'model_root':str(self.model_root),'ffmpeg_path':self.ffmpeg_path,**data}
        req=directory/'request.json';out=directory/'result.json';atomic_json(req,request)
        env=os.environ.copy();env.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_DATASETS_OFFLINE='1',TOKENIZERS_PARALLELISM='false')
        with self.lifecycle_lock:
            if self.stopping:return
            task=ManagedProcess([*self.worker_command,str(req),str(out)],timeout=self.worker_timeout,env=env)
            if self.stopping:task.stop();return
            self.active={'job_id':job['id'],'op':op,'task':task,'output':out,'index':data.get('index')}
        self.started[job['id']]=time.monotonic()
        job.update(status={'prepare':'preparing','asr':'transcribing','align':'aligning','title':'titling'}[op],stage={'prepare':'preparing','asr':'transcribing','align':'aligning','title':'titling'}[op],wait_reason=None,error=None)
        self.save(job)
    def complete_task(self):
        active=self.active
        if active is None or active['task'].poll() is None:return
        self.active=None;job=self.store.get(active['job_id']);op=active['op']
        job['elapsed_seconds']+=time.monotonic()-self.started.pop(job['id'],time.monotonic())
        try:
            if active['task'].proc.returncode!=0:raise ValueError('model_timeout' if active['task'].timed_out else 'worker_failed')
            result=json.loads(active['output'].read_text())
            self.accept(job,op,result,active['index'])
            job['attempts'].pop(op,None)
        except (OSError,ValueError,TypeError,KeyError) as exc:
            attempts=job['attempts'].get(op,0)+1;job['attempts'][op]=attempts
            if attempts<2:job.update(status='waiting',wait_reason='retry',error=failure('worker_failed'))
            elif op=='align':
                job.update(alignment_status='failed',aligned_chunks=[],phase='publish' if job['title_status']=='manual' else 'title',status='waiting',error=failure('alignment_failed'))
                self.write_transcript(job)
            elif op=='title':job.update(topic='음성 녹음',title_status='fallback',phase='publish',status='waiting',error=failure('title_failed'))
            else:job.update(status='failed',wait_reason=None,queued=False,error=failure('worker_timeout' if active['task'].timed_out else 'worker_failed'))
        self.save(job)
    def accept(self,job,op,result,index):
        if op=='prepare':
            h=result['pcm_hash']
            if not isinstance(h,str) or len(h)!=64 or any(c not in '0123456789abcdef' for c in h):raise ValueError('invalid_pcm_hash')
            for key in ('audio_path','pcm_path'):validate_owned(self.root,result[key])
            if type(result['frames']) is not int or result['frames']<1:raise ValueError('invalid_frames')
            if h==job.get('pcm_hash'):
                # Metadata/container-only changes retain all committed model results.
                resume=job.pop('refresh_resume',None)
                if resume not in {'asr','align','title','publish'}:
                    resume='publish' if job.get('topic') and job.get('artifact_path') else 'title' if job.get('alignment_status') in {'completed','failed'} else 'align' if len(job['asr_chunks'])==len(chunk_ranges(job['frames'])) else 'asr'
                job.update(phase=resume,status='waiting',wait_reason=None)
                return
            job.pop('title_checkpoint',None)
            job.update(pcm_hash=h,audio_path=result['audio_path'],pcm_path=result['pcm_path'],duration_s=float(result['duration_s']),frames=result['frames'],asr_chunks=[],aligned_chunks=[],topic=None,artifact_path=None,publication_id=None,manifest_path=None,revision=job['revision']+1,title_status='pending',alignment_status='pending',phase='asr',status='waiting',completed_units=0,total_units=len(chunk_ranges(result['frames'])))
        elif op=='asr':
            if result.get('truncated') is not False or not isinstance(result.get('text'),str):raise ValueError('truncated_or_invalid_asr')
            if len(job['asr_chunks'])!=index:raise ValueError('checkpoint_index_mismatch')
            job['asr_chunks'].append({'text':result['text'],'language':result.get('language','Korean')})
            job.update(status='waiting',completed_units=len(job['asr_chunks']))
            if len(job['asr_chunks'])==len(chunk_ranges(job['frames'])):
                job.update(phase='align',completed_units=0);self.write_transcript(job)
        elif op=='align':
            start,end=chunk_ranges(job['frames'])[index]
            segments=global_segments(result['segments'],start/16000,(end-start)/16000)
            if job['asr_chunks'][index]['text'].strip() and not segments:raise ValueError('empty_alignment')
            job['aligned_chunks'].append(segments)
            job.update(status='waiting',completed_units=len(job['aligned_chunks']))
            if len(job['aligned_chunks'])==len(job['asr_chunks']):
                job.update(phase='publish' if job['title_status']=='manual' else 'title',alignment_status='completed');self.write_transcript(job)
        elif op=='title':
            if 'title_checkpoint' in result:
                checkpoint=result['title_checkpoint']
                if not isinstance(checkpoint,dict) or not isinstance(checkpoint.get('chunks'),list) or not isinstance(checkpoint.get('hints'),list):raise ValueError('invalid_title_checkpoint')
                checkpoint_path=self.root/'work'/job['id']/('title-checkpoint-'+uuid.uuid4().hex+'.json')
                atomic_json(checkpoint_path,checkpoint)
                job.update(title_checkpoint=str(checkpoint_path),status='waiting',completed_units=result['completed_units'],total_units=result['total_units'])
                return
            if not isinstance(result.get('topic'),str) or not result['topic'].strip():raise ValueError('empty_title')
            job.update(topic=sanitize_topic(result['topic']),title_status='completed',phase='publish',status='waiting',completed_units=None,total_units=None)
    def write_transcript(self,job):
        transcript={'text':'\n'.join(c['text'] for c in job['asr_chunks']),'segments':[s for chunk in job['aligned_chunks'] for s in chunk],'alignment_status':job['alignment_status']}
        digest=__import__('hashlib').sha256(json.dumps(transcript,sort_keys=True,ensure_ascii=False).encode()).hexdigest()
        path=self.root/'artifacts'/job['id']/('transcript-'+digest+'.json')
        if not path.exists():atomic_json(path,transcript)
        job['artifact_path']=str(path)
    def replay(self,job):
        now=time.monotonic();key=(job['id'],job['phase'],job.get('publication_id'))
        if now-self.last_replay.get(key,-100)>5:
            self.last_replay[key]=now
            if job['phase']=='publish' and job.get('publication_id'):
                self.emit('publication_ready',{'job_id':job['id'],'publication_id':job['publication_id'],'manifest_path':job['manifest_path']})
            elif job['phase']=='snapshot':
                self.emit('snapshot_requested',{'job_id':job['id'],'source_reference':job['source_reference']})
    def tick(self):
        if self.stopping:return
        now=time.monotonic()
        if now-self.last_power>2:self.power=bool(self.power_check());self.last_power=now
        self.poll_download()
        if self.inventory:
            new_models=self.inventory.snapshot()
            if new_models!=self.models:
                self.models=new_models;self.emit('snapshot',self.snapshot())
        if now-self.last_models>30 and not self.active and not self.download:
            if self.inventory:self.inventory.start()
            else:self.models=self.readiness()
            self.last_models=now
        with self.store.transaction():self.complete_task()
        self.flush_changed()
        paused=self.store.get_meta('paused',False)
        for job in self.store.jobs():
            if job['phase']=='publish' and job.get('publication_id') and job['status']=='saving':self.replay(job)
        if self.active:return
        for job in self.store.jobs():
            if not job['queued'] or job['status']=='failed':continue
            if job.get('refresh_requested'):
                job.update(refresh_resume=job['phase'],refresh_requested=False,phase='snapshot',status='waiting',publication_id=None,manifest_path=None)
                with self.store.transaction():self.save(job)
            if job['phase']=='publish':
                if not job.get('recording_date'):
                    if job['status']!='needs_recording_date':
                        job.update(status='needs_recording_date',wait_reason='recording_date',stage='saving',completed_units=None,total_units=None)
                        with self.store.transaction():self.save(job)
                    continue
                if not job.get('publication_id'):
                    try:
                        transcript=json.loads(Path(job['artifact_path']).read_text())
                        manifest_path=build_publication(self.root,job,transcript,self.model_manifest())
                        manifest=json.loads(Path(manifest_path).read_text())
                        job.update(manifest_path=manifest_path,publication_id=manifest['publication_id'],status='saving',stage='saving',wait_reason='publication',completed_units=None,total_units=None)
                    except (OSError,ValueError,KeyError):job.update(status='failed',queued=False,error=failure('artifact_failed'))
                    with self.store.transaction():self.save(job)
                self.replay(job);continue
            reason='paused' if paused else 'ac_power' if self.store.get_meta('ac_only',True) and not self.power else 'models' if not self.models['models_ready'] else 'source_incomplete' if job['completion_evidence']!='finished' else None
            if reason:
                status='paused' if paused else 'waiting'
                if job['status']!=status or job['wait_reason']!=reason:
                    job.update(status=status,wait_reason=reason)
                    with self.store.transaction():self.save(job)
                continue
            if job['phase']=='snapshot':
                if job['status']!='preparing':
                    job.update(status='preparing',stage='preparing',wait_reason='snapshot',completed_units=None,total_units=None)
                    with self.store.transaction():self.save(job)
                self.replay(job);continue
            try:
                if job['phase']=='prepare':data=job['prepare_data'];op='prepare'
                elif job['phase'] in {'asr','align'}:
                    op=job['phase'];index=len(job['asr_chunks'] if op=='asr' else job['aligned_chunks']);ranges=chunk_ranges(job['frames']);start,end=ranges[index]
                    path=self.root/'work'/job['id']/job['pcm_hash']/f'chunk-{index}.wav';path.parent.mkdir(parents=True,exist_ok=True)
                    if not path.exists():extract_chunk(job['pcm_path'],path,start,end)
                    job.update(completed_units=index,total_units=len(ranges))
                    data={'index':index,'audio_path':str(path),'duration_s':(end-start)/16000}
                    if op=='align':data.update(job['asr_chunks'][index])
                else:
                    op='title';data={'artifact_path':job['artifact_path']}
                    if job.get('title_checkpoint'):data['title_checkpoint']=json.loads(Path(job['title_checkpoint']).read_text())
                    else:job.update(completed_units=None,total_units=None)
                with self.store.transaction():self.launch(job,op,data)
            except (OSError,ValueError,KeyError,IndexError):
                job.update(status='failed',queued=False,error=failure('pipeline_failed'))
                with self.store.transaction():self.save(job)
            break
        self.flush_changed()
    def model_manifest(self):
        try:return json.loads((self.model_root/'models.json').read_text())
        except (OSError,ValueError):return {}
    def poll_download(self):
        if self.download:
            task=self.download['task'];path=self.download['progress']
            try:
                with open(path,'rb') as f:
                    f.seek(self.download['offset'])
                    while True:
                        line=f.readline()
                        if not line or not line.endswith(b'\n'):break
                        self.download['offset']=f.tell()
                        try:event=json.loads(line);self.emit('model_progress',event['payload'])
                        except (ValueError,KeyError):pass
            except OSError:pass
            if task.poll() is not None:
                if task.proc.returncode!=0:self.emit('model_progress',{'model':'all','downloaded_bytes':0,'total_bytes':None,'state':'failed','error':failure('model_download_failed')})
                self.download=None
                if self.inventory:self.inventory.start()
                self.models=self.readiness();self.emit('snapshot',self.snapshot())
        if not self.download and not self.active and self.store.get_meta('download_requested',False):
            with self.store.transaction():self.store.set_meta('download_requested',False)
            directory=self.root/'work'/'downloads'/uuid.uuid4().hex;directory.mkdir(parents=True)
            path=directory/'progress.jsonl'
            with self.lifecycle_lock:
                if self.stopping:return
                with open(path,'wb') as out:task=ManagedProcess([sys.executable,'-m','voice_memo_transcriber.download_models','--model-root',str(self.model_root)],timeout=6*3600,stdout=out)
                if self.stopping:task.stop();return
                self.download={'task':task,'progress':path,'offset':0}
    def request_stop(self):
        # Called by supervisor without touching SQLite, even during blocking disk work.
        with self.lifecycle_lock:
            self.stopping=True
            active=self.active;download=self.download
        if active:active['task'].stop()
        if download:download['task'].stop()
    def close(self):
        if self.closed:return
        self.request_stop()
        if self.active:
            job=self.store.get(self.active['job_id']);job.update(status='waiting',wait_reason='restart')
            with self.store.transaction():self.store.put(job)
        self.store.close();self.closed=True
