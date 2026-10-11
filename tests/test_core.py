import unittest, tempfile, pathlib, json, datetime, sys
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))

class CoreTests(unittest.TestCase):
    def core(self):
        try:
            from voice_memo_transcriber import dates, titles, protocol, store, artifacts
            return dates,titles,protocol,store,artifacts
        except ImportError:
            self.fail('Engine core has not been implemented')
    def test_dates_timezone_epoch_and_missing(self):
        d,*_=self.core()
        self.assertEqual(d.recording_day('2026-10-10T14:59:59Z',None),'2026-10-10')
        self.assertEqual(d.recording_day('2026-10-10T15:00:00Z',None),'2026-10-11')
        self.assertEqual(d.recording_day('2026-10-10T15:00:00Z','America/Los_Angeles'),'2026-10-10')
        self.assertIsNone(d.recording_day(None,None))
        self.assertEqual(d.apple_epoch_iso(0),'2001-01-01T00:00:00+00:00')
        self.assertRaises(ValueError,d.recording_day,'2026-10-10T12:00:00',None)
        self.assertRaises(ValueError,d.validate_day,'2025-02-29')
        self.assertEqual(d.validate_day('2024-02-29'),'2024-02-29')
    def test_title_sanitize_codepoints_and_single_suffix(self):
        _,t,*_=self.core()
        self.assertEqual(t.display_title('일정 / 검토 — 2099-01-01','2026-10-10'),'일정 검토 — 2026-10-10')
        topic=t.sanitize_topic('가'*60+'\x00/..')
        self.assertEqual(len(topic),40)
        self.assertEqual(t.sanitize_topic('e\u0301'),'é')
        self.assertEqual(t.sanitize_topic('../\\\n'),'음성 녹음')
    def test_protocol_rejects_version_unknown_and_oversize(self):
        _,_,p,*_=self.core()
        for c in [dict(request_id='x',protocol_version=2,kind='pause',payload={}),dict(request_id='x',protocol_version=1,kind='shell',payload={})]:
            self.assertRaises(ValueError,p.parse_command,json.dumps(c).encode())
        self.assertRaises(ValueError,p.parse_command,b' '* (8*1024*1024+1))
        self.assertEqual(p.parse_command(b'{"request_id":"x","protocol_version":1,"kind":"hello","payload":{}}')['kind'],'hello')
    def test_single_writer_and_durable_request_dedupe(self):
        *_,s,a=self.core()
        with tempfile.TemporaryDirectory() as td:
            first=s.Store(pathlib.Path(td))
            self.assertRaises(s.WriterBusy,s.Store,pathlib.Path(td))
            calls=[]
            def effect():
                calls.append(1); first.set_meta('durable','yes'); return {'ok':True}
            self.assertEqual(first.request('req',{'kind':'pause'},effect),{'ok':True})
            first.close(); second=s.Store(pathlib.Path(td))
            self.assertEqual(second.request('req',{'kind':'pause'},effect),{'ok':True})
            self.assertEqual(calls,[1]); self.assertEqual(second.get_meta('durable'),'yes')
            self.assertRaises(ValueError,second.request,'req',{'kind':'resume'},effect)
            second.close()
    def test_immutable_artifacts_preserve_previous_revision(self):
        *_,a=self.core()
        with tempfile.TemporaryDirectory() as td:
            job={'id':'j','uuid':'u','pcm_hash':'a'*64,'topic':'일정','recording_date':'2026-10-10','revision':1,'date_provenance':'user_supplied'}
            tr={'text':'원문','segments':[],'alignment_status':'failed'}
            one=a.build_publication(pathlib.Path(td),job,tr,{})
            before=pathlib.Path(one).read_bytes()
            job['revision']=2; job['topic']='새 제목'
            two=a.build_publication(pathlib.Path(td),job,tr,{})
            self.assertNotEqual(one,two); self.assertEqual(pathlib.Path(one).read_bytes(),before)
            m=json.loads(before); self.assertEqual(len(m['files']),2)
            self.assertTrue(all('2026-10-10' in f['name'] for f in m['files']))
            hashes={f['name']:f['sha256'] for f in m['files']}
            self.assertTrue(a.valid_receipt(m,{'publication_id':m['publication_id'],'output_manifest_path':'/output/manifest.json','artifact_hashes':hashes}))
            hashes[next(iter(hashes))]='0'*64
            self.assertFalse(a.valid_receipt(m,{'publication_id':m['publication_id'],'output_manifest_path':'/output/manifest.json','artifact_hashes':hashes}))

if __name__=='__main__': unittest.main()
