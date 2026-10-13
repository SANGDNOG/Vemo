"""Independent permanent-delete checks, exclusively in temporary synthetic state."""
import contextlib
import json
import os
import pathlib
import sqlite3
import unittest
from unittest.mock import patch

import test_engine
from test_engine import memo


class RecordingPurgeIndependentTests(unittest.TestCase):
    def setUp(self):
        test_engine.EngineTests.setUp(self)
        # Native registry paths use the engine's canonical owned state root.
        self.root = self.root.resolve()
    tearDown = test_engine.EngineTests.tearDown
    engine = test_engine.EngineTests.engine
    send = test_engine.EngineTests.send
    job = test_engine.EngineTests.job
    drain = test_engine.EngineTests.drain
    snapshot = test_engine.EngineTests.snapshot

    def create(self, uid="purge-independent", imported=False):
        if not hasattr(self, "e"):
            self.engine()
        item = memo(uid, revision={"size": 40, "mtime_ns": 1})
        item["duration_s"] = 3 / 16000
        if imported:
            item.update(source_kind="import", source_reference="import:" + uid)
        self.assertTrue(self.send("discover", {"memos": [item]})["ok"])
        return self.job(uid)

    def owned_files(self, job):
        files = []
        for scope in ("work", "artifacts", "snapshots/jobs"):
            path = self.root / scope / job["id"] / "nested" / "synthetic.bin"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic owned content")
            files.append(path)
        return files

    def trash(self, job):
        self.assertTrue(self.send("trash_recording", {"job_id": job["id"]})["ok"])

    def purge(self, job, rid=None):
        return self.send("purge_recording", {"job_id": job["id"]}, rid)

    def assert_hidden(self, job):
        self.assertNotIn(job["id"], [j["id"] for j in self.e.snapshot()["jobs"]])

    def assert_preserved(self, before):
        for path, value in before.items():
            self.assertEqual(path.read_bytes(), value, str(path))

    def test_nontrash_rejection_preserves_job_and_owned_content(self):
        job = self.create()
        files = self.owned_files(job)
        before = {p: p.read_bytes() for p in files}
        self.assertFalse(self.purge(job)["ok"])
        self.assertEqual(self.job(job["uuid"]), job)
        self.assert_preserved(before)
        self.assertFalse(any(e["kind"] == "job_removed" for e in self.events))

    def test_actual_generated_files_removed_and_external_hardlink_export_preserved(self):
        job = self.create()
        self.send("enqueue", {"uuids": [job["uuid"]]})
        self.e.tick()
        self.snapshot(self.job(job["uuid"]))
        self.drain(lambda: self.job(job["uuid"]).get("publication_id") is not None)
        job = self.job(job["uuid"])
        manifest = json.loads(pathlib.Path(job["manifest_path"]).read_text())
        generated = {pathlib.Path(job[k]) for k in
                     ("audio_path", "pcm_path", "artifact_path", "notes_path", "manifest_path")}
        generated.update(pathlib.Path(item["path"]) for item in manifest["files"])
        self.assertTrue(all(p.is_file() for p in generated))
        # A surviving hardlink proves cleanup unlinks owned names, without wiping
        # inode contents that may also be referenced outside the owned tree.
        original = self.root / "external-source.wav"
        os.link(job["audio_path"], original)
        export = self.root / "exported-recording.zip"
        export.write_bytes(b"already exported; outside purge scope")
        native = self.root / "native" / "recording-zips" / "receipt.json"
        native.parent.mkdir(parents=True)
        native.write_text('{"synthetic":true}')
        before = {p: p.read_bytes() for p in (original, export, native)}
        self.trash(job)
        self.events.clear()
        self.assertTrue(self.purge(job, "actual-purge")["ok"])
        self.assertTrue(all(not p.exists() for p in generated))
        self.assert_preserved(before)
        kinds = [e["kind"] for e in self.events]
        self.assertEqual(kinds, ["job_removed", "command_result"])
        self.assertEqual(self.events[0]["payload"], {"job_id": job["id"]})
        self.assert_hidden(job)

    def test_transaction_abort_prevents_any_filesystem_cleanup(self):
        job = self.create()
        files = self.owned_files(job)
        self.trash(job)
        before_job = self.job(job["uuid"])
        before = {p: p.read_bytes() for p in files}
        transaction = self.e.store.transaction

        @contextlib.contextmanager
        def abort_before_commit():
            with transaction():
                yield
                raise ValueError("synthetic intent commit failure")

        with patch.object(self.e.store, "transaction", abort_before_commit), \
                patch.object(self.e, "cleanup_purge", wraps=self.e.cleanup_purge) as cleanup:
            self.assertFalse(self.purge(job)["ok"])
            cleanup.assert_not_called()
        self.assertEqual(self.job(job["uuid"]), before_job)
        self.assert_preserved(before)

    def test_real_first_unlink_observes_committed_intent_and_restart_finishes_partial_cleanup(self):
        job = self.create()
        files = self.owned_files(job)
        self.trash(job)
        unlink = os.unlink
        removed = []

        def crash_after_unlink(path, *args, **kwargs):
            self.assertFalse(self.e.store.db.in_transaction)
            with sqlite3.connect(self.root / "queue.sqlite3") as db:
                raw = db.execute("SELECT data FROM jobs WHERE id=?", (job["id"],)).fetchone()[0]
                durable = json.loads(raw)
            self.assertTrue(durable.get("purge_requested_at"))
            self.assertTrue(durable.get("deleted_at"))
            self.assertFalse(durable["queued"])
            unlink(path, *args, **kwargs)
            removed.append(str(path))
            raise SystemExit("synthetic interruption after first real unlink")

        with patch("os.unlink", side_effect=crash_after_unlink):
            with self.assertRaises(SystemExit):
                self.purge(job, "interrupted-real-cleanup")
        self.assertEqual(len(removed), 1)
        self.assertTrue(any(p.exists() for p in files), "Must interrupt a partially completed cleanup")
        self.e.close()
        self.engine()
        self.assertTrue(all(not p.exists() for p in files))
        self.assert_hidden(job)
        self.assertTrue(self.purge(job, "interrupted-real-cleanup")["ok"])

    def test_cleanup_error_remains_visible_honest_and_retryable_after_restart(self):
        job = self.create()
        files = self.owned_files(job)
        self.trash(job)
        self.events.clear()
        with patch.object(self.e, "cleanup_purge", side_effect=PermissionError("synthetic-private-path")):
            failed = self.purge(job, "failed-cleanup")
            self.assertFalse(failed["ok"])
            self.assertEqual(failed["error"]["code"], "purge_cleanup_failed")
            self.assertTrue(failed["error"]["recoverable"])
            view = next(j for j in self.e.snapshot()["jobs"] if j["id"] == job["id"])
            self.assertIs(view["purge_pending"], True)
            self.assertTrue(view["deleted_at"])
            self.assertFalse(any(e["kind"] == "job_removed" for e in self.events))
            self.assertTrue(all(p.exists() for p in files))
            self.assertNotIn("synthetic-private-path", json.dumps(self.events))
            self.assertFalse(self.send("restore_recording", {"job_id": job["id"]})["ok"])
        self.e.close()
        from voice_memo_transcriber.engine import Engine
        with patch.object(Engine, "cleanup_purge", side_effect=PermissionError("still unavailable")):
            self.engine()
            self.assertTrue(self.e.snapshot()["jobs"][0]["purge_pending"])
        self.assertTrue(self.purge(job, "cleanup-retry")["ok"])
        self.assertTrue(all(not p.exists() for p in files))
        self.assert_hidden(job)

    def test_purge_suppression_survives_all_discovery_routes_and_valid_stale_receipt(self):
        job = self.create()
        self.send("enqueue", {"uuids": [job["uuid"]]})
        self.e.tick()
        token = self.job(job["uuid"])["snapshot_request_id"]
        self.snapshot(self.job(job["uuid"]))
        self.drain(lambda: self.job(job["uuid"]).get("publication_id") is not None)
        job = self.job(job["uuid"])
        manifest = json.loads(pathlib.Path(job["manifest_path"]).read_text())
        receipt = {"job_id": job["id"], "publication_id": job["publication_id"], "receipt": {
            "publication_id": job["publication_id"], "output_manifest_path": "/synthetic/export.json",
            "artifact_hashes": {f["name"]: f["sha256"] for f in manifest["files"]}}}
        self.trash(job)
        self.assertTrue(self.purge(job, "durable-purge")["ok"])
        self.e.close()
        self.engine()
        changed = memo(job["uuid"], revision={"size": 90, "mtime_ns": 9})
        changed.update(metadata_revision="9", duration_s=20, source_reference="new-source-reference")
        for kind, payload in (("discover", {"memos": [changed]}), ("import", {"memo": changed}),
                              ("enqueue", {"uuids": [job["uuid"]]}), ("backfill", {"uuids": [job["uuid"]]})):
            self.send(kind, payload)
        self.assertFalse(self.send("publish_ack", receipt)["ok"])
        self.assertFalse(self.send("snapshot_ready", {"job_id": job["id"], "snapshot_request_id": token,
                                                      "error": {"code": "late"}})["ok"])
        for kind in ("restore_recording", "refresh_recording", "generate_notes", "retry"):
            self.assertFalse(self.send(kind, {"job_id": job["id"]})["ok"], kind)
        self.events.clear()
        self.e.tick()
        self.assertIsNone(self.e.active)
        self.assertFalse(any(e["kind"] in {"snapshot_requested", "publication_ready"} for e in self.events))
        self.assert_hidden(job)
        self.assertTrue(self.purge(job, "durable-purge")["ok"])
        self.assertTrue(self.purge(job)["ok"])

    def test_job_scope_symlink_cannot_delete_other_jobs_or_outside_source(self):
        job = self.create()
        other = self.create("must-survive")
        protected = self.owned_files(other)
        before = {p: p.read_bytes() for p in protected}
        link = self.root / "work" / job["id"]
        link.symlink_to(self.root / "work" / other["id"], target_is_directory=True)
        self.trash(job)
        result = self.purge(job)
        self.assertFalse(result["ok"])
        self.assertEqual(result["error"]["code"], "purge_cleanup_failed")
        self.assert_preserved(before)
        self.assertEqual(self.job(other["uuid"]), other)
        self.assertTrue(link.is_symlink())
        link.unlink()
        self.assertTrue(self.purge(job)["ok"])
        self.assert_preserved(before)

    def import_registry(self, job, shared=False):
        cache = self.root / "snapshots/imports" / "synthetic.wav"
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_bytes(b"synthetic import cache")
        item = memo(job["uuid"])
        item.update(source_kind="import", source_reference=job["source_reference"])
        entry = {"memo": item, "path": str(cache), "hash": "a" * 64, "delivered": False}
        entries = {job["source_reference"]: entry}
        if shared:
            other = {**item, "uuid": "other-import", "source_reference": "import:other"}
            entries["import:other"] = {**entry, "memo": other}
        native = self.root / "native"
        native.mkdir(exist_ok=True)
        path = native / "imports.json"
        registry = {"schema_version": 1, "entries": entries}
        path.write_text(json.dumps(registry))
        return cache, path, registry

    def test_import_registry_marks_only_matching_entry_and_preserves_external_source(self):
        job = self.create(imported=True)
        cache, path, registry = self.import_registry(job)
        original = self.root / "original-source.wav"
        os.link(cache, original)
        expected = original.read_bytes()
        registry["entries"]["import:unrelated"] = {"untouched": [1, 2, 3]}
        path.write_text(json.dumps(registry))
        self.trash(job)
        self.assertTrue(self.purge(job)["ok"])
        saved = json.loads(path.read_text())
        self.assertIs(saved["entries"][job["source_reference"]]["purged"], True)
        self.assertEqual(saved["entries"]["import:unrelated"], {"untouched": [1, 2, 3]})
        self.assertFalse(cache.exists())
        self.assertEqual(original.read_bytes(), expected)

    def test_shared_import_cache_cannot_be_removed_from_another_live_entry(self):
        job = self.create(imported=True)
        cache, path, registry = self.import_registry(job, shared=True)
        original = cache.read_bytes()
        self.trash(job)
        result = self.purge(job)
        self.assertEqual(cache.read_bytes(), original, "An unpurged import still owns this cache")
        self.assertEqual(json.loads(path.read_text())["entries"]["import:other"],
                         registry["entries"]["import:other"])
        if not result["ok"]:
            self.assertEqual(result["error"]["code"], "purge_cleanup_failed")
            self.assertTrue(result["error"]["recoverable"])


if __name__ == "__main__":
    unittest.main()
