import pathlib,sys
from voice_memo_transcriber.engine import Engine
from voice_memo_transcriber.service import serve
from test_engine import memo
root=pathlib.Path(sys.argv[1])
e=Engine(root,root/'models',lambda e:None,power_check=lambda:False,readiness=lambda:{'models_ready':True,'model_status':{}})
e.store.set_meta('ac_only',False);e.discover([memo('persisted')],True)
serve(e)
