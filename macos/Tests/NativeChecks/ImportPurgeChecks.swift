import Foundation
import Darwin
import NativeCore

private func importPurgeRejects(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() }
    catch let error as BrokerFailure {
        try check(error.code == "import_purged", name)
        return
    }
    throw NativeFailure.invalid("FAIL: " + name)
}

func importPurgeChecks(root: URL) async throws {
    let fm = FileManager.default
    let fixture = root.appendingPathComponent("import-purge-" + UUID().uuidString)
    try fm.createDirectory(at:fixture,withIntermediateDirectories:true)
    defer { try? fm.removeItem(at:fixture) }
    let state = fixture.appendingPathComponent("state"), source = fixture.appendingPathComponent("source.wav")
    let bytes = wave()
    try bytes.write(to:source)
    let broker = ImportBroker(root:state)
    let memo = try await broker.importFile(source)
    let registryURL = state.appendingPathComponent("native/imports.json")
    func registry() throws -> [String:Any] {
        try JSONSerialization.jsonObject(with:Data(contentsOf:registryURL)) as! [String:Any]
    }
    func save(_ value: [String:Any]) throws {
        try JSONSerialization.data(withJSONObject:value,options:.sortedKeys).write(to:registryURL,options:.atomic)
    }
    var record = try registry()
    var entries = record["entries"] as! [String:[String:Any]]
    var entry = entries[memo.source_reference]!
    let cached = URL(fileURLWithPath:entry["path"] as! String)
    entry.removeValue(forKey:"purged"); entries[memo.source_reference] = entry; record["entries"] = entries
    try save(record)
    let legacy = ImportBroker(root:state)
    let legacyPending = try await legacy.pendingImports()
    try check(legacyPending.map(\.source_reference) == [memo.source_reference], "legacy schema-one import without purged remains pending")
    let legacyMemo = try await legacy.importFile(source)
    try check(legacyMemo.uuid == memo.uuid, "legacy registry without purged reuses import identity")

    entry["purged"] = true; entries[memo.source_reference] = entry; record["entries"] = entries
    try save(record)
    let tombstoned = ImportBroker(root:state)
    let pending = try await tombstoned.pendingImports()
    try check(pending.isEmpty, "purged import is excluded from pending deliveries")
    try await importPurgeRejects("purged import hash rejects while cached bytes still exist") { _ = try await tombstoned.importFile(source) }
    try await importPurgeRejects("purged snapshot rejects while cached bytes still exist") { _ = try await tombstoned.snapshot(reference:memo.source_reference,jobID:"purged-job") }
    try check(try Data(contentsOf:cached) == bytes && !fm.fileExists(atPath:state.appendingPathComponent("snapshots/jobs/purged-job").path), "purged rejection preserves cached bytes and creates no job snapshot")

    try fm.removeItem(at:cached)
    try await importPurgeRejects("purged hash rejects before opening deleted cached source") { _ = try await tombstoned.importFile(source) }
    try await importPurgeRejects("purged snapshot rejects before opening deleted cached source") { _ = try await tombstoned.snapshot(reference:memo.source_reference,jobID:"purged-job") }
    try check(try fm.contentsOfDirectory(atPath:cached.deletingLastPathComponent().path).isEmpty, "rejected purged imports remove temporary copied audio")
    try await tombstoned.markDelivered(reference:memo.source_reference)
    let saved = try registry()
    let savedEntry = (saved["entries"] as! [String:[String:Any]])[memo.source_reference]!
    try check(savedEntry["purged"] as? Bool == true && savedEntry["delivered"] as? Bool == true && savedEntry["hash"] as? String == entry["hash"] as? String && savedEntry["path"] as? String == entry["path"] as? String, "markDelivered preserves purged hash/path tombstone")

    var distinct = bytes; distinct[distinct.count - 1] = 1
    try distinct.write(to:source)
    let fresh = try await tombstoned.importFile(source)
    try check(fresh.uuid != memo.uuid, "different audio content can import alongside a purged hash")
    let freshPending = try await ImportBroker(root:state).pendingImports()
    try check(freshPending.map(\.source_reference) == [fresh.source_reference], "pending delivery keeps active entry and excludes purged entry")
    var withFalse = try registry(), falseEntries = withFalse["entries"] as! [String:[String:Any]]
    falseEntries[fresh.source_reference]?["purged"] = false; withFalse["entries"] = falseEntries
    try save(withFalse)
    let lockFD = open(state.appendingPathComponent("native/imports.lock").path,O_RDWR)
    guard lockFD >= 0 else { throw NativeFailure.invalid("FAIL: fixture imports lock") }
    do {
        defer { close(lockFD) }
        guard flock(lockFD,LOCK_EX | LOCK_NB) == 0 else { throw NativeFailure.invalid("FAIL: fixture imports lock acquisition") }
        do {
            _ = try await tombstoned.snapshot(reference:fresh.source_reference,jobID:"busy-job")
            throw NativeFailure.invalid("FAIL: snapshot ignored purge lock")
        } catch let error as BrokerFailure {
            try check(error.code == "broker_busy" && !fm.fileExists(atPath:state.appendingPathComponent("snapshots/jobs/busy-job").path), "snapshot respects shared backend purge lock before copying audio")
        }
    }
    let unpurgedSnapshot = try await tombstoned.snapshot(reference:fresh.source_reference,jobID:"active-job")
    try check(try Data(contentsOf:URL(fileURLWithPath:unpurgedSnapshot.owned_path)) == distinct, "explicit purged false retains snapshot behavior")
    try await tombstoned.markDelivered(reference:fresh.source_reference)
    let final = try registry(), finalEntries = final["entries"] as! [String:[String:Any]]
    try check(final["schema_version"] as? Int == 1 && finalEntries.count == 2 && finalEntries[fresh.source_reference]?["purged"] as? Bool == false && finalEntries[memo.source_reference]?["purged"] as? Bool == true, "native delivery rewrites preserve schema and sibling purge flags")
}
