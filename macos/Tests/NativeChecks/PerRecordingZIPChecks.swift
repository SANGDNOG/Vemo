import Foundation
import Darwin
import CryptoKit
import NativeCore

private actor ZIPMessages {
    var values: [String] = []
    func add(_ value: String) { values.append(value) }
}

private actor ZIPPublicationGate {
    private var entered = false
    private var continuation: CheckedContinuation<Void,Never>?
    func pause() async { entered = true; await withCheckedContinuation { continuation = $0 } }
    func wait() async { while !entered { await Task.yield() } }
    func resume() { continuation?.resume(); continuation = nil }
}

private func zipCommand(_ executable: String, _ arguments: [String]) throws -> Data {
    let process = Process(), output = Pipe()
    process.executableURL = URL(fileURLWithPath:executable)
    process.arguments = arguments; process.standardOutput = output; process.standardError = output
    try process.run()
    let data = output.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    guard process.terminationStatus == 0 else { throw NativeFailure.invalid("FAIL: ZIP fixture command") }
    return data
}

private func zipRejects(_ name: String, _ action: () async throws -> Void) async throws {
    do { try await action() } catch { print("PASS: " + name); return }
    throw NativeFailure.invalid("FAIL: " + name)
}

private func zipFixtureJob(_ id: String, root: URL) throws -> Job {
    try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:[
        "id":id,"uuid":id,"topic":"../회의: 원문","recording_date":"2026-10-13",
        "status":"completed","stage":"completed","elapsed_seconds":0,
        "title_status":"completed","alignment_status":"completed","notes_status":"completed",
        "audio_path":root.appendingPathComponent("audio.m4a").path,
        "artifact_path":root.appendingPathComponent("transcript.json").path,
        "notes_path":root.appendingPathComponent("notes.json").path
    ]))
}

