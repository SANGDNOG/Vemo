"""Supervised process groups with an independent pipe/parent guardian."""
import os,select,signal,subprocess,sys,threading,time

class ManagedProcess:
    def __init__(self,command,timeout=3600,env=None,stdout=None):
        env=dict(os.environ if env is None else env)
        module_root=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        env['PYTHONPATH']=module_root+os.pathsep+env.get('PYTHONPATH','')
        self.timeout=timeout;self.started=time.monotonic();self.timed_out=False;self.mutex=threading.Lock()
        self.proc=subprocess.Popen([sys.executable,'-m','voice_memo_transcriber.process','--guardian',str(os.getpid()),*command],stdin=subprocess.PIPE,stdout=stdout if stdout is not None else subprocess.DEVNULL,stderr=subprocess.DEVNULL,close_fds=True,start_new_session=True,env=env)
    def poll(self):
        code=self.proc.poll()
        if code is None and time.monotonic()-self.started>self.timeout:
            self.timed_out=True;self.stop();code=self.proc.poll()
        if code is not None and self.proc.stdin and not self.proc.stdin.closed:self.proc.stdin.close()
        return code
    def stop(self,grace=1):
        with self.mutex:
            if self.proc.stdin and not self.proc.stdin.closed:self.proc.stdin.close()
            try:os.killpg(self.proc.pid,signal.SIGTERM)
            except ProcessLookupError:pass
            # Always kill the group after grace, even if the guardian exited early.
            end=time.monotonic()+grace
            while self.proc.poll() is None and time.monotonic()<end:time.sleep(.02)
            try:os.killpg(self.proc.pid,signal.SIGKILL)
            except ProcessLookupError:pass
            try:self.proc.wait(timeout=2)
            except subprocess.TimeoutExpired:pass

def guardian(parent,command):
    # This supervisor has its own group; model descendants use a separate group.
    # That permits SIGKILL of the entire model group while preserving exit status.
    stopping=[False]
    def signaled(*_):stopping[0]=True
    signal.signal(signal.SIGTERM,signaled);signal.signal(signal.SIGINT,signaled)
    child=subprocess.Popen(command,stdin=subprocess.DEVNULL,close_fds=True,start_new_session=True)
    orphan=False
    while child.poll() is None:
        readable,_,_=select.select([0],[],[],.1)
        if stopping[0] or os.getppid()!=parent or (readable and not os.read(0,1)):
            orphan=True;break
    code=child.returncode
    try:os.killpg(child.pid,signal.SIGTERM)
    except ProcessLookupError:pass
    end=time.monotonic()+.3
    while time.monotonic()<end:time.sleep(.02)
    # Unconditionally kill remaining descendants, including ones that ignore TERM.
    try:os.killpg(child.pid,signal.SIGKILL)
    except ProcessLookupError:pass
    try:child.wait(timeout=1)
    except subprocess.TimeoutExpired:pass
    if orphan:return 143
    return code if code is not None and code>=0 else 1

if __name__=='__main__':
    sys.exit(guardian(int(sys.argv[2]),sys.argv[3:]))
