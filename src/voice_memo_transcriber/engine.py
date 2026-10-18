"""One-thread durable state machine. Readers and inference never touch SQLite."""
import copy,fcntl,json,math,os,re,stat,subprocess,sys,time,uuid,threading,datetime as dt
from pathlib import Path
from .store import Store,owned_directory,remove_owned_tree,atomic_json_at
from .dates import recording_day,validate_day
from .titles import sanitize_topic
from .protocol import parse_command
from .media import validate_owned,chunk_ranges,extract_chunk,global_segments
from .artifacts import atomic_json,build_publication,valid_receipt,sha256
from .models import model_status,ModelInventory
from .model_catalog import DEFAULT_SELECTION,PROFILES,device_capabilities,recommendations,validate_selection
from .process import ManagedProcess
from .diarization import decode_document,validate_document,rename_speaker,speaker_count
from .diarization_io import sources as speaker_sources,read_owned as speaker_read,commit as speaker_commit
from .diarization_runtime import MESSAGES as SPEAKER_MESSAGES
from .notes import validate_notes,source_chunks,source_digest
from .study_notes import ERROR_CODES,safe_diagnostics

JOB_FIELDS=('id','uuid','topic','recording_date','recorded_at','duration_s','status','wait_reason','stage','completed_units','total_units','elapsed_seconds','artifact_path','audio_path','pcm_path','error','title_status','alignment_status','original_title','notes_path','notes_status','notes_error','notes_protocol','deleted_at','speakers_path','speakers_status','speakers_error')
RUNNING={'preparing','transcribing','aligning','titling','noting'}
RESULT_FIELDS=('model_selection','pcm_hash','frames','duration_s','audio_path','pcm_path','artifact_path','asr_chunks','aligned_chunks','alignment_status','topic','title_status','title_checkpoint','notes_path','notes_status','notes_error','notes_protocol','notes_checkpoint','notes_only','notes_prior_status','publication_id','manifest_path','receipt','speakers_path','speakers_status','speakers_error')

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
        self.selection=self.store.get_meta('model_selection',dict(DEFAULT_SELECTION))
        self.device=device_capabilities()
        self.inventory=ModelInventory(self.model_root,self.selection) if readiness is None else None
        self.readiness=readiness or self.inventory.snapshot;self.models=self.readiness()
        self.last_power=float("-inf");self.power=False;self.last_models=float("-inf");self.last_replay={};self.started={};self.changed=set();self.last_model_change_reason=object()
        # In-flight work was never committed. Recover the same phase/chunk.
        with self.store.transaction():
            for job in self.store.jobs():
                # Legacy checkpoints always used the original pinned profiles.
                if not job.get('deleted_at') and 'model_selection' not in job and job.get('status') not in {'completed','needs_recording_date'} and (job.get('phase') in {'prepare','asr','align','title','notes'} or job.get('pcm_hash')):
                    job['model_selection']=dict(DEFAULT_SELECTION);self.store.put(job)
                if job.get('speakers_status') in {'queued','analyzing'}:
                    job.update(speakers_status='cancelled',speakers_error=None);self.store.put(job)
                if job.get('deleted_at'):
                    job['queued']=False;self.store.put(job)
                elif job['phase']=='snapshot' and job['queued'] and job.get('snapshot_request_id'):
                    self.invalidate(job);job.update(status='waiting',wait_reason='restart');self.store.put(job)
                elif job['status'] in RUNNING:
                    job.update(status='waiting',wait_reason='restart',stage=job['phase']);self.store.put(job)
        # Intent was committed before any deletion. Resume once, retaining failed
        # intents for an explicit retry rather than retrying on every engine tick.
        for job in self.store.jobs():
            if job.get('purge_requested_at') and not job.get('purged_at'):self.finish_purge(job['id'])
        self.flush_changed()
    def emit(self,kind,payload):
        self.sequence+=1;self.emit_callback({'sequence':self.sequence,'protocol_version':1,'kind':kind,'payload':payload})
    def view(self,job):
        view={key:job.get(key) for key in JOB_FIELDS}
        view['refresh_pending']=bool(job.get('refresh_requested') or job.get('refresh_state'))
        view['purge_pending']=bool(job.get('purge_requested_at') and not job.get('purged_at'))
        view['elapsed_seconds']=job.get('elapsed_seconds',0)+(time.monotonic()-self.started[job['id']] if job['id'] in self.started else 0)
        return view
    def save(self,job):
        self.store.put(job);self.changed.add(job['id'])
    def flush_changed(self):
        ids=self.changed;self.changed=set()
        for jid in sorted(ids):
            job=self.store.get(jid)
            if job.get('purged_at'):self.emit('job_removed',{'job_id':jid})
            else:self.emit('job_changed',{'job':self.view(job)})
        reason=self.model_change_reason()
        if reason!=self.last_model_change_reason:
            self.last_model_change_reason=reason;self.emit('engine_status',self.engine_status())
    def model_change_reason(self):
        if self.download or self.store.get_meta('download_requested',False):return 'download_active'
        if self.active or any(not j.get('deleted_at') and j.get('queued') and j['status'] not in {'completed','needs_recording_date','failed'} and j['phase'] not in {'snapshot','publish'} for j in self.store.jobs()):return 'processing'
        return None
    def job_selection(self,job):
        return job.get('model_selection') or (DEFAULT_SELECTION if job.get('pcm_hash') or job.get('artifact_path') or job.get('notes_checkpoint') else self.selection)
    def job_model_status(self,job):
        profiles=self.models.get('profile_status')
        if profiles is None:return self.models  # Embedders may supply legacy readiness.
        selection=self.job_selection(job)
        selected={role:profiles.get(identifier,{'state':'missing_or_invalid'}) for role,identifier in selection.items()}
        states={**self.models.get('model_status',{}),**selected}
        return {**self.models,'model_status':states,'models_ready':all(states.get(n,{}).get('state')=='ready' for n in ('asr','aligner','title')),'notes_ready':selected['notes']['state']=='ready'}
    def engine_status(self):
        reason=self.model_change_reason()
        profiles=self.models.get('profile_status',{})
        catalog=[dict(id=identifier,**{k:v for k,v in p.items() if k!='storage'},supported=self.device['mlx_supported'] and p.get('selectable',True),disabled_reason='notes_schema_validation_failed' if not p.get('selectable',True) else None if self.device['mlx_supported'] else 'unsupported_model_backend',state=profiles.get(identifier,{}).get('state','verifying')) for identifier,p in PROFILES.items()]
        preparation={role:profiles.get(identifier,self.models.get('model_status',{}).get(role,{})).get('state','verifying') for role,identifier in self.selection.items()}
        return {**self.models,'paused':self.store.get_meta('paused',False),'ac_only':self.store.get_meta('ac_only',True),
                'model_catalog':catalog,'model_selection':{**self.selection,'can_change':reason is None,'disabled_reason':reason,'preparation':preparation},'model_recommendations':recommendations(self.device)}
    def snapshot(self):
        return {'jobs':[self.view(j) for j in self.store.jobs() if not j.get('purged_at')],'engine_status':self.engine_status()}
    def command(self,command):
        # Also validate direct embedding callers; service parses byte limits first.
        self.changed=set()
        try:
            command=parse_command(json.dumps(command,ensure_ascii=False).encode())
            result=self.store.request(command['request_id'],command,lambda:self.apply(command['kind'],command['payload']))
            if command['kind']=='purge_recording' and (result['ok'] or result.get('error',{}).get('code')=='purge_cleanup_failed'):
                result=self.finish_purge(command['payload']['job_id'],command['request_id'])
        except (ValueError,TypeError,KeyError,OverflowError) as exc:
            self.changed=set();result={'ok':False,'error':failure('invalid_command',str(exc) if isinstance(exc,ValueError) else 'invalid payload')}
        selected=self.store.get_meta('model_selection',dict(DEFAULT_SELECTION))
        if selected!=self.selection:
            self.selection=selected
            if self.inventory:
                self.inventory.selection=dict(self.selection)
                self.models=self.readiness()
        self.flush_changed()
        if command.get('kind') in {'hello','snapshot'} and result['ok']:self.emit('snapshot',self.snapshot())
        if command.get('kind') in {'pause','resume','set_policy','set_models','download_models'} and result['ok']:self.emit('engine_status',self.engine_status())
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
        metadata=memo.get('metadata_revision')
        if metadata is not None and (not isinstance(metadata,str) or not 0<len(metadata)<=256 or any(ord(c)<32 for c in metadata)):raise ValueError('invalid_metadata_revision')
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
                if old.get('purge_requested_at') or old.get('purged_at'):continue
                previous=old.get('media_revision');revision=memo.get('media_revision');prior_day=old.get('recording_date')
                metadata=memo.get('metadata_revision');duration=memo.get('duration_s')
                changed=(revision is not None and previous is not None and revision!=previous
                         or metadata is not None and old.get('metadata_revision') is not None and metadata!=old['metadata_revision']
                         or duration is not None and old.get('source_duration_s') is not None and abs(duration-old['source_duration_s'])>.05
                         or duration is not None and old.get('pcm_hash') and old.get('checked_source_duration_s') is None and not old.get('refresh_state') and old['status']!='failed' and abs(duration-old['duration_s'])>.05)
                old.update(source_reference=memo['source_reference'],completion_evidence=memo['completion_evidence'],original_title=memo.get('original_title'))
                # Unavailable iCloud media/metadata must not erase the last known identity.
                if revision is not None:old['media_revision']=revision
                if metadata is not None:old['metadata_revision']=metadata
                if duration is not None:
                    old['source_duration_s']=duration
                    if not old.get('pcm_hash'):old['duration_s']=duration
                if old.get('date_provenance')!='user_supplied':
                    if memo.get('recorded_at') is not None:
                        old.update(recorded_at=memo['recorded_at'],recording_date=day,date_provenance=memo.get('recorded_at_source','unknown'),original_timezone=memo.get('original_timezone'))
                if changed and (old['queued'] or old.get('pcm_hash') or old.get('deleted_at')):
                    self.request_refresh(old,source_changed=True)
                if old.get('recording_date')!=prior_day and old.get('artifact_path') and old.get('topic'):
                    old.update(revision=old['revision']+1,publication_id=None)
                    if not old.get('deleted_at'):
                        old['queued']=True
                        if old['phase']=='publish':old.update(status='waiting',wait_reason=None)
                if explicit and not old.get('pcm_hash') and not old.get('deleted_at'):old['queued']=True
                if old.get('deleted_at'):old['queued']=False
                self.save(old);continue
            job={key:None for key in JOB_FIELDS}
            recorded=memo.get('recorded_at')
            queued=explicit or (baseline and cutoff is not None and recorded is not None and dt.datetime.fromisoformat(recorded.replace('Z','+00:00')).timestamp()>cutoff)
            job.update(id=uuid.uuid4().hex,uuid=memo['uuid'],recorded_at=memo.get('recorded_at'),recording_date=day,date_provenance=memo.get('recorded_at_source','unknown'),original_timezone=memo.get('original_timezone'),duration_s=memo.get('duration_s'),original_title=memo.get('original_title'),status='waiting' if queued else 'unprocessed',stage='snapshot',wait_reason=None,elapsed_seconds=0,notes_status='pending',title_status='pending',alignment_status='pending',queued=queued,phase='snapshot',source_reference=memo['source_reference'],completion_evidence=memo['completion_evidence'],source_kind=memo['source_kind'],media_revision=memo.get('media_revision'),metadata_revision=memo.get('metadata_revision'),source_duration_s=memo.get('duration_s'),pcm_hash=None,revision=0,generation=0,asr_chunks=[],aligned_chunks=[],attempts={},refresh_requested=False,publication_id=None,manifest_path=None)
            self.save(job)
        if not explicit:self.store.set_meta('baseline_seen',True)
    def invalidate(self,job):
        job['generation']=job.get('generation',0)+1
        job.update(snapshot_request_id=None,snapshot_token_required=True)
    def stop_job(self,job):
        if job.get('speakers_status') in {'queued','analyzing'}:
            job.update(speakers_status='cancelled',speakers_error=None)
        if self.active and self.active['job_id']==job['id']:
            active=self.active;active['task'].stop();self.active=None
            job['elapsed_seconds']+=time.monotonic()-self.started.pop(job['id'],time.monotonic())
    def request_refresh(self,job,source_changed=False):
        # Repeated manual requests share one source check; new source evidence supersedes it.
        if not source_changed and (job.get('refresh_requested') or job.get('refresh_state')):return
        if not job.get('refresh_state'):
            job['refresh_state']={key:job.get(key) for key in ('phase','status','queued','stage','wait_reason','revision')}
            job['refresh_previous']=copy.deepcopy({key:job[key] for key in RESULT_FIELDS if key in job})
        if job['phase'] not in {'snapshot','prepare'}:job['refresh_resume']=job['phase']
        self.invalidate(job);self.stop_job(job)
        job.update(refresh_requested=True,publication_id=None,status='waiting',wait_reason=None,error=None,attempts={})
        job['queued']=not bool(job.get('deleted_at'))
    def refresh_failed(self,job,error):
        previous=job.pop('refresh_previous',None)
        if previous is not None:
            for key in RESULT_FIELDS:job.pop(key,None)
            job.update(previous)
        job.pop('refresh_state',None)
        job.pop('refresh_resume',None)
        job.update(status='failed',phase='snapshot',stage='snapshot',queued=False,wait_reason=None,error=error,refresh_requested=False)
    def finish_purge(self,jid,rid=None):
        """Run filesystem effects only after Store.request committed the intent."""
        if self.store.db.in_transaction:raise RuntimeError('purge_requires_committed_intent')
        job=self.store.get(jid)
        result={'ok':True}
        if not job.get('purged_at'):
            if not job.get('purge_requested_at') or not job.get('deleted_at'):raise ValueError('purge_not_requested')
            try:
                active=self.active if self.active and self.active['job_id']==jid else None
                if active:
                    active['task'].stop()
                    if active['task'].poll() is None:raise ValueError('purge_worker_still_running')
                self.stop_job(job)
                self.cleanup_purge(job)
            except (OSError,ValueError,TypeError,KeyError,RecursionError):
                error=failure('purge_cleanup_failed')
                job.update(queued=False,error=error)
                result={'ok':False,'error':error}
            else:
                job={key:job[key] for key in ('id','uuid','source_kind','source_reference','deleted_at','generation')}
                job.update(purged_at=dt.datetime.now(dt.timezone.utc).isoformat(),queued=False)
        with self.store.transaction():
            self.save(job)
            if rid is not None:self.store.set_request_result(rid,result)
        return result
    def cleanup_purge(self,job):
        # Stored audio/artifact/receipt paths are never deletion authority.
        if not re.fullmatch(r'[0-9a-f]{32}',job['id']):raise ValueError('unsafe_purge_job_id')
        if job.get('source_kind')=='import':self.purge_import_copy(job)
        for parts in (('work',job['id']),('artifacts',job['id']),('snapshots','jobs',job['id'])):
            remove_owned_tree(self.root,parts)
    def purge_import_copy(self,job):
        with owned_directory(self.root,('native',)) as native:
            lock=os.open('imports.lock',os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK,0o600,dir_fd=native)
            try:
                if not stat.S_ISREG(os.fstat(lock).st_mode):raise ValueError('unsafe_import_lock')
                fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
                fd=os.open('imports.json',os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK,dir_fd=native)
                with os.fdopen(fd,'rb') as source:
                    before=os.fstat(source.fileno())
                    if not stat.S_ISREG(before.st_mode) or before.st_size>8*1024*1024:raise ValueError('invalid_import_registry')
                    registry=json.loads(source.read(8*1024*1024+1))
                    after=os.fstat(source.fileno())
                    if (before.st_ino,before.st_size,before.st_mtime_ns)!=(after.st_ino,after.st_size,after.st_mtime_ns):raise ValueError('changing_import_registry')
                if (not isinstance(registry,dict) or type(registry.get('schema_version')) is not int
                        or registry['schema_version']!=1 or not isinstance(registry.get('entries'),dict)):
                    raise ValueError('invalid_import_registry')
                entry=registry['entries'].get(job['source_reference'])
                if not isinstance(entry,dict) or not isinstance(entry.get('memo'),dict):raise ValueError('missing_import_identity')
                memo=entry['memo']
                if (memo.get('uuid')!=job['uuid'] or memo.get('source_kind')!='import'
                        or memo.get('source_reference')!=job['source_reference']
                        or not isinstance(entry.get('path'),str) or type(entry.get('delivered')) is not bool
                        or not isinstance(entry.get('hash'),str) or not re.fullmatch(r'[0-9a-f]{64}',entry['hash'])
                        or 'purged' in entry and type(entry['purged']) is not bool):raise ValueError('invalid_import_identity')
                path=Path(entry['path'])
                if not path.is_absolute() or '..' in path.parts or path.parent!=self.root/'snapshots'/'imports':raise ValueError('unsafe_import_copy')
                if any(other!=job['source_reference'] and isinstance(value,dict) and value.get('path')==entry['path']
                       for other,value in registry['entries'].items()):raise ValueError('shared_import_copy')
                with owned_directory(self.root,('snapshots','imports')) as copies:
                    try:before=os.stat(path.name,dir_fd=copies,follow_symlinks=False)
                    except FileNotFoundError:before=None
                    if before is not None and (not stat.S_ISREG(before.st_mode) or before.st_dev!=os.fstat(copies).st_dev):raise ValueError('unsafe_import_copy')
                    # Preserve Codable Memo fields and the content hash; native uses
                    # this durable flag to suppress reimports and late snapshots.
                    entry['purged']=True
                    memo['original_title']=None
                    atomic_json_at(native,'imports.json',registry)
                    if before is not None:
                        after=os.stat(path.name,dir_fd=copies,follow_symlinks=False)
                        if (before.st_dev,before.st_ino)!=(after.st_dev,after.st_ino):raise ValueError('changing_import_copy')
                        os.unlink(path.name,dir_fd=copies)
                    os.fsync(copies)
            finally:os.close(lock)
    def apply(self,kind,payload):
        if kind in {'hello','snapshot','shutdown'}:return {'ok':True}
        if kind=='discover':self.discover(payload['memos']);return {'ok':True}
        if kind=='import':self.discover([payload['memo']],True);return {'ok':True}
        if kind in {'pause','resume'}:
            self.store.set_meta('paused',kind=='pause')
            if kind=='pause' and self.active and self.active['op']=='diarize':
                job=self.store.get(self.active['job_id']);self.stop_job(job);self.save(job)
            if kind=='pause' and self.active and self.active['op']=='notes':
                active=self.active;job=self.store.get(active['job_id'])
                if job.get('notes_protocol')=='study_v1':
                    self.invalidate(job)
                    active['task'].stop();self.active=None
                    job['elapsed_seconds']+=time.monotonic()-self.started.pop(job['id'],time.monotonic())
                    job.update(status='paused',wait_reason='paused',completed_units=None,total_units=None)
                    self.save(job)
            return {'ok':True}
        if kind=='set_policy':
            if type(payload.get('ac_only')) is not bool:raise ValueError('ac_only_must_be_boolean')
            self.store.set_meta('ac_only',payload['ac_only']);return {'ok':True}
        if kind=='set_models':
            if self.model_change_reason():return {'ok':False,'error':failure('models_busy',self.model_change_reason())}
            try:selected=validate_selection(payload,self.device)
            except ValueError as exc:return {'ok':False,'error':failure(str(exc))}
            self.store.set_meta('model_selection',selected)
            return {'ok':True}
        if kind=='download_models':
            notes_only=payload.get('notes_only',False)
            speakers_only=payload.get('speakers_only',False)
            if type(speakers_only) is not bool or speakers_only and notes_only:raise ValueError('invalid_download_scope')
            if type(notes_only) is not bool:raise ValueError('notes_only_must_be_boolean')
            if self.download:return {'ok':False,'error':failure('models_busy','download_active')}
            try:validate_selection(self.selection,self.device)
            except ValueError as exc:return {'ok':False,'error':failure(str(exc))}
            self.store.set_meta('download_model_selection',dict(self.selection))
            self.store.set_meta('download_notes_only',notes_only)
            self.store.set_meta('download_speakers_only',speakers_only)
            self.store.set_meta('download_requested',True);return {'ok':True}
        if kind in {'enqueue','backfill'}:
            ids=payload['uuids']
            if not isinstance(ids,list) or not all(isinstance(v,str) for v in ids):raise ValueError('invalid_uuids')
            for uid in ids:
                job=self.store.by_uuid(uid)
                if not job:raise ValueError('unknown_uuid')
                if not job.get('deleted_at') and job['status']=='unprocessed':job.update(queued=True,status='waiting',wait_reason=None);self.save(job)
            return {'ok':True}
        job=self.store.get(payload['job_id'])
        if kind=='purge_recording':
            if job.get('purged_at'):return {'ok':True}
            if not job.get('deleted_at'):raise ValueError('recording_not_trashed')
            if not job.get('purge_requested_at'):
                self.invalidate(job)
                job.update(purge_requested_at=dt.datetime.now(dt.timezone.utc).isoformat(),queued=False,refresh_requested=False,publication_id=None)
                self.save(job)
            return {'ok':True}
        if job.get('purged_at'):raise ValueError('recording_purged')
        if job.get('purge_requested_at'):raise ValueError('recording_purge_pending')
        if kind=='trash_recording':
            if not job.get('deleted_at'):
                job['trash_state']={key:job.get(key) for key in ('status','queued','wait_reason','stage')}
                self.invalidate(job);self.stop_job(job)
                job.update(deleted_at=dt.datetime.now(dt.timezone.utc).isoformat(),queued=False,wait_reason=None)
                self.save(job)
            return {'ok':True}
        if kind=='restore_recording':
            if job.get('deleted_at'):
                state=job.pop('trash_state',{})
                self.invalidate(job);job.update(deleted_at=None,**state)
                if job.get('refresh_requested'):job.update(queued=True,status='waiting',wait_reason=None)
                if state.get('status')=='unprocessed' and not state.get('queued'):
                    job.update(status='unprocessed',queued=False,wait_reason=None,refresh_requested=False)
                    job.pop('refresh_state',None);job.pop('refresh_previous',None);job.pop('refresh_resume',None)
                elif job['status'] in RUNNING:job.update(status='waiting',wait_reason='restart')
                # A pending publication callback from before trash cannot acknowledge restore.
                if job['status']!='completed' and job['phase']=='publish':
                    job.update(revision=job['revision']+1,publication_id=None,status='waiting')
                self.save(job)
            return {'ok':True}
        if job.get('deleted_at'):raise ValueError('recording_trashed')
        if kind=='cancel_speakers':
            if self.active and self.active['job_id']==job['id'] and self.active['op']=='diarize':self.stop_job(job)
            job.update(speakers_status='cancelled',speakers_error=None)
            self.save(job);return {'ok':True}
        if kind in {'analyze_speakers','rename_speaker'}:
            if not job.get('artifact_path') or not job.get('audio_path'):raise ValueError('transcript_not_ready')
            if job.get('refresh_requested') or job.get('refresh_state') or job['status'] not in {'completed','needs_recording_date','failed'} or job.get('queued'):
                raise ValueError('job_busy')
            if job.get('speakers_status') in {'queued','analyzing'}:raise ValueError('speaker_analysis_busy')
            if kind=='analyze_speakers':
                if not job.get('pcm_path'):raise ValueError('transcript_not_ready')
                count=payload.get('num_speakers')
                speaker_count(count)
                # Explicit optional intent. It does not change the transcription queue.
                job.update(speakers_status='queued',speakers_error=None,speakers_num_speakers=count)
            else:
                audio_hash,transcript=speaker_sources(self.root,job['audio_path'],job['artifact_path'])
                doc=decode_document(speaker_read(self.root,job['speakers_path']))
                if payload.get('generation')!=doc['generation']:raise ValueError('stale_speaker_generation')
                doc=rename_speaker(doc,payload.get('speaker_id'),payload.get('name'),audio_hash,transcript['text'])
                job.update(speakers_path=speaker_commit(self.root,job['artifact_path'],doc),speakers_status='completed',speakers_error=None)
            self.save(job);return {'ok':True}
        if kind=='refresh_recording':
            self.request_refresh(job);self.save(job);return {'ok':True}
        if kind=='snapshot_ready':
            if job['phase']!='snapshot' or not job['queued'] or job.get('refresh_requested'):raise ValueError('snapshot_not_requested')
            token=job.get('snapshot_request_id')
            if ((token is not None and payload.get('snapshot_request_id')!=token)
                    or token is None and (job.get('snapshot_token_required') or payload.get('snapshot_request_id') is not None)):
                raise ValueError('stale_snapshot')
            if 'error' in payload:
                job.update(status='waiting',wait_reason='source_copy',error=self.safe_error(payload['error']));self.save(job);return {'ok':True}
            before,after=payload['revision_before'],payload['revision_after'];self.validate_revision(before);self.validate_revision(after)
            if before!=after:raise ValueError('snapshot_changed')
            path=validate_owned(self.root,payload['owned_path'])
            st=path.stat()
            if st.st_size!=after['size'] or st.st_mtime_ns!=after['mtime_ns']:raise ValueError('snapshot_changed')
            candidate=self.root/'work'/job['id']/'candidates'/uuid.uuid4().hex;candidate.mkdir(parents=True,exist_ok=True)
            job.update(phase='prepare',status='waiting',wait_reason=None,snapshot_request_id=None,prepare_data={'source':str(path),'revision_before':before,'revision_after':after,'audio_path':str(candidate/('audio'+path.suffix)),'pcm_path':str(candidate/'pcm.wav')})
            self.save(job);return {'ok':True}
        if kind=='generate_notes':
            if not job.get('artifact_path'):raise ValueError('transcript_not_ready')
            if (self.active and self.active['job_id']==job['id']) or job['phase'] in {'notes','prepare','snapshot','asr','align','title'} and job['status'] not in {'completed','needs_recording_date','failed'}:raise ValueError('job_busy')
            validate_owned(self.root,job['artifact_path'])
            job.pop('notes_checkpoint',None)
            job['model_selection']={**(job.get('model_selection') or DEFAULT_SELECTION),'notes':self.selection['notes']}
            if not job.get('topic'):job.update(topic='음성 녹음',title_status='fallback')
            job.update(notes_protocol='study_v1',notes_only=True,notes_prior_status=job['status'],notes_status='pending',notes_error=None,phase='notes',stage='noting',queued=True,status='waiting',wait_reason=None,error=None,attempts={},completed_units=None,total_units=None)
            self.save(job);return {'ok':True}
        if kind=='retry':
            self.stop_job(job);self.invalidate(job)
            job.update(queued=True,error=None,wait_reason=None,status='waiting',attempts={})
            if job['phase']=='publish':job['last_publish_error']=None
            self.save(job);return {'ok':True}
        if kind in {'retitle','set_recording_date'}:
            if kind=='retitle' and 'topic' in payload and not job.get('artifact_path'):
                if self.active and self.active['job_id']==job['id']:raise ValueError('job_busy')
                job.update(topic=sanitize_topic(payload['topic']),title_status='manual')
                if job.get('refresh_previous') is not None:
                    job['refresh_previous'].update(topic=job['topic'],title_status='manual')
                self.save(job);return {'ok':True}
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
                if job.get('refresh_previous') is not None:
                    job['refresh_previous'].update(topic=job['topic'],title_status='manual')
            elif payload.get('generate') is True:job.update(title_status='pending');job.pop('title_checkpoint',None);phase='title'
            else:raise ValueError('topic_or_generate_required')
            if job['phase'] in {'asr','align','title','notes','prepare','snapshot'}:phase=job['phase']
            job.update(revision=job['revision']+1,publication_id=None,manifest_path=None,phase=phase,queued=True,status='waiting',wait_reason=None,error=None,attempts={})
            self.save(job);return {'ok':True}
        if kind=='publish_ack':
            if job['phase']!='publish' or job.get('refresh_requested') or job.get('refresh_state') or payload['publication_id']!=job.get('publication_id'):raise ValueError('stale_publication')
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
        if 'model_selection' not in job:job['model_selection']=dict(self.job_selection(job))
        request={'model_selection':dict(job['model_selection']),'op':op,'uuid':job['uuid'],'state_root':str(self.root),'model_root':str(self.model_root),'ffmpeg_path':self.ffmpeg_path,**data}
        req=directory/'request.json';out=directory/'result.json';atomic_json(req,request)
        env=os.environ.copy();env.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_DATASETS_OFFLINE='1',TOKENIZERS_PARALLELISM='false',OMP_NUM_THREADS='2',OPENBLAS_NUM_THREADS='2',VECLIB_MAXIMUM_THREADS='2',MKL_NUM_THREADS='2')
        with self.lifecycle_lock:
            if self.stopping:return
            task=ManagedProcess([*self.worker_command,str(req),str(out)],timeout=self.worker_timeout,env=env)
            if self.stopping:task.stop();return
            self.active={'job_id':job['id'],'op':op,'task':task,'output':out,'index':data.get('index'),'generation':job.get('generation',0),'pcm_hash':job.get('pcm_hash')}
        self.started[job['id']]=time.monotonic()
        if op=='diarize':
            job.update(speakers_status='analyzing',speakers_error=None);self.save(job);return
        job.update(status={'prepare':'preparing','asr':'transcribing','align':'aligning','title':'titling','notes':'noting'}[op],stage={'prepare':'preparing','asr':'transcribing','align':'aligning','title':'titling','notes':'noting'}[op],wait_reason=None,error=None)
        self.save(job)
    def complete_task(self):
        active=self.active
        if active is None or active['task'].poll() is None:return
        self.active=None;job=self.store.get(active['job_id']);op=active['op']
        if op=='diarize':
            self.complete_speakers(job,active);return
        if (job.get('deleted_at') or job.get('refresh_requested') or active.get('generation')!=job.get('generation',0)
                or active.get('pcm_hash')!=job.get('pcm_hash') or job['phase']!={'prepare':'prepare','asr':'asr','align':'align','title':'title','notes':'notes'}[op]):
            self.started.pop(job['id'],None);return
        job['elapsed_seconds']+=time.monotonic()-self.started.pop(job['id'],time.monotonic())
        try:
            if active['task'].proc.returncode!=0:raise ValueError('model_timeout' if active['task'].timed_out else 'worker_failed')
            result=decode_document(active['output'].read_bytes())
            if not isinstance(result,dict):raise ValueError('invalid_worker_result')
            self.accept(job,op,result,active['index'])
            job['attempts'].pop(op,None)
        except (OSError,ValueError,TypeError,KeyError) as exc:
            attempts=job['attempts'].get(op,0)+1;job['attempts'][op]=attempts
            if attempts<2:job.update(status='waiting',wait_reason='retry',error=failure('worker_failed'))
            elif op=='align':
                job.update(alignment_status='failed',aligned_chunks=[],phase=self.after_title(job) if job['title_status']=='manual' else 'title',status='waiting',error=failure('alignment_failed'))
                self.write_transcript(job)
            elif op=='title':job.update(topic='음성 녹음',title_status='fallback',phase=self.after_title(job),status='waiting',error=failure('title_failed'))
            elif op=='notes':self.fail_notes(job,failure('notes_failed'))
            else:
                error=failure('worker_timeout' if active['task'].timed_out else 'worker_failed')
                if job.get('refresh_previous') is not None:self.refresh_failed(job,error)
                else:job.update(status='failed',wait_reason=None,queued=False,error=error)
        self.save(job)
    def complete_speakers(self,job,active):
        self.started.pop(job['id'],None)
        if (job.get('deleted_at') or job.get('refresh_requested') or job.get('refresh_state')
                or job.get('speakers_status')!='analyzing' or active.get('generation')!=job.get('generation',0)
                or active.get('pcm_hash')!=job.get('pcm_hash')):return
        try:
            if active['task'].proc.returncode!=0:raise ValueError('diarization_failed')
            result=decode_document(speaker_read(self.root,active['output']))
            if result.get('speaker_error'):
                code=result['speaker_error']
                raise ValueError(code if code in SPEAKER_MESSAGES else 'diarization_failed')
            audio_hash,transcript=speaker_sources(self.root,job['audio_path'],job['artifact_path'])
            doc=validate_document(result['speakers'],audio_hash,transcript['text'])
            projection=[{k:s[k] for k in ('start_s','end_s','text')} for s in doc['segments']]
            original=[{k:s[k] for k in ('start_s','end_s','text')} for s in transcript['segments']]
            if projection!=original:raise ValueError('diarization_failed')
            if any(s['end_s']>job['duration_s'] for s in doc['turns']+doc['segments']):raise ValueError('diarization_failed')
            job.update(speakers_path=speaker_commit(self.root,job['artifact_path'],doc),speakers_status='completed',speakers_error=None)
        except (OSError,ValueError,TypeError,KeyError,AttributeError):
            code=locals().get('code','diarization_failed')
            if code not in SPEAKER_MESSAGES:code='diarization_failed'
            job.update(speakers_status='failed',speakers_error=failure(code,SPEAKER_MESSAGES[code]))
        self.save(job)

    def accept(self,job,op,result,index):
        if op=='prepare':
            h=result['pcm_hash']
            if not isinstance(h,str) or len(h)!=64 or any(c not in '0123456789abcdef' for c in h):raise ValueError('invalid_pcm_hash')
            for key in ('audio_path','pcm_path'):validate_owned(self.root,result[key])
            if type(result['frames']) is not int or result['frames']<1:raise ValueError('invalid_frames')
            duration=result['duration_s']
            if type(duration) not in (int,float) or not math.isfinite(duration) or duration<=0:raise ValueError('invalid_duration')
            job['checked_source_duration_s']=job.get('source_duration_s')
            if h==job.get('pcm_hash'):
                # Metadata/container-only changes retain all committed model results.
                resume=job.pop('refresh_resume',None)
                if resume not in {'asr','align','title','notes','publish'}:
                    resume='publish' if job.get('topic') and job.get('artifact_path') else 'title' if job.get('alignment_status') in {'completed','failed'} else 'align' if len(job['asr_chunks'])==len(chunk_ranges(job['frames'])) else 'asr'
                state=job.get('refresh_state',{})
                previous=job.get('refresh_previous',{})
                if resume=='title' and job.get('title_status')=='manual':resume=self.after_title(job)
                if state.get('status')=='completed' and state.get('revision')==job['revision'] and previous.get('pcm_hash')==h:
                    job.update(publication_id=previous.get('publication_id'),manifest_path=previous.get('manifest_path'),phase='publish',status='completed',queued=False,wait_reason=None,error=None)
                else:job.update(phase=resume,status='waiting',wait_reason=None,error=None)
                # If an earlier check already selected new PCM, keep its rollback
                # until the replacement transcript/notes finish successfully.
                if previous.get('pcm_hash')==h or not previous:
                    job.pop('refresh_state',None);job.pop('refresh_previous',None)
                return
            job.pop('title_checkpoint',None)
            job.pop('notes_checkpoint',None)
            job.pop('notes_only',None)
            job.pop('notes_prior_status',None);job.pop('refresh_resume',None)
            manual=job.get('title_status')=='manual'
            protocol='study_v1' if job.get('pcm_hash') else job.get('notes_protocol') or 'study_v1'
            job.update(notes_status='pending',notes_path=None,notes_error=None,notes_protocol=protocol,pcm_hash=h,audio_path=result['audio_path'],pcm_path=result['pcm_path'],duration_s=float(duration),frames=result['frames'],asr_chunks=[],aligned_chunks=[],topic=job.get('topic') if manual else None,artifact_path=None,publication_id=None,revision=job['revision']+1,title_status='manual' if manual else 'pending',alignment_status='pending',phase='asr',status='waiting',completed_units=0,total_units=len(chunk_ranges(result['frames'])))
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
                job.update(phase=self.after_title(job) if job['title_status']=='manual' else 'title',alignment_status='completed');self.write_transcript(job)
        elif op=='title':
            if 'title_checkpoint' in result:
                checkpoint=result['title_checkpoint']
                if not isinstance(checkpoint,dict) or not isinstance(checkpoint.get('chunks'),list) or not isinstance(checkpoint.get('hints'),list):raise ValueError('invalid_title_checkpoint')
                checkpoint_path=self.root/'work'/job['id']/('title-checkpoint-'+uuid.uuid4().hex+'.json')
                atomic_json(checkpoint_path,checkpoint)
                job.update(title_checkpoint=str(checkpoint_path),status='waiting',completed_units=result['completed_units'],total_units=result['total_units'])
                return
            if not isinstance(result.get('topic'),str) or not result['topic'].strip():raise ValueError('empty_title')
            job.update(topic=sanitize_topic(result['topic']),title_status='completed',phase=self.after_title(job),status='waiting',completed_units=None,total_units=None)
        elif op=='notes':
            source=json.loads(validate_owned(self.root,job['artifact_path']).read_text(encoding='utf-8'))['text']
            protocol=(job.get('notes_protocol') or ('legacy' if job.get('notes_checkpoint') else 'study_v1'))
            if result.get('notes_error_code') is not None:
                code=result['notes_error_code']
                if code not in ERROR_CODES or 'notes' in result or 'notes_checkpoint' in result:
                    raise ValueError('invalid_notes_error')
                error=failure(code)
                if 'notes_diagnostics' in result:error['diagnostics']=safe_diagnostics(result['notes_diagnostics'])
                self.fail_notes(job,error);return
            if protocol=='study_v1':
                if (result.get('notes_protocol')!='study_v1' or 'notes_checkpoint' in result
                        or 'completed_units' not in result or result['completed_units'] is not None
                        or 'total_units' not in result or result['total_units'] is not None):
                    raise ValueError('invalid_study_notes_progress')
                expected=None;total=None
            elif protocol=='legacy':
                total=len(source_chunks(source));expected=index+1
                if type(result.get('completed_units')) is not int or result['completed_units']!=expected or type(result.get('total_units')) is not int or result['total_units']!=total:raise ValueError('invalid_notes_progress')
                if 'notes_checkpoint' in result:
                    checkpoint=result['notes_checkpoint']
                    if not isinstance(checkpoint,dict) or checkpoint.get('index')!=expected or expected>=total or checkpoint.get('total_units')!=total or checkpoint.get('source_sha256')!=source_digest(source):raise ValueError('invalid_notes_checkpoint')
                    checkpoint['notes']=validate_notes(checkpoint.get('notes'),source)
                    path=self.root/'work'/job['id']/('notes-checkpoint-'+uuid.uuid4().hex+'.json')
                    atomic_json(path,checkpoint)
                    job.update(notes_checkpoint=str(path),notes_protocol='legacy',status='waiting',completed_units=expected,total_units=total)
                    return
                if expected!=total:raise ValueError('incomplete_notes_generation')
            else:raise ValueError('unknown_notes_protocol')
            notes=validate_notes(result['notes'],source)
            digest=__import__('hashlib').sha256(json.dumps(notes,sort_keys=True,ensure_ascii=False).encode('utf-8')).hexdigest()
            path=self.root/'artifacts'/job['id']/('notes-'+digest+'.json')
            if not path.exists():atomic_json(path,notes)
            else:
                if json.loads(path.read_text(encoding='utf-8'))!=notes:raise ValueError('local_artifact_conflict')
            job.update(notes_path=str(path),notes_status='completed',notes_error=None,phase='publish',status='waiting',publication_id=None,manifest_path=None,completed_units=expected,total_units=total,error=None)
            job.pop('notes_only',None);job.pop('notes_prior_status',None);job.pop('notes_checkpoint',None)
            job.pop('refresh_state',None);job.pop('refresh_previous',None)
    def after_title(self,job):
        if job.get('notes_status')=='pending' and not job.get('notes_checkpoint'):job['notes_protocol']=job.get('notes_protocol') or 'study_v1'
        return 'notes' if job.get('notes_status')=='pending' else 'publish'
    def fail_notes(self,job,error):
        # A failed regeneration keeps the last good notes and immutable publication.
        previous=job.pop('notes_prior_status',None);note_only=job.pop('notes_only',False)
        job.update(notes_status='failed',notes_error=error,phase='publish',stage='saving',status='waiting',error=None,wait_reason=None,completed_units=None,total_units=None)
        if note_only and previous=='completed' and job.get('publication_id'):
            job.update(status='completed',queued=False)
        job.pop('refresh_state',None);job.pop('refresh_previous',None)
    def write_transcript(self,job):
        transcript={'text':'\n'.join(c['text'] for c in job['asr_chunks']),'segments':[s for chunk in job['aligned_chunks'] for s in chunk],'alignment_status':job['alignment_status']}
        digest=__import__('hashlib').sha256(json.dumps(transcript,sort_keys=True,ensure_ascii=False).encode()).hexdigest()
        path=self.root/'artifacts'/job['id']/('transcript-'+digest+'.json')
        if not path.exists():atomic_json(path,transcript)
        if job.get('artifact_path')!=str(path):job.update(speakers_path=None,speakers_status=None,speakers_error=None)
        job['artifact_path']=str(path)
    def replay(self,job):
        if job.get('deleted_at') or job.get('refresh_requested'):return
        now=time.monotonic();key=(job['id'],job['phase'],job.get('publication_id'),job.get('snapshot_request_id'))
        if now-self.last_replay.get(key,-100)>5:
            self.last_replay[key]=now
            if job['phase']=='publish' and job.get('publication_id'):
                self.emit('publication_ready',{'job_id':job['id'],'publication_id':job['publication_id'],'manifest_path':job['manifest_path']})
            elif job['phase']=='snapshot':
                self.emit('snapshot_requested',{'job_id':job['id'],'source_reference':job['source_reference'],'snapshot_request_id':job['snapshot_request_id']})
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
            if not job.get('deleted_at') and job['phase']=='publish' and job.get('publication_id') and job['status']=='saving':self.replay(job)
        if self.active or self.download:return
        for job in self.store.jobs():
            if job.get('deleted_at') or not job['queued'] or job['status']=='failed':continue
            if job.get('refresh_requested'):
                job.update(refresh_requested=False,phase='snapshot',status='waiting',publication_id=None)
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
                        notes=json.loads(validate_owned(self.root,job['notes_path']).read_text(encoding='utf-8')) if job.get('notes_path') else None
                        manifest_path=build_publication(self.root,job,transcript,self.model_manifest(job),notes=notes)
                        manifest=json.loads(Path(manifest_path).read_text())
                        job.update(manifest_path=manifest_path,publication_id=manifest['publication_id'],status='saving',stage='saving',wait_reason='publication',completed_units=None,total_units=None)
                    except (OSError,ValueError,KeyError):job.update(status='failed',queued=False,error=failure('artifact_failed'))
                    with self.store.transaction():self.save(job)
                self.replay(job);continue
            job_models=self.job_model_status(job)
            reason='paused' if paused else 'ac_power' if self.store.get_meta('ac_only',True) and not self.power else 'models' if not job_models['models_ready'] and not (job['phase']=='notes' and not job.get('notes_checkpoint') and (job.get('notes_protocol') or 'study_v1')=='study_v1') else 'source_incomplete' if job['completion_evidence']!='finished' else None
            if reason:
                status='paused' if paused else 'waiting'
                if job['status']!=status or job['wait_reason']!=reason:
                    job.update(status=status,wait_reason=reason)
                    with self.store.transaction():self.save(job)
                continue
            if job['phase']=='notes' and not job.get('notes_checkpoint') and (job.get('notes_protocol') or 'study_v1')=='study_v1' and not job_models.get('notes_ready',False):
                if job_models.get('model_status',{}).get('notes',{}).get('state')=='verifying':
                    job.update(status='waiting',stage='noting',wait_reason='models',completed_units=None,total_units=None)
                    with self.store.transaction():self.save(job)
                    continue
                self.fail_notes(job,failure('notes_model_missing'))
                with self.store.transaction():self.save(job)
                continue
            if job['phase']=='snapshot':
                if job['status']!='preparing' or not job.get('snapshot_request_id'):
                    job.update(status='preparing',stage='preparing',wait_reason='snapshot',completed_units=None,total_units=None)
                    if not job.get('snapshot_request_id'):job['snapshot_request_id']=uuid.uuid4().hex
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
                elif job['phase']=='notes':
                    op='notes';protocol=(job.get('notes_protocol') or ('legacy' if job.get('notes_checkpoint') else 'study_v1'))
                    data={'artifact_path':job['artifact_path'],'notes_protocol':protocol,'index':0}
                    job['notes_protocol']=protocol
                    if protocol=='study_v1':
                        if job.get('notes_checkpoint'):raise ValueError('study_checkpoint_not_supported')
                        job.update(completed_units=None,total_units=None)
                    elif protocol=='legacy':
                        source=json.loads(validate_owned(self.root,job['artifact_path']).read_text(encoding='utf-8'))['text']
                        total=len(source_chunks(source))
                        if job.get('notes_checkpoint'):
                            checkpoint=json.loads(validate_owned(self.root,job['notes_checkpoint']).read_text(encoding='utf-8'))
                            if not isinstance(checkpoint,dict) or type(checkpoint.get('index')) is not int or type(checkpoint.get('total_units')) is not int:raise ValueError('invalid_notes_checkpoint')
                            if not 0<checkpoint['index']<total or checkpoint['total_units']!=total or checkpoint.get('source_sha256')!=source_digest(source):raise ValueError('invalid_notes_checkpoint')
                            checkpoint['notes']=validate_notes(checkpoint.get('notes'),source)
                            data.update(notes_checkpoint=checkpoint,index=checkpoint['index'])
                        else:job.update(completed_units=0,total_units=total)
                    else:raise ValueError('unknown_notes_protocol')
                else:
                    op='title';data={'artifact_path':job['artifact_path']}
                    if job.get('title_checkpoint'):data['title_checkpoint']=json.loads(Path(job['title_checkpoint']).read_text())
                    else:job.update(completed_units=None,total_units=None)
                with self.store.transaction():self.launch(job,op,data)
            except (OSError,ValueError,TypeError,KeyError,IndexError):
                if job['phase']=='notes':self.fail_notes(job,failure('notes_failed'))
                elif job.get('refresh_previous') is not None:self.refresh_failed(job,failure('pipeline_failed'))
                else:job.update(status='failed',queued=False,error=failure('pipeline_failed'))
                with self.store.transaction():self.save(job)
            break
        if not self.active and not self.download and not paused:
            for job in self.store.jobs():
                if job.get('speakers_status')!='queued':continue
                if (job.get('deleted_at') or job.get('refresh_requested') or job.get('refresh_state') or job.get('queued')
                        or job['status'] not in {'completed','needs_recording_date','failed'}):
                    job.update(speakers_status='cancelled');self.save(job);continue
                if self.store.get_meta('ac_only',True) and not self.power:continue
                try:
                    with self.store.transaction():self.launch(job,'diarize',{'audio_path':job['audio_path'],'pcm_path':job['pcm_path'],'artifact_path':job['artifact_path'],'num_speakers':job.get('speakers_num_speakers')})
                except (OSError,ValueError,TypeError,KeyError):
                    job.update(speakers_status='failed',speakers_error=failure('diarization_failed',SPEAKER_MESSAGES['diarization_failed']))
                    with self.store.transaction():self.save(job)
                break
        self.flush_changed()
    def model_manifest(self,job=None):
        try:manifest=json.loads((self.model_root/'models.json').read_text())
        except (OSError,ValueError):manifest={}
        return {**manifest,'selection':dict((job or {}).get('model_selection') or DEFAULT_SELECTION)}
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
                if self.download.get('speakers_only'):
                    self.emit('model_progress',{'model':'speakers','state':'completed' if task.proc.returncode==0 else 'failed'})
                elif task.proc.returncode!=0:self.emit('model_progress',{'model':'all','downloaded_bytes':0,'total_bytes':None,'state':'failed','error':failure('model_download_failed')})
                self.download=None
                if self.inventory:self.inventory.start()
                self.models=self.readiness();self.emit('snapshot',self.snapshot())
        if not self.download and not self.active and self.store.get_meta('download_requested',False):
            with self.store.transaction():self.store.set_meta('download_requested',False)
            directory=self.root/'work'/'downloads'/uuid.uuid4().hex;directory.mkdir(parents=True)
            path=directory/'progress.jsonl'
            with self.lifecycle_lock:
                if self.stopping:return
                arguments=[sys.executable,'-m','voice_memo_transcriber.download_models','--model-root',str(self.model_root)]
                selected=self.store.get_meta('download_model_selection',dict(DEFAULT_SELECTION))
                arguments.extend(['--asr-profile',selected['asr'],'--notes-profile',selected['notes']])
                if self.store.get_meta('download_notes_only',False):arguments.append('--notes-only')
                speakers_only=self.store.get_meta('download_speakers_only',False)
                if speakers_only:
                    arguments=[sys.executable,'-m','voice_memo_transcriber.diarization_models','--model-root',str(self.model_root)]
                with open(path,'wb') as out:task=ManagedProcess(arguments,timeout=6*3600,stdout=out)
                if self.stopping:task.stop();return
                self.download={'task':task,'progress':path,'offset':0,'speakers_only':speakers_only}
                if speakers_only:self.emit('model_progress',{'model':'speakers','state':'downloading'})
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
            job=self.store.get(self.active['job_id'])
            if self.active['op']=='diarize':job.update(speakers_status='cancelled')
            else:job.update(status='waiting',wait_reason='restart')
            with self.store.transaction():self.store.put(job)
        self.store.close();self.closed=True
