import pathlib,sys,json,time,os
from voice_memo_transcriber.engine import Engine
from voice_memo_transcriber.service import serve
from voice_memo_transcriber.process import ManagedProcess
root=pathlib.Path(sys.argv[1]);emit=lambda e:print(json.dumps(e),flush=True)
e=Engine(root,root/'models',emit)
pidpath=root/'model.pid'
command=[sys.executable,'-c','import os,time;open('+repr(str(pidpath))+',"w").write(str(os.getpid()));time.sleep(60)']
e.active={'job_id':'test','op':'asr','task':ManagedProcess(command),'output':root/'unused','index':0}
# A real owned job allows normal crash checkpoint recovery.
e.store.put({'id':'test','uuid':'test','status':'transcribing','phase':'asr','elapsed_seconds':0})
while not pidpath.exists():time.sleep(.01)
e.emit('model_started',{})
serve(e,tick=lambda:None)
