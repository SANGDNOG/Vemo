import Foundation
import Darwin
import CryptoKit
import NativeCore

private actor MirrorIndependentMessages {
    private var messages: [String] = []
    func add(_ value: String) { messages.append(value) }
    func values() -> [String] { messages }
}

private func mirrorIndependentRequired(_ value: URL?, _ reason: String) throws -> URL {
    guard let value else { throw NativeFailure.invalid("FAIL: " + reason) }
    return value
}

private func mirrorIndependentInode(_ url: URL) throws -> UInt64 {
    var info = stat()
    guard lstat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFREG else {
        throw NativeFailure.invalid("FAIL: expected synthetic regular archive")
    }
    return UInt64(info.st_ino)
}

private func mirrorIndependentArchives(_ root: URL) throws -> [URL] {
    guard let files = FileManager.default.enumerator(at:root, includingPropertiesForKeys:[.isSymbolicLinkKey]) else {
        throw NativeFailure.invalid("FAIL: synthetic export enumeration")
    }
    var result: [URL] = []
    for case let url as URL in files {
        if try url.resourceValues(forKeys:[.isSymbolicLinkKey]).isSymbolicLink == true { files.skipDescendants(); continue }
        if url.pathExtension == "zip" { result.append(url) }
    }
    return result
}

private struct MirrorIndependentFixture {
    let root: URL, state: URL, destination: URL
    let audio: URL, transcript: URL, notes: URL
    let job: Job

    init(parent: URL, name: String) throws {
        let fm = FileManager.default
        root = parent.appendingPathComponent(name)
        state = root.appendingPathComponent("state")
        destination = root.appendingPathComponent("export-root")
        try fm.createDirectory(at:state, withIntermediateDirectories:true)
        try fm.createDirectory(at:destination, withIntermediateDirectories:true)
        audio = state.appendingPathComponent("audio.m4a")
        transcript = state.appendingPathComponent("transcript.json")
        notes = state.appendingPathComponent("notes.json")
        let text = "합성 강의 자료입니다. 실제 녹음이 아닙니다."
        try Data("independent synthetic audio bytes".utf8).write(to:audio)
        try JSONSerialization.data(withJSONObject:["text":text,"segments":[],"alignment_status":"completed"]).write(to:transcript)
        let digest = SHA256.hash(data:Data(text.utf8)).map { String(format:"%02x", $0) }.joined()
        try JSONSerialization.data(withJSONObject:["schema_version":1,"source_sha256":digest,
            "summary":[["text":"합성 자료","quotes":[text]]],"topics":[],"highlights":[]]).write(to:notes)
        job = try JSONDecoder().decode(Job.self, from:JSONSerialization.data(withJSONObject:[
            "id":"independent-mirror", "uuid":"independent-mirror", "topic":"합성 강의",
            "recording_date":"2026-10-13", "status":"completed", "stage":"completed", "elapsed_seconds":0,
            "title_status":"completed", "alignment_status":"completed", "notes_status":"completed",
            "audio_path":audio.path, "artifact_path":transcript.path, "notes_path":notes.path]))
    }

    func organization(parent: String = "Study", child: String = "Physics", outer: String? = nil,
                      revision: Int = 1) -> LibraryOrganization {
        var folders = [LibraryFolder(id:"parent",name:parent,parentID:outer == nil ? nil : "outer"),
                       LibraryFolder(id:"child",name:child,parentID:"parent")]
        if let outer { folders.append(LibraryFolder(id:"outer",name:outer)) }
        return LibraryOrganization(revision:revision,folders:folders,assignments:[job.id:"child"])
    }
}

