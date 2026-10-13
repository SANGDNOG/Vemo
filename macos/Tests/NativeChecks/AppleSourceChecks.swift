import Foundation
import CSQLite
import NativeCore

// Catches epoch/date substitution, pending media treated as complete, deletion
// resurrection, unsafe filenames, read/write source access and duration drift.
func appleSourceChecks(root: URL) async throws {
    let folder = root.appendingPathComponent("apple-fixture")
    try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true)
    let db = folder.appendingPathComponent("CloudRecordings.db")
    let audio = folder.appendingPathComponent("finished.wav")
    try wave().write(to:audio)
    var sqlite: OpaquePointer?
    guard sqlite3_open(db.path,&sqlite) == SQLITE_OK else { throw NativeFailure.invalid("fixture open") }
    defer { sqlite3_close(sqlite) }
    func sql(_ text: String) throws { guard sqlite3_exec(sqlite,text,nil,nil,nil) == SQLITE_OK else { throw NativeFailure.invalid("fixture SQL: " + String(cString:sqlite3_errmsg(sqlite))) } }
    try sql("CREATE TABLE ZCLOUDRECORDING(Z_PK INTEGER PRIMARY KEY,Z_ENT INTEGER,Z_OPT INTEGER,ZAUDIOFUTUREFLAGS INTEGER,ZFLAGS INTEGER,ZSHAREDFLAGS INTEGER,ZSILENCEREMOVERENABLED INTEGER,ZSKIPSILENCEENABLED INTEGER,ZSTUDIOMIXENABLED INTEGER,ZFOLDER INTEGER,ZDATE TIMESTAMP,ZDURATION FLOAT,ZEVICTIONDATE TIMESTAMP,ZLOCALDURATION FLOAT,ZMTLAYERMIX FLOAT,ZPLAYBACKPOSITION FLOAT,ZPLAYBACKRATE FLOAT,ZPLAYBACKSPEED FLOAT,ZSTUDIOMIXLEVEL FLOAT,ZCUSTOMLABEL VARCHAR,ZCUSTOMLABELFORSORTING VARCHAR,ZENCRYPTEDTITLE VARCHAR,ZPATH VARCHAR,ZUNIQUEID VARCHAR,ZAUDIOFUTUREUUIDS BLOB,ZAUDIODIGEST BLOB,ZAUDIOFUTURE BLOB,ZMTAUDIOFUTURE BLOB,ZVERSIONEDAUDIOFUTURE BLOB); CREATE TABLE Z_METADATA(Z_PLIST BLOB)")
    let hashes = ["CloudRecording":"wzISBP+96pkUsBpdE2V3vJH08CnDBpBi8U/vSlVVosQ=","DatabaseProperty":"DdeyItMrmgzYUVyA8NUAc8cS1Sr4LwwTo+KneZZrPBI=","EntityRevision":"MCYSLwlQNrzkytEuk0Paa2vv7h+rbxnn3DWtIuHpxa0=","Folder":"BTuJxZB4F1ci2UMqAPo0Fx2Oif08rXM0z+/UQoivHc0=","Migration":"C9+RC8Owb0OTnIogkfdqVeaZV1hChUC6VuqvEwC0DUU=","Recording":"l+6Nf+h4pgpvs9n/EqyKB5n5y0F2UwNh6/6d/evM+L8="]
    let plist = try PropertyListSerialization.data(fromPropertyList:["NSStoreModelVersionHashes":hashes.mapValues { Data(base64Encoded:$0)! }],format:.binary,options:0)
    try sql("INSERT INTO Z_METADATA VALUES(X'" + plist.map { String(format:"%02x",$0) }.joined() + "'); INSERT INTO ZCLOUDRECORDING(Z_PK,Z_OPT,ZFLAGS,ZDATE,ZDURATION,ZLOCALDURATION,ZPATH,ZUNIQUEID,ZCUSTOMLABEL,ZCUSTOMLABELFORSORTING) VALUES(1,1,4,813682799,1,1,'finished.wav','finished-id','2026-10-14T14:59:59Z','목표 강의')")
    let before = try Data(contentsOf:db)
    let adapter = AppleVoiceMemosAdapter(recordingsRoot:folder,editingRisk:{ false })
    let broker = SourceBroker(root:root.appendingPathComponent("apple-state"),databaseURL:db,adapter:adapter)
    let found = try await broker.scan()
    try check(found.count == 1 && found[0].recorded_at == "2026-10-14T14:59:59Z", "Apple date is reference epoch, never file date")
    try check(found[0].original_title == "목표 강의", "Apple visible title uses localTitle field rather than legacy date label")
    try check(found[0].completion_evidence == "finished", "playable synced media is eligible when Apple editor closed")
    try check(found[0].metadata_revision == "1", "Apple metadata revision reaches engine discovery")
    let snapshot = try await broker.snapshot(reference:found[0].source_reference,jobID:"finished")
    try check(FileManager.default.fileExists(atPath:snapshot.owned_path), "pinned Apple source produces owned decoded audio")
    try check(try Data(contentsOf:db) == before && !FileManager.default.fileExists(atPath:db.path + "-shm"), "Apple adapter never writes source DB or sidecars")
    try sql("UPDATE ZCLOUDRECORDING SET Z_OPT=2")
    let revised = try await broker.scan()
    try check(revised[0].metadata_revision == "2" && revised[0].media_revision == found[0].media_revision, "metadata changes are detected even with unchanged audio file stamp")
    try sql("UPDATE ZCLOUDRECORDING SET Z_OPT=1")
    for flag in [0,2,6,260] {
        try sql("UPDATE ZCLOUDRECORDING SET ZFLAGS=\(flag)")
        let memos = try await broker.scan()
        try check(memos[0].completion_evidence == "unknown", "unplayable or pending flag \(flag) stays incomplete")
    }
    try sql("UPDATE ZCLOUDRECORDING SET ZFLAGS=4")
    let editing = SourceBroker(root:root.appendingPathComponent("editing-state"),databaseURL:db,adapter:AppleVoiceMemosAdapter(recordingsRoot:folder,editingRisk:{ true }))
    try check(try await editing.scan()[0].completion_evidence == "unknown", "open Apple recording editor blocks automatic snapshots")
    let qta = folder.appendingPathComponent("spatial.qta")
    try wave().write(to:qta)
    try sql("UPDATE ZCLOUDRECORDING SET ZPATH='spatial.qta',ZDURATION=0,ZLOCALDURATION=0")
    let spatial = try await broker.scan()
    try check(spatial[0].media_revision != nil && spatial[0].completion_evidence == "finished" && spatial[0].duration_s == 1, "QTA uses readable audio duration when legacy scalar durations are absent")
    let spatialCopy = try await broker.snapshot(reference:spatial[0].source_reference,jobID:"spatial")
    try check(try Data(contentsOf:URL(fileURLWithPath:spatialCopy.owned_path)) == Data(contentsOf:qta), "QTA snapshot fully decodes and preserves source bytes")
    try sql("UPDATE ZCLOUDRECORDING SET ZDURATION=2")
    try check(try await broker.scan()[0].completion_evidence == "unknown", "QTA header mismatch with positive DB duration stays incomplete")
    try sql("UPDATE ZCLOUDRECORDING SET ZDURATION=0,ZFLAGS=6")
    try check(try await broker.scan()[0].completion_evidence == "unknown", "readable QTA still waits for pending download flag")
    try sql("UPDATE ZCLOUDRECORDING SET ZPATH='finished.wav',ZFLAGS=4")
    try Data("incomplete QuickTime data".utf8).write(to:folder.appendingPathComponent("broken.qta"))
    try sql("UPDATE ZCLOUDRECORDING SET ZPATH='broken.qta',ZDURATION=1,ZLOCALDURATION=1")
    try check(try await broker.scan()[0].completion_evidence == "unknown", "unreadable QTA header cannot inherit matching stale DB durations")
    try sql("UPDATE ZCLOUDRECORDING SET ZPATH='finished.wav'")
    try sql("UPDATE ZCLOUDRECORDING SET ZDURATION=2,ZLOCALDURATION=2")
    do { _ = try await broker.snapshot(reference:found[0].source_reference,jobID:"truncated"); throw NativeFailure.invalid("FAIL: wrong duration accepted") }
    catch { try check(!error.localizedDescription.contains("FAIL:"), "decodable truncated file is rejected by expected duration") }
    try sql("UPDATE ZCLOUDRECORDING SET ZDURATION=1,ZLOCALDURATION=1,ZPATH='../escape.wav'")
    let unsafe = try await broker.scan()
    try check(unsafe[0].media_revision == nil && unsafe[0].completion_evidence == "unknown", "Apple path traversal never exposes source")
    try sql("UPDATE ZCLOUDRECORDING SET ZPATH='finished.wav',ZEVICTIONDATE=1")
    try check(try await broker.scan().isEmpty, "deleted Apple records are excluded")
    try sql("UPDATE ZCLOUDRECORDING SET ZEVICTIONDATE=NULL; ALTER TABLE ZCLOUDRECORDING ADD COLUMN UNKNOWN_FIELD INTEGER")
    let changed = await broker.diagnostic()
    try check(!changed.schema_verified && changed.status == "unsupported_schema", "changed Apple schema fails closed despite same model hash")
}
