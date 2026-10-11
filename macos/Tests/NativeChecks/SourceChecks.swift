import Foundation
import CSQLite
import NativeCore
struct FixtureSourceAdapter: VerifiedSourceAdapter {
    let recording: SourceRecording
    func accepts(_ diagnostic: SourceSchemaDiagnostic) -> Bool { diagnostic.tables.contains { $0.name == "verified_fixture" && $0.columns.contains { $0.name == "finished" && $0.type == "INTEGER" } } }
    func recordings(databaseSnapshot: URL) throws -> [SourceRecording] { [recording] }
}
func sourceChecks(root: URL) async throws {
    let sharedQTA = root.appendingPathComponent("shared.qta")
    try wave().write(to:sharedQTA)
    let sharedBroker = ImportBroker(root:root.appendingPathComponent("qta-import-state"))
    let importedQTA = try await sharedBroker.importFile(sharedQTA)
    try check(importedQTA.duration_s == 1 && importedQTA.recorded_at == nil && importedQTA.completion_evidence == "finished", "shared QTA imports as fully decoded audio without inventing recording date")
    try await appleSourceChecks(root:root)
    let original = root.appendingPathComponent("source-store"), state = root.appendingPathComponent("source-state")
    try FileManager.default.createDirectory(at:original,withIntermediateDirectories:true)
    let db = original.appendingPathComponent("CloudRecordings.db"), audio = original.appendingPathComponent("recording.wav")
    try wave().write(to:audio)
    var sqlite: OpaquePointer?; guard sqlite3_open(db.path,&sqlite) == SQLITE_OK else { throw NativeFailure.invalid("fixture DB open failed") }
    guard sqlite3_exec(sqlite,"CREATE TABLE verified_fixture(finished INTEGER); INSERT INTO verified_fixture VALUES(1)",nil,nil,nil) == SQLITE_OK else { throw NativeFailure.invalid("fixture DB creation failed") }; sqlite3_close(sqlite)
    let before = try Data(contentsOf:db), source = SourceBroker(root:state,databaseURL:db)
    let diagnostic = await source.diagnostic()
    try check(diagnostic.status == "unsupported_schema" && !diagnostic.schema_verified, "unknown Apple schema explicitly unsupported")
    try check(diagnostic.tables.contains { $0.name == "verified_fixture" }, "read-only content-free schema introspection")
    do { _ = try await source.scan(); throw NativeFailure.invalid("FAIL: unsupported source became empty success") } catch { try check(!error.localizedDescription.contains("FAIL:"), "unknown schema never returns empty success") }
    try check(try Data(contentsOf:db) == before && !FileManager.default.fileExists(atPath:db.path + "-shm"), "source DB unchanged and no original SQLite sidecars")
    let missing = await SourceBroker(root:state,databaseURL:original.appendingPathComponent("missing.db")).diagnostic()
    try check(missing.status == "source_access_failed", "missing or unreadable source is an explicit diagnostic")
    let recording = SourceRecording(uuid:"fixture-id",sourceURL:audio,recordedAt:nil,recordedAtSource:"unknown",originalTimezone:nil,duration:1,title:"fixture",completionEvidence:"finished")
    let verified = SourceBroker(root:state,databaseURL:db,adapter:FixtureSourceAdapter(recording:recording))
    let memos = try await verified.scan()
    try check(memos.count == 1 && memos[0].recorded_at == nil && !memos[0].source_reference.contains(audio.path), "verified adapter discovery is opaque and never dates from mtime")
    let reopened = SourceBroker(root:state,databaseURL:db,adapter:FixtureSourceAdapter(recording:recording))
    let repeated = try await reopened.scan()
    try check(repeated[0].source_reference == memos[0].source_reference, "native source registry survives restart")
    let snapshot = try await reopened.snapshot(reference:memos[0].source_reference,jobID:"source-job")
    try check(snapshot.revision_before == snapshot.revision_after && FileManager.default.fileExists(atPath:snapshot.owned_path), "verified finished source produces stable owned snapshot")
    let active = SourceRecording(uuid:"fixture-id",sourceURL:audio,recordedAt:nil,recordedAtSource:"unknown",originalTimezone:nil,duration:1,title:"fixture",completionEvidence:"active")
    let activeBroker = SourceBroker(root:state,databaseURL:db,adapter:FixtureSourceAdapter(recording:active))
    do { _ = try await activeBroker.snapshot(reference:memos[0].source_reference,jobID:"active-job"); throw NativeFailure.invalid("FAIL: active recording copied") } catch { try check(!error.localizedDescription.contains("FAIL:"), "active recording is never snapshotted as finished") }
}
