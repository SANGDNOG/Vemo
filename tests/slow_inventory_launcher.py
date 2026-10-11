import sys,pathlib,time
import voice_memo_transcriber.engine as engine_module
import voice_memo_transcriber.models as models_module
from voice_memo_transcriber.service import serve
models_module.model_status=lambda root:time.sleep(30) or {'models_ready':True,'model_status':{}}
root=pathlib.Path(sys.argv[1]);engine=engine_module.Engine(root,root/'models',lambda e:None)
serve(engine)