/// Call from NativeChecks only; all inputs, receipts and destination files are synthetic.
func zipFolderMirrorIndependentChecks(root: URL) async throws {
    let fm = FileManager.default
    let parent = root.appendingPathComponent("independent-zip-mirror-" + UUID().uuidString)
    try fm.createDirectory(at:parent, withIntermediateDirectories:true)
    defer { try? fm.removeItem(at:parent) }

    let fixture = try MirrorIndependentFixture(parent:parent,name:"layout")
    let store = PerRecordingZIPStore(root:fixture.state)
    let first = try mirrorIndependentRequired(
        try await store.exportIfNeeded(job:fixture.job,destination:fixture.destination), "initial root ZIP")
    let bytes = try Data(contentsOf:first), inode = try mirrorIndependentInode(first)
    // Directory URL equality distinguishes an incidental trailing slash; compare locations.
    try check(first.deletingLastPathComponent().path == fixture.destination.path, "independent unfiled ZIP starts at export root")

    // Relocation must not require eligible notes or readable input files. Withholding
    // every input also makes accidental recompression a real failure, not a log check.
    let inputs = [fixture.audio,fixture.transcript,fixture.notes]
    for input in inputs { try fm.moveItem(at:input,to:input.appendingPathExtension("held")) }
    let messages = MirrorIndependentMessages()
    let nested = try mirrorIndependentRequired(try await store.relocateIfNeeded(
        jobID:fixture.job.id,destination:fixture.destination,organization:fixture.organization()) {
            await messages.add($0)
        }, "receipt-only nested relocation")
    try check(nested.deletingLastPathComponent().path == fixture.destination.appendingPathComponent("Study/Physics").path, "independent nested layout mirrors both ancestors")
    try check(!fm.fileExists(atPath:first.path) && (try Data(contentsOf:nested)) == bytes && mirrorIndependentInode(nested) == inode,
              "independent relocation moves original inode and exact bytes without a duplicate")
    let rename = fixture.organization(parent:"Learning",child:"Waves",revision:2)
    let renamed = try mirrorIndependentRequired(try await PerRecordingZIPStore(root:fixture.state).relocateIfNeeded(
        jobID:fixture.job.id,destination:fixture.destination,organization:rename), "restart folder rename")
    try check(renamed.deletingLastPathComponent().path == fixture.destination.appendingPathComponent("Learning/Waves").path && !fm.fileExists(atPath:nested.path),
              "independent restarted actor follows folder and ancestor rename")
    let movedOrganization = fixture.organization(parent:"Learning",child:"Waves",outer:"Archive",revision:3)
    let moved = try mirrorIndependentRequired(try await store.relocateIfNeeded(
        jobID:fixture.job.id,destination:fixture.destination,organization:movedOrganization), "parent move")
    try check(moved.deletingLastPathComponent().path == fixture.destination.appendingPathComponent("Archive/Learning/Waves").path,
              "independent parent move relocates descendant ZIP")
    let unfiled = try mirrorIndependentRequired(try await store.relocateIfNeeded(
        jobID:fixture.job.id,destination:fixture.destination,organization:LibraryOrganization(revision:4)), "return to unfiled")
    try check(unfiled == first && mirrorIndependentInode(unfiled) == inode && (try Data(contentsOf:unfiled)) == bytes,
              "independent unfiled relocation returns same archive to root")
    let replay = try await PerRecordingZIPStore(root:fixture.state).relocateIfNeeded(
        jobID:fixture.job.id,destination:fixture.destination,organization:LibraryOrganization(revision:4))
    try check(replay == nil && (try mirrorIndependentArchives(fixture.destination)).count == 1, "independent repeated layout is a no-op with exactly one ZIP")
    let phases = await messages.values()
    try check(!phases.contains(where:{$0.contains("압축") || $0.contains("녹음 복사") || $0.contains("오디오 확인")}),
              "independent receipt relocation never stages or compresses source")
    for input in inputs { try fm.moveItem(at:input.appendingPathExtension("held"),to:input) }

    let integrated = try mirrorIndependentRequired(try await store.exportIfNeeded(
        job:fixture.job,destination:fixture.destination,organization:fixture.organization()), "export also relocates")
    try check(integrated.deletingLastPathComponent().path == nested.deletingLastPathComponent().path && mirrorIndependentInode(integrated) == inode,
              "independent export API relocates existing receipt without recompression")

    // Same clean name and unrelated legacy ZIPs are foreign until a receipt proves
    // ownership. Neither matching names nor matching bytes authorize their removal.
    let collisions = try MirrorIndependentFixture(parent:parent,name:"collisions")
    let collisionStore = PerRecordingZIPStore(root:collisions.state)
    let old = try mirrorIndependentRequired(try await collisionStore.exportIfNeeded(job:collisions.job,destination:collisions.destination), "collision initial ZIP")
    let oldBytes = try Data(contentsOf:old), oldInode = try mirrorIndependentInode(old)
    let targetFolder = collisions.destination.appendingPathComponent("Study/Physics")
    try fm.createDirectory(at:targetFolder,withIntermediateDirectories:true)
    let foreign = targetFolder.appendingPathComponent(old.lastPathComponent)
    try oldBytes.write(to:foreign)
    let sentinel = collisions.destination.appendingPathComponent("personal-file.txt")
    try Data("unrelated export content".utf8).write(to:sentinel)
    let relocated = try mirrorIndependentRequired(try await collisionStore.relocateIfNeeded(
        jobID:collisions.job.id,destination:collisions.destination,organization:collisions.organization()), "collision move")
    try check(relocated != foreign && relocated.deletingLastPathComponent().path == targetFolder.path && mirrorIndependentInode(relocated) == oldInode,
              "independent occupied identical-byte ZIP gets distinct non-overwriting target")
    try check((try Data(contentsOf:foreign)) == oldBytes && (try String(contentsOf:sentinel,encoding:.utf8)) == "unrelated export content",
              "independent external files and identical-byte unowned archive remain untouched")

    let links = try MirrorIndependentFixture(parent:parent,name:"symlink")
    let linkStore = PerRecordingZIPStore(root:links.state)
    let linkOld = try mirrorIndependentRequired(try await linkStore.exportIfNeeded(job:links.job,destination:links.destination), "symlink initial ZIP")
    let linkBytes = try Data(contentsOf:linkOld)
    let outside = links.root.appendingPathComponent("outside")
    try fm.createDirectory(at:outside,withIntermediateDirectories:true)
    try fm.createSymbolicLink(at:links.destination.appendingPathComponent("Study"),withDestinationURL:outside)
    var rejectedLink = false
    do { _ = try await linkStore.relocateIfNeeded(jobID:links.job.id,destination:links.destination,organization:links.organization()) }
    catch { rejectedLink = true }
    try check(rejectedLink && (try Data(contentsOf:linkOld)) == linkBytes && (try fm.contentsOfDirectory(atPath:outside.path)).isEmpty,
              "independent symlink ancestor rejects without moving old archive or touching outside")

    let missing = try MirrorIndependentFixture(parent:parent,name:"missing")
    let missingStore = PerRecordingZIPStore(root:missing.state)
    let removed = try mirrorIndependentRequired(try await missingStore.exportIfNeeded(job:missing.job,destination:missing.destination), "missing initial ZIP")
    try fm.removeItem(at:removed)
    let skipped = try await missingStore.relocateIfNeeded(jobID:missing.job.id,destination:missing.destination,organization:missing.organization())
    try check(skipped == nil && (try mirrorIndependentArchives(missing.destination)).isEmpty, "independent missing archive relocation skips rather than recompressing")
    let repaired = try mirrorIndependentRequired(try await PerRecordingZIPStore(root:missing.state).exportIfNeeded(
        job:missing.job,destination:missing.destination,organization:missing.organization()), "eligible missing archive repair")
    try check(repaired.deletingLastPathComponent().path == missing.destination.appendingPathComponent("Study/Physics").path && (try mirrorIndependentArchives(missing.destination)).count == 1,
              "independent later eligible export repairs missing archive in current layout")
}