// These fixtures exercise publication, not audio decoding; no live recordings are read.
func perRecordingZIPChecks(root: URL) async throws {
    let fm = FileManager.default
    let fixture = root.appendingPathComponent("per-recording-zip-" + UUID().uuidString)
    try fm.createDirectory(at:fixture,withIntermediateDirectories:true)
    defer { try? fm.removeItem(at:fixture) }
    let state = fixture.appendingPathComponent("state"), destination = fixture.appendingPathComponent("Drive")
    try fm.createDirectory(at:state,withIntermediateDirectories:true)
    try fm.createDirectory(at:destination,withIntermediateDirectories:true)
    let audio = Data("synthetic-original-audio".utf8), text = "정확한 원문입니다. English evidence."
    let transcript = try JSONSerialization.data(withJSONObject:["text":text,"segments":[],"alignment_status":"completed","extra":"retained"])
    let sourceHash = SHA256.hash(data:Data(text.utf8)).map { String(format:"%02x",$0) }.joined()
    let notes = try JSONSerialization.data(withJSONObject:["schema_version":1,"source_sha256":sourceHash,
        "summary":[["text":"원문 요약","quotes":["정확한 원문입니다."]]],
        "topics":[["keyword":"English","summary":"근거","quotes":["English evidence."]]],
        "highlights":[["quote":"English evidence."]]])
    let audioURL = state.appendingPathComponent("audio.m4a"), transcriptURL = state.appendingPathComponent("transcript.json"), notesURL = state.appendingPathComponent("notes.json")
    try audio.write(to:audioURL); try transcript.write(to:transcriptURL); try notes.write(to:notesURL)
    try Data("exclude this database".utf8).write(to:state.appendingPathComponent("queue.sqlite"))
    let job = try zipFixtureJob("one",root:state), secondJob = try zipFixtureJob("two",root:state)
    let first = try await RecordingZIPExport.exportOne(job:job,stateRoot:state,destination:destination)
    let bytes = try Data(contentsOf:first)
    let listing = String(decoding:try zipCommand("/usr/bin/unzip",["-Z1",first.path]),as:UTF8.self).split(separator:"\n").map(String.init).sorted()
    try check(listing == ["notes.md","original.m4a","transcript.txt"], "per-recording ZIP has exactly three root entries")
    let extracted = fixture.appendingPathComponent("extracted")
    _ = try zipCommand("/usr/bin/ditto",["-x","-k",first.path,extracted.path])
    for name in listing {
        var s = stat()
        try check(lstat(extracted.appendingPathComponent(name).path,&s) == 0 && s.st_mode & S_IFMT == S_IFREG, "ZIP entry is a regular file: " + name)
    }
    try check(try Data(contentsOf:extracted.appendingPathComponent("original.m4a")) == audio, "ZIP preserves original bytes")
    try check(try String(contentsOf:extracted.appendingPathComponent("transcript.txt"),encoding:.utf8) == "../회의: 원문 — 2026-10-13\n\n정확한 원문입니다. English evidence.\n", "ZIP includes exact titled transcript")
    let markdown = try String(contentsOf:extracted.appendingPathComponent("notes.md"),encoding:.utf8)
    try check(markdown.contains("# ../회의: 원문 — 2026-10-13") && markdown.contains("원문 요약") && markdown.contains("> English evidence."), "ZIP includes rendered validated notes")

    let store = PerRecordingZIPStore(root:state)
    let messages = ZIPMessages()
    let skipped = try await store.exportIfNeeded(job:job,destination:destination) { await messages.add($0) }
    try check(skipped == nil, "durable unchanged receipt skips after restart")
    let skipMessages = await messages.values
    try check(skipMessages == ["ZIP 입력 확인 중"], "unchanged reconciliation skips audio hashing, staging and compression")
    let second = try await store.exportIfNeeded(job:secondJob,destination:destination)
    try check(second != nil && second != first && (try fm.contentsOfDirectory(atPath:destination.path)).count == 2, "two recordings produce two ZIPs even with identical inputs")
    let replay = try await RecordingZIPExport.exportOne(job:job,stateRoot:state,destination:destination)
    try check(replay == first && (try Data(contentsOf:first)) == bytes, "exportOne reuses unchanged ZIP without clobber")

    var changed = job; changed.topic = "새 제목"
    let version = try await store.exportIfNeeded(job:changed,destination:destination)
    try check(version != nil && version != first && (try Data(contentsOf:first)) == bytes, "changed title creates immutable ZIP version")
    let oldContent = try await PerRecordingZIPStore(root:state).exportIfNeeded(job:job,destination:destination)
    try check(oldContent == nil && (try fm.contentsOfDirectory(atPath:destination.path)).count == 3, "returning to prior content reuses its durable receipt")

    var unknownDay = job; unknownDay.id = "unknown-day"; unknownDay.uuid = "unknown-day"; unknownDay.status = "needs_recording_date"; unknownDay.recording_date = nil; unknownDay.recorded_at = nil
    let noDate = try await store.exportIfNeeded(job:unknownDay,destination:destination)
    try check(noDate != nil, "ready notes and transcript export without inventing a recording date")
    for invalid in ["pending","running","failed"] {
        var unfinished = job; unfinished.notes_status = invalid
        try await zipRejects("unfinished notes reject ZIP: " + invalid) { _ = try await store.exportIfNeeded(job:unfinished,destination:destination) }
    }
    var trashed = job; trashed.deleted_at = "2026-10-13T00:00:00Z"
    try await zipRejects("trashed recording rejects ZIP") { _ = try await store.exportIfNeeded(job:trashed,destination:destination) }
    var incomplete = job; incomplete.status = "transcribing"
    try await zipRejects("incomplete transcript rejects ZIP") { _ = try await store.exportIfNeeded(job:incomplete,destination:destination) }
    try Data(#"{"text":"changed transcript","segments":[],"alignment_status":"completed"}"#.utf8).write(to:transcriptURL)
    try await zipRejects("stale notes reject ZIP even with previous receipt") { _ = try await store.exportIfNeeded(job:job,destination:destination) }
    try transcript.write(to:transcriptURL)

    let absent = fixture.appendingPathComponent("missing-destination")
    try await zipRejects("missing destination is retryable and not created") { _ = try await store.exportIfNeeded(job:job,destination:absent) }
    try check(!fm.fileExists(atPath:absent.path), "export never substitutes or creates an unavailable destination")
    try fm.createDirectory(at:absent,withIntermediateDirectories:true)
    let retry = try await store.exportIfNeeded(job:job,destination:absent)
    try check(retry != nil, "restored destination exports successfully")

    try fm.removeItem(at:first)
    let restored = try await store.exportIfNeeded(job:job,destination:destination)
    try check(restored == first, "missing archive is recreated at its vacant immutable path")
    let restoredBytes = try Data(contentsOf:first)
    try restoredBytes.write(to:first)
    let touched = try await store.exportIfNeeded(job:job,destination:destination)
    try check(touched == nil, "changed archive stamp with intact bytes is verified and reused")
    let foreign = Data("foreign changed archive: do not overwrite".utf8)
    try foreign.write(to:first)
    let repaired = try await store.exportIfNeeded(job:job,destination:destination)
    try check(repaired != nil && repaired != first && (try Data(contentsOf:first)) == foreign, "changed archive is preserved while a recovery ZIP is created")
    let repairedSkip = try await PerRecordingZIPStore(root:state).exportIfNeeded(job:job,destination:destination)
    try check(repairedSkip == nil, "recovery ZIP deduplicates across restart")

    let collision = fixture.appendingPathComponent("foreign-destination")
    try fm.createDirectory(at:collision,withIntermediateDirectories:true)
    let foreignTarget = collision.appendingPathComponent(first.lastPathComponent)
    try foreign.write(to:foreignTarget)
    let collisionZIP = try await store.exportIfNeeded(job:job,destination:collision)
    try check(collisionZIP != nil && collisionZIP != foreignTarget && (try Data(contentsOf:foreignTarget)) == foreign, "unreceipted filename collision never overwrites a cloud file")

    // The durable intent is written before publishing. Emulate a crash after rename,
    // before committing its archive stamp/current pointer, using only synthetic state.
    let receiptsRoot = state.appendingPathComponent("native/recording-zips")
    let enumerator = fm.enumerator(at:receiptsRoot,includingPropertiesForKeys:nil)!
    var recoveredIntent = false
    while let path = enumerator.nextObject() as? URL {
        guard path.pathExtension == "json", path.lastPathComponent != "current.json" else { continue }
        var record = (try JSONSerialization.jsonObject(with:Data(contentsOf:path))) as! [String:Any]
        guard record["destination"] as? String == collision.path, record["jobID"] as? String == job.id else { continue }
        record["archiveStamp"] = NSNull()
        try JSONSerialization.data(withJSONObject:record,options:.sortedKeys).write(to:path)
        try? fm.removeItem(at:path.deletingLastPathComponent().appendingPathComponent("current.json"))
        recoveredIntent = true
    }
    try check(recoveredIntent, "crash fixture contains a durable publication intent")
    let crashReplay = try await PerRecordingZIPStore(root:state).exportIfNeeded(job:job,destination:collision)
    try check(crashReplay == nil && (try fm.contentsOfDirectory(atPath:collision.path)).count == 2, "pending intent adopts its verified ZIP without a crash duplicate")

    let gate = ZIPPublicationGate()
    var cancelledJob = job; cancelledJob.id = "cancelled"; cancelledJob.uuid = "cancelled"
    let cancellationJob = cancelledJob
    let beforeCancellation = try fm.contentsOfDirectory(atPath:collision.path).sorted()
    let task = Task {
        try await store.exportIfNeeded(job:cancellationJob,destination:collision) { message in
            if message == "ZIP 저장 직전" { await gate.pause() }
        }
    }
    await gate.wait()
    // A second actor cannot race publication while the first awaits a callback.
    do {
        _ = try await PerRecordingZIPStore(root:state).exportIfNeeded(job:cancellationJob,destination:collision)
        throw NativeFailure.invalid("FAIL: concurrent ZIP writer accepted")
    } catch let error as BrokerFailure {
        try check(error.code == "broker_busy", "durable lock excludes concurrent actor publication")
    }
    task.cancel(); await gate.resume()
    do { _ = try await task.value; throw NativeFailure.invalid("FAIL: cancelled ZIP published") }
    catch is CancellationError { print("PASS: cancellation before rename publishes nothing") }
    try check(try fm.contentsOfDirectory(atPath:collision.path).sorted() == beforeCancellation, "cancellation removes staged ZIP and preserves destination files")
    let cancelledRetry = try await PerRecordingZIPStore(root:state).exportIfNeeded(job:cancellationJob,destination:collision)
    try check(cancelledRetry != nil, "pending intent without a published ZIP retries successfully")

    let symlinkedArchive = destination.appendingPathComponent(repaired!.lastPathComponent)
    try fm.removeItem(at:symlinkedArchive)
    try fm.createSymbolicLink(at:symlinkedArchive,withDestinationURL:foreignTarget)
    let linkRecovery = try await store.exportIfNeeded(job:job,destination:destination)
    var linkStat = stat()
    try check(linkRecovery != nil && lstat(symlinkedArchive.path,&linkStat) == 0 && linkStat.st_mode & S_IFMT == S_IFLNK && (try Data(contentsOf:foreignTarget)) == foreign, "changed archive symlink is preserved and never followed or overwritten")

    let oldDate = try fm.attributesOfItem(atPath:audioURL.path)[.modificationDate] as! Date
    try Data("synthetic-modified-audio".utf8).write(to:audioURL)
    try fm.setAttributes([.modificationDate:oldDate],ofItemAtPath:audioURL.path)
    let audioVersion = try await store.exportIfNeeded(job:job,destination:collision)
    try check(audioVersion != nil && audioVersion != collisionZIP, "audio content change invalidates receipt even with restored mtime")

    var outside = job; outside.audio_path = fixture.appendingPathComponent("outside.m4a").path
    try audio.write(to:URL(fileURLWithPath:outside.audio_path!))
    try await zipRejects("unowned audio rejects ZIP") { _ = try await store.exportIfNeeded(job:outside,destination:destination) }
    let link = state.appendingPathComponent("linked.m4a")
    try fm.createSymbolicLink(at:link,withDestinationURL:audioURL)
    var linked = job; linked.audio_path = link.path
    try await zipRejects("linked input rejects ZIP") { _ = try await store.exportIfNeeded(job:linked,destination:destination) }
    let partials = try fm.contentsOfDirectory(atPath:destination.path).filter { $0.hasPrefix(".vemo-zip-") }
    try check(partials.isEmpty, "completed and rejected exports leave no partial ZIPs")
}
