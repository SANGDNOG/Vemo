import Foundation
import Darwin
import CryptoKit
import NativeCore

private actor FolderZIPMessages {
    var values: [String] = []
    func add(_ value: String) { values.append(value) }
}
private actor FolderZIPGate {
    private var entered = false
    private var continuation: CheckedContinuation<Void,Never>?
    func pause() async { entered = true; await withCheckedContinuation { continuation = $0 } }
    func wait() async { while !entered { await Task.yield() } }
    func resume() { continuation?.resume(); continuation = nil }
}
private func folderZIPRejected(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() } catch { print("PASS: " + name); return }
    throw NativeFailure.invalid("FAIL: " + name)
}
private func folderZIPFiles(_ root: URL) throws -> [URL] {
    let enumerator = FileManager.default.enumerator(at:root,includingPropertiesForKeys:nil)!
    return enumerator.allObjects.compactMap { $0 as? URL }.filter { $0.pathExtension == "zip" }
}

func zipFolderChecks(root: URL) async throws {
    let fm = FileManager.default
    let fixture = root.appendingPathComponent("zip-folder-" + UUID().uuidString)
    try fm.createDirectory(at:fixture,withIntermediateDirectories:true)
    defer { try? fm.removeItem(at:fixture) }
    let state = fixture.appendingPathComponent("state"), output = fixture.appendingPathComponent("exports")
    try fm.createDirectory(at:state,withIntermediateDirectories:true)
    try fm.createDirectory(at:output,withIntermediateDirectories:true)
    let audio = state.appendingPathComponent("original.m4a"), artifact = state.appendingPathComponent("transcript.json"), notes = state.appendingPathComponent("notes.json")
    try Data("synthetic original".utf8).write(to:audio)
    try Data(#"{"text":"Evidence.","segments":[],"alignment_status":"completed"}"#.utf8).write(to:artifact)
    // SHA-256 of "Evidence."; obtain a legitimate notes fixture through CryptoKit below.
    let text = "Evidence."
    let sourceHash = SHA256.hash(data:Data(text.utf8)).map { String(format:"%02x",$0) }.joined()
    try JSONSerialization.data(withJSONObject:["schema_version":1,"source_sha256":sourceHash,"summary":[["text":"Summary","quotes":[text]]],"topics":[],"highlights":[]]).write(to:notes)
    let job = try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:[
        "id":"folder-job","uuid":"folder-memo","topic":"회의","recording_date":"2026-10-13",
        "status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed","notes_status":"completed",
        "audio_path":audio.path,"artifact_path":artifact.path,"notes_path":notes.path]))
    let folders = [LibraryFolder(id:"a",name:"팀/회의"),LibraryFolder(id:"b",name:"팀:회의"),
        LibraryFolder(id:"c",name:"하위",parentID:"b"),LibraryFolder(id:"d",name:"팀_회의 (2)"),
        LibraryFolder(id:"e",name:"...",parentID:"c")]
    var organization = LibraryOrganization(folders:folders,assignments:[job.id:"e"])
    try check(try ZIPFolderLayout.components(jobID:job.id,organization:organization) == ["팀_회의 (2)","하위","폴더"], "ZIP folder layout mirrors sanitized full ancestry")
    var reordered = organization; reordered.folders.reverse()
    try check(try ZIPFolderLayout.components(jobID:job.id,organization:reordered) == ["팀_회의 (2)","하위","폴더"], "sanitized folder collision names are deterministic across input order")
    try check(try ZIPFolderLayout.components(jobID:"unfiled",organization:organization).isEmpty, "unfiled ZIP layout remains export root")
    var literal = organization; literal.assignments[job.id] = "d"
    try check(try ZIPFolderLayout.components(jobID:job.id,organization:literal) == ["팀_회의 (2) (2)"], "literal numbered folder also avoids sanitized sibling collision")
    let caseFolders = [LibraryFolder(id:"a",name:"Team"),LibraryFolder(id:"b",name:"team"),LibraryFolder(id:"c",name:"é"),LibraryFolder(id:"d",name:"e\u{301}")]
    let caseOrganization = LibraryOrganization(folders:caseFolders,assignments:["case":"b","unicode":"d"])
    try check(try ZIPFolderLayout.components(jobID:"case",organization:caseOrganization) == ["team (2)"] && ZIPFolderLayout.components(jobID:"unicode",organization:caseOrganization) == ["é (2)"], "case and Unicode equivalent sibling folders never merge")
    var cyclic = organization; cyclic.folders[0].parentID = "e"; cyclic.folders[1].parentID = "a"
    try await folderZIPRejected("cyclic folder graph rejects export paths") { _ = try ZIPFolderLayout.components(jobID:job.id,organization:cyclic) }

    let store = PerRecordingZIPStore(root:state)
    let first = try await RecordingZIPExport.exportOne(job:job,stateRoot:state,destination:output)
    let firstBytes = try Data(contentsOf:first)
    var versionJob = job; versionJob.topic = "다른 제목"
    let second = try await RecordingZIPExport.exportOne(job:versionJob,stateRoot:state,destination:output)
    let secondBytes = try Data(contentsOf:second)
    let foreign = output.appendingPathComponent("unrelated.zip")
    try Data("foreign ZIP".utf8).write(to:foreign)
    let messages = FolderZIPMessages()
    let moved = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization) { await messages.add($0) }
    let assigned = output.appendingPathComponent("팀_회의 (2)/하위/폴더")
    try check(moved?.deletingLastPathComponent() == assigned && !fm.fileExists(atPath:first.path) && !fm.fileExists(atPath:second.path), "legacy root receipts relocate every ZIP version into assigned ancestry")
    try check(try Data(contentsOf:assigned.appendingPathComponent(first.lastPathComponent)) == firstBytes && Data(contentsOf:assigned.appendingPathComponent(second.lastPathComponent)) == secondBytes, "folder relocation preserves exact archive bytes for every version")
    try check(try Data(contentsOf:foreign) == Data("foreign ZIP".utf8), "folder relocation preserves unrelated export files")
    let values = await messages.values
    try check(!values.contains(where:{ $0.contains("압축") || $0.contains("오디오") }), "legacy folder migration moves verified ZIPs without recompression or audio hashing")
    let replay = try await PerRecordingZIPStore(root:state).relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    try check(replay == nil && (try folderZIPFiles(output)).count == 3, "folder relocation deduplicates after actor restart")
    let exportReplay = try await store.exportIfNeeded(job:versionJob,destination:output,organization:organization)
    try check(exportReplay == nil, "export reuses migrated receipt instead of creating a hierarchy duplicate")

    // Organization-only reconciliation must not require available transcript or notes.
    try fm.removeItem(at:notes); try fm.removeItem(at:artifact)
    organization.folders[1].name = "새 팀"; organization.folders[2].parentID = "a"
    let renamed = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    let reparented = output.appendingPathComponent("팀_회의/하위/폴더")
    try check(renamed?.deletingLastPathComponent() == reparented && (try folderZIPFiles(reparented)).count == 2, "parent move relocates existing ZIPs even without transcript or notes")
    organization.folders[0].name = "새 부모"
    let renameParent = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    try check(renameParent?.deletingLastPathComponent() == output.appendingPathComponent("새 부모/하위/폴더"), "ancestor rename updates mirrored ZIP hierarchy")

    let anotherRoot = fixture.appendingPathComponent("other-exports")
    try fm.createDirectory(at:anotherRoot,withIntermediateDirectories:true)
    let other = try await store.relocateIfNeeded(jobID:job.id,destination:anotherRoot,organization:organization)
    try check(other == nil && (try folderZIPFiles(output)).count == 3 && (try folderZIPFiles(anotherRoot)).isEmpty, "changing configured root preserves all prior-root exports")

    // Occupied clean filename includes identical bytes: it is still foreign without intent.
    organization.assignments.removeValue(forKey:job.id)
    try firstBytes.write(to:first)
    let unfiled = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    try check(unfiled?.deletingLastPathComponent().path == output.path && (try Data(contentsOf:first)) == firstBytes && (try folderZIPFiles(output)).count == 4, "unfiling preserves occupied foreign ZIP and moves versions with collision suffix")
    let rootArchives = try fm.contentsOfDirectory(at:output,includingPropertiesForKeys:nil).filter { $0.pathExtension == "zip" }
    try check(rootArchives.contains { $0.lastPathComponent == "회의 — 2026-10-13 (2).zip" }, "folder relocation uses readable numbered ZIP collision")

    // Cancel after durable move intent, then emulate death after the exclusive rename.
    organization.assignments[job.id] = "a"
    let gate = FolderZIPGate(), crashOrganization = organization
    let moving = Task {
        try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:crashOrganization) { message in
            if message == "ZIP 폴더 이동 직전" { await gate.pause() }
        }
    }
    await gate.wait(); moving.cancel(); await gate.resume()
    do { _ = try await moving.value; throw NativeFailure.invalid("FAIL: cancelled folder move succeeded") }
    catch is CancellationError { print("PASS: folder relocation cancellation before rename preserves sources") }
    let receiptRoot = state.appendingPathComponent("native/recording-zips")
    let intentPaths = (fm.enumerator(at:receiptRoot,includingPropertiesForKeys:nil)!.allObjects as! [URL]).filter { $0.lastPathComponent == "move.json" }
    try check(intentPaths.count == 1, "cancelled folder move retains one durable intent for restart")
    let intent = try JSONSerialization.jsonObject(with:Data(contentsOf:intentPaths[0])) as! [String:Any]
    let sourceReceipt = intent["source"] as! [String:Any], targetReceipt = intent["target"] as! [String:Any]
    let moveSource = URL(fileURLWithPath:sourceReceipt["destination"] as! String).appendingPathComponent(sourceReceipt["archiveName"] as! String)
    let moveTarget = URL(fileURLWithPath:targetReceipt["destination"] as! String).appendingPathComponent(targetReceipt["archiveName"] as! String)
    let crashBytes = try Data(contentsOf:moveSource)
    try fm.moveItem(at:moveSource,to:moveTarget)
    let resumed = try await PerRecordingZIPStore(root:state).relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    try check(resumed != nil && (try Data(contentsOf:moveTarget)) == crashBytes && !fm.fileExists(atPath:intentPaths[0].path) && (try folderZIPFiles(output)).count == 4, "crash after rename resumes intent without duplicate archives")

    // Missing receipt-backed versions remain recorded, never interpreted as deletion authority.
    let missing = try folderZIPFiles(output.appendingPathComponent("새 부모")).first!
    try fm.removeItem(at:missing)
    organization.assignments.removeValue(forKey:job.id)
    _ = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization)
    let journals = (fm.enumerator(at:receiptRoot,includingPropertiesForKeys:nil)!.allObjects as! [URL]).filter { $0.pathExtension == "json" && $0.lastPathComponent != "current.json" }
    try check(journals.count == 2, "missing archive leaves its durable receipt intact during relocation")

    let outside = fixture.appendingPathComponent("outside")
    try fm.createDirectory(at:outside,withIntermediateDirectories:true)
    try fm.createSymbolicLink(at:output.appendingPathComponent("새 팀"),withDestinationURL:outside)
    organization.assignments[job.id] = "b"
    try await folderZIPRejected("symlink in mirrored folder path rejects relocation") { _ = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization) }
    try check(try fm.contentsOfDirectory(atPath:outside.path).isEmpty, "unsafe folder rejection never writes outside export root")
    let noReceipt = try await store.relocateIfNeeded(jobID:"never-exported",destination:output,organization:LibraryOrganization())
    try check(noReceipt == nil, "folder reconciliation without receipts is a no-op")

    // Changed archive bytes or invalid metadata must never authorize source removal.
    organization.assignments.removeValue(forKey:job.id)
    let intact = try folderZIPFiles(output).first { $0.lastPathComponent != first.lastPathComponent && $0 != foreign }!
    let intactBytes = try Data(contentsOf:intact)
    try Data("external archive edit".utf8).write(to:intact)
    organization.assignments[job.id] = "a"
    let namesBefore = try folderZIPFiles(output).map(\.path).sorted()
    try await folderZIPRejected("changed receipt-backed archive rejects relocation before moving other versions") { _ = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization) }
    try check(try folderZIPFiles(output).map(\.path).sorted() == namesBefore && Data(contentsOf:intact) == Data("external archive edit".utf8), "changed ZIP rejection preserves all sources and external edits")
    try intactBytes.write(to:intact)
    let journal = journals.first { path in
        let value = try! JSONSerialization.jsonObject(with:Data(contentsOf:path)) as! [String:Any]
        return value["archiveName"] as? String == intact.lastPathComponent
    }!
    let validJournal = try Data(contentsOf:journal)
    var invalidJournal = try JSONSerialization.jsonObject(with:validJournal) as! [String:Any]
    invalidJournal["archiveName"] = "../outside.zip"
    try JSONSerialization.data(withJSONObject:invalidJournal).write(to:journal)
    try await folderZIPRejected("invalid receipt path rejects relocation without deleting source") { _ = try await store.relocateIfNeeded(jobID:job.id,destination:output,organization:organization) }
    try check(try Data(contentsOf:intact) == intactBytes && Data(contentsOf:journal) != validJournal, "invalid receipt and its source ZIP are both preserved")
    try validJournal.write(to:journal)
}
