import contextlib
import fcntl
import hashlib
import json
import os
import sqlite3
import threading
from pathlib import Path

class WriterBusy(RuntimeError): pass

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
