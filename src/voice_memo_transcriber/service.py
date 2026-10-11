"""Bounded JSONL bridge. Input/parent/child supervision is independent of inference."""
import argparse,json,os,queue,select,signal,sys,threading,time
from pathlib import Path
from .engine import Engine,failure
from .protocol import LIMIT,parse_command
from .store import WriterBusy

def serve(engine,tick=None):
    commands=queue.Queue(maxsize=256);outgoing=queue.Queue(maxsize=512);stopped=threading.Event();parent=os.getppid()
    stdout=sys.stdout.buffer;stdin=sys.stdin.fileno();original_emit=engine.emit_callback
    def stop():
        if stopped.is_set():return
        stopped.set();engine.request_stop()
        # A stalled fsync/model spawn must not keep an orphan lock forever.
        def deadline():
            if not engine.closed:os._exit(1)
        timer=threading.Timer(5,deadline);timer.daemon=True;timer.start()
    def output(event):
        encoded=json.dumps(event,ensure_ascii=False,allow_nan=False,separators=(',',':')).encode()+b'\n'
        if len(encoded)>LIMIT:
            raise ValueError('event_too_large')
        try:outgoing.put_nowait(encoded)
        except queue.Full:stop()
    engine.emit_callback=output
    def writer():
        while True:
            item=outgoing.get()
            try:
                if item is None:return
                stdout.write(item);stdout.flush()
            except (BrokenPipeError,OSError):stop();return
            finally:outgoing.task_done()
    def enqueue(item):
        try:commands.put_nowait(item)
        except queue.Full:stop()
    def reader():
        buffer=bytearray();oversize=False
        try:
            while not stopped.is_set():
                ready,_,_=select.select([stdin],[],[],.1)
                if not ready:continue
                data=os.read(stdin,65536)
                if not data:stop();return
                for part_index,part in enumerate(data.split(b'\n')):
                    if part_index:
                        if oversize:enqueue(('error',failure('line_too_large')))
                        elif buffer:
                            try:enqueue(('command',parse_command(bytes(buffer))))
                            except (ValueError,UnicodeError):
                                rid=''
                                try:
                                    value=json.loads(buffer);candidate=value.get('request_id') if isinstance(value,dict) else ''
                                    if isinstance(candidate,str) and len(candidate)<=256:rid=candidate
                                except (ValueError,UnicodeError):pass
                                enqueue(('error',{**failure('invalid_command'),'request_id':rid}))
                        else:enqueue(('error',failure('invalid_command')))
                        buffer.clear();oversize=False
                    if not oversize:
                        if len(buffer)+len(part)>LIMIT:buffer.clear();oversize=True
                        else:buffer.extend(part)
        except (OSError,ValueError):stop()
    def supervisor():
        while not stopped.wait(.1):
            if os.getppid()!=parent:stop();return
    for target in (writer,reader,supervisor):threading.Thread(target=target,daemon=True).start()
    def signaled(signum,frame):stop()
    old_signals={s:signal.signal(s,signaled) for s in (signal.SIGTERM,signal.SIGINT)}
    handshaken=False
    try:
        while not stopped.is_set() and not engine.stopping:
            try:kind,value=commands.get(timeout=.05)
            except queue.Empty:kind=None
            if kind=='command':
                result=engine.command(value)
                if result['ok'] and value['kind'] in {'hello','set_policy'}:handshaken=True
            elif kind=='error':
                rid=value.pop('request_id','');engine.emit('command_result',{'request_id':rid,'ok':False,'error':value})
            if handshaken and not stopped.is_set() and not engine.stopping:(tick or engine.tick)()
    finally:
        engine.close();stopped.set()
        # Give final result bounded time to drain, never block cleanup on GUI reads.
        until=time.monotonic()+.5
        while outgoing.unfinished_tasks and time.monotonic()<until:time.sleep(.01)
        try:outgoing.put_nowait(None)
        except queue.Full:pass
        for s,handler in old_signals.items():signal.signal(s,handler)
        engine.emit_callback=original_emit

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--state-root',type=Path,required=True);parser.add_argument('--model-root',type=Path,required=True);parser.add_argument('--ffmpeg-path',default='/opt/homebrew/bin/ffmpeg');parser.add_argument('--worker-timeout',type=float,default=3600)
    args=parser.parse_args()
    if args.worker_timeout<=0:parser.error('worker timeout must be positive')
    try:
        engine=Engine(args.state_root,args.model_root,lambda event:None,ffmpeg_path=args.ffmpeg_path,worker_timeout=args.worker_timeout)
    except WriterBusy:
        print('engine_state_locked',file=sys.stderr);return 73
    try:serve(engine)
    except Exception as exc:
        # Never expose recording text, paths, or upstream exception details.
        print('engine_session_failed: '+type(exc).__name__,file=sys.stderr);return 1
    return 0

if __name__=='__main__':sys.exit(main())
