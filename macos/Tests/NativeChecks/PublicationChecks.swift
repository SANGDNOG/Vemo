import Foundation
import NativeCore
import CryptoKit
func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
func publicationChecks(root: URL) async throws {
    let state = root.appendingPathComponent("publish-state"), output = root.appendingPathComponent("output")
    let local = state.appendingPathComponent("artifacts/job/versions/pub1")
    try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
    let body = Data("회의 — 2026-10-10\n\n원문\n".utf8), name = "회의 — 2026-10-10.txt", source = local.appendingPathComponent("회의 — 2026-10-10.txt")
    try body.write(to: source)
    func manifest(_ id: String, data: Data = body) throws -> URL {
        let m: [String:Any] = ["schema_version":1,"publication_id":id,"job_id":"job","uuid":"memo","recording_date":"2026-10-10","title":"회의 — 2026-10-10","revision":["number":1],"files":[["name":name,"path":source.path,"sha256":digest(data)]]]
        let path = local.appendingPathComponent(id + ".json"); try JSONSerialization.data(withJSONObject:m).write(to:path); return path
    }
    let first = try manifest("pub1"), broker = PublishBroker(stateRoot: state)
    let receipt = try await broker.publish(manifestPath:first.path, jobID:"job", publicationID:"pub1", outputRoot:output)
    let active = output.appendingPathComponent("2026/10/memo/manifest.json"), version = active.deletingLastPathComponent().appendingPathComponent("versions/pub1")
    let file = version.appendingPathComponent(name)
    try check(try Data(contentsOf:file) == body, "publication copies verified content")
    let before = try FileManager.default.attributesOfItem(atPath:active.path)[.modificationDate] as! Date
    let replay = try await PublishBroker(stateRoot:state).publish(manifestPath:first.path, jobID:"job", publicationID:"pub1", outputRoot:output)
    try check(replay.artifact_hashes == receipt.artifact_hashes && replay.output_manifest_path == receipt.output_manifest_path, "ACK replay returns exact receipt")
    try check(try FileManager.default.attributesOfItem(atPath:active.path)[.modificationDate] as! Date == before, "ACK replay does not rewrite active manifest")
    try Data("user edit".utf8).write(to:file)
    do { _ = try await broker.publish(manifestPath:first.path, jobID:"job", publicationID:"pub1", outputRoot:output); throw NativeFailure.invalid("FAIL: user output overwritten") } catch { try check(!error.localizedDescription.contains("FAIL:"), "edited publication conflicts") }
    try check(try Data(contentsOf:file) == Data("user edit".utf8), "user edited output preserved")
    try body.write(to:file)
    let second = try manifest("pub2")
    _ = try await broker.publish(manifestPath:second.path, jobID:"job", publicationID:"pub2", outputRoot:output)
    try check(FileManager.default.fileExists(atPath:file.path), "prior immutable revision retained")
    let oldReplay = try await broker.publish(manifestPath:first.path, jobID:"job", publicationID:"pub1", outputRoot:output)
    try check(URL(fileURLWithPath:oldReplay.output_manifest_path).deletingLastPathComponent() == version, "old replay receipts point at immutable manifest")
    let third = try manifest("pub3")
    let partial = active.deletingLastPathComponent().appendingPathComponent(".staging/pub3")
    try FileManager.default.createDirectory(at:partial, withIntermediateDirectories:true)
    try JSONSerialization.data(withJSONObject:["schema_version":1,"publication_id":"pub3","job_id":"job","uuid":"memo","recording_date":"2026-10-10","title":"회의 — 2026-10-10","revision":["number":1],"files":[["name":name,"path":active.deletingLastPathComponent().appendingPathComponent("versions/pub3").appendingPathComponent(name).path,"sha256":digest(body)]]]).write(to:partial.appendingPathComponent("manifest.json"))
    _ = try await broker.publish(manifestPath:third.path, jobID:"job", publicationID:"pub3", outputRoot:output)
    try check(!FileManager.default.fileExists(atPath:partial.path), "partial staging recovery activates complete revision")
    let wrong = try manifest("bad-hash", data:Data("wrong".utf8))
    do { _ = try await broker.publish(manifestPath:wrong.path, jobID:"job", publicationID:"bad-hash", outputRoot:output); throw NativeFailure.invalid("FAIL: source hash ignored") } catch { try check(!error.localizedDescription.contains("FAIL:"), "source hash mismatch rejected") }
    try check(try await broker.activeRevision(uuid:"memo", recordingDate:"2026-10-10", outputRoot:output).lastPathComponent == "pub3", "result folder resolves active version")
    try FileManager.default.removeItem(at:state.appendingPathComponent("native/publications"))
    let receiptWithoutJournal = try await broker.publish(manifestPath:third.path,jobID:"job",publicationID:"pub3",outputRoot:output)
    try check(receiptWithoutJournal.publication_id == "pub3", "exact active publication replays without private journal")
    try FileManager.default.removeItem(at:active)
    do {
        _ = try await broker.publish(manifestPath:third.path,jobID:"job",publicationID:"pub3",outputRoot:output)
        throw NativeFailure.invalid("FAIL: unactivated revision produced successful ACK")
    } catch { try check(!error.localizedDescription.contains("FAIL:"), "missing activation journal and active manifest cannot ACK a complete revision") }
    try check(!FileManager.default.fileExists(atPath:active.path), "missing activation evidence is preserved for review")
    try check(try Data(contentsOf:active.deletingLastPathComponent().appendingPathComponent("versions/pub3").appendingPathComponent(name)) == body, "orphan complete revision retained without false success")
}
