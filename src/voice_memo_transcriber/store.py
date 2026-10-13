import contextlib
import fcntl
import hashlib
import json
import os
import sqlite3
import stat
import threading
import uuid
from pathlib import Path

class WriterBusy(RuntimeError): pass

@contextlib.contextmanager
def owned_directory(root,parts=()):
    """Pin each directory without following links or crossing mounted filesystems."""
    fd=os.open(root,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
    try:
        device=os.fstat(fd).st_dev
        for part in parts:
            if not isinstance(part,str) or not part or part in {'.','..'} or '/' in part:
                raise ValueError('unsafe_purge_path')
            child=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=fd)
            if os.fstat(child).st_dev!=device:
                os.close(child);raise ValueError('unsafe_purge_mount')
            os.close(fd);fd=child
        yield fd
    finally:os.close(fd)

def remove_owned_tree(root,parts):
    """Delete only the named subtree using pinned descriptors; missing is idempotent."""
    def walk(parent,name,device,remove):
        try:before=os.stat(name,dir_fd=parent,follow_symlinks=False)
        except FileNotFoundError:return
        if before.st_dev!=device:raise ValueError('unsafe_purge_mount')
        if stat.S_ISREG(before.st_mode):
            if remove:os.unlink(name,dir_fd=parent)
            return
        if not stat.S_ISDIR(before.st_mode):raise ValueError('unsafe_purge_file')
        fd=os.open(name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent)
        try:
            opened=os.fstat(fd)
            if (opened.st_dev,opened.st_ino)!=(before.st_dev,before.st_ino):raise ValueError('changing_purge_path')
            for child in os.listdir(fd):walk(fd,child,device,remove)
            if remove:os.fsync(fd)
            after=os.stat(name,dir_fd=parent,follow_symlinks=False)
            if (after.st_dev,after.st_ino)!=(opened.st_dev,opened.st_ino):raise ValueError('changing_purge_path')
        finally:os.close(fd)
        if remove:os.rmdir(name,dir_fd=parent)
    try:
        with owned_directory(root,parts[:-1]) as parent:
            device=os.fstat(parent).st_dev
            # Reject unsafe descendants before touching any contents of this tree.
            walk(parent,parts[-1],device,False)
            walk(parent,parts[-1],device,True)
            os.fsync(parent)
    except FileNotFoundError:return

def atomic_json_at(parent,name,value):
    """Durable registry update beneath an already validated and locked directory."""
    temp='.purge-'+uuid.uuid4().hex+'.tmp'
    try:
        fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW|os.O_CLOEXEC,0o600,dir_fd=parent)
        with os.fdopen(fd,'w',encoding='utf-8') as out:
            json.dump(value,out,ensure_ascii=False,allow_nan=False);out.flush();os.fsync(out.fileno())
        os.replace(temp,name,src_dir_fd=parent,dst_dir_fd=parent);os.fsync(parent)
    finally:
        try:os.unlink(temp,dir_fd=parent)
        except FileNotFoundError:pass

class Store:
    def __init__(self,root):
        self.root=Path(root); self.root.mkdir(parents=True,exist_ok=True)
        self.owner=threading.get_ident()
        self.lock=open(self.root/'engine.lock','a+b')
        try: fcntl.flock(self.lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
        except BlockingIOError:
            self.lock.close(); raise WriterBusy('another engine owns this state root')
        try:
            self.db=sqlite3.connect(str(self.root/'queue.sqlite3'),isolation_level=None)
            self.db.execute('PRAGMA journal_mode=WAL'); self.db.execute('PRAGMA synchronous=FULL')
            self.db.executescript('''CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
              CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,uuid TEXT UNIQUE NOT NULL,data TEXT NOT NULL);
              CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY,digest TEXT NOT NULL,result TEXT NOT NULL);''')
        except BaseException:
            self.lock.close(); raise
    def check_owner(self):
        if threading.get_ident()!=self.owner: raise RuntimeError('only the engine thread may write the queue')
    @contextlib.contextmanager
    def transaction(self):
        self.check_owner(); self.db.execute('BEGIN IMMEDIATE')
        try: yield; self.db.execute('COMMIT')
        except BaseException:
            self.db.execute('ROLLBACK'); raise
    def request(self,rid,command,effect):
        digest=hashlib.sha256(json.dumps(command,sort_keys=True,ensure_ascii=False,allow_nan=False).encode()).hexdigest()
        with self.transaction():
            old=self.db.execute('SELECT digest,result FROM requests WHERE id=?',(rid,)).fetchone()
            if old:
                if old[0]!=digest: raise ValueError('request_id_conflict')
                return json.loads(old[1])
            result=effect()
            self.db.execute('INSERT INTO requests VALUES(?,?,?)',(rid,digest,json.dumps(result,ensure_ascii=False,allow_nan=False)))
        return result
    def get_meta(self,key,default=None):
        row=self.db.execute('SELECT value FROM meta WHERE key=?',(key,)).fetchone()
        return json.loads(row[0]) if row else default
    def set_request_result(self,rid,result):
        self.check_owner()
        self.db.execute('UPDATE requests SET result=? WHERE id=?',(json.dumps(result,ensure_ascii=False,allow_nan=False),rid))
    def set_meta(self,key,value):
        self.check_owner(); self.db.execute('INSERT OR REPLACE INTO meta VALUES(?,?)',(key,json.dumps(value,ensure_ascii=False,allow_nan=False)))
    def put(self,job):
        self.check_owner(); self.db.execute('INSERT INTO jobs VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET data=excluded.data',(job['id'],job['uuid'],json.dumps(job,ensure_ascii=False,allow_nan=False)))
    def get(self,jid):
        row=self.db.execute('SELECT data FROM jobs WHERE id=?',(jid,)).fetchone()
        if not row: raise ValueError('unknown_job')
        return json.loads(row[0])
    def jobs(self):
        return [json.loads(r[0]) for r in self.db.execute('SELECT data FROM jobs ORDER BY rowid')]
    def by_uuid(self,uid):
        row=self.db.execute('SELECT data FROM jobs WHERE uuid=?',(uid,)).fetchone()
        return json.loads(row[0]) if row else None
    def close(self):
        self.check_owner(); self.db.close(); self.lock.close()
