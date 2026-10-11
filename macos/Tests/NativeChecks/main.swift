import Foundation
import NativeCore
func check(_ condition: Bool, _ name: String) throws { if !condition { throw NativeFailure.invalid("FAIL: \(name)") }; print("PASS: \(name)") }
func rejects(_ name: String, _ action: () throws -> Void) throws { do { try action() } catch { print("PASS: \(name)"); return }; throw NativeFailure.invalid("FAIL: \(name)") }
func wave() -> Data {
    var d = Data(); func ascii(_ s: String) { d.append(contentsOf: s.utf8) }; func n<T: FixedWidthInteger>(_ v: T) { var value = v.littleEndian; withUnsafeBytes(of: &value) { d.append(contentsOf: $0) } }
    ascii("RIFF"); n(UInt32(36 + 16000)); ascii("WAVEfmt "); n(UInt32(16)); n(UInt16(1)); n(UInt16(1)); n(UInt32(8000)); n(UInt32(16000)); n(UInt16(2)); n(UInt16(16)); ascii("data"); n(UInt32(16000)); d.append(Data(repeating: 0, count: 16000)); return d
}
@main struct Checks {
    static func main() async {
        do { try await run() } catch { try? FileHandle.standardError.write(contentsOf: Data("FAIL: \(error.localizedDescription)\n".utf8)); exit(1) }
    }
    static func run() async throws {
        if CommandLine.arguments.contains("--runtime-audio-checks") { try runtimePathChecks(); try await audioNormalizationChecks(); print("All runtime/audio checks passed"); return }
        if CommandLine.arguments.contains("--transcript-display-checks") { try transcriptDisplayChecks(); print("All transcript display checks passed"); return }
        if CommandLine.arguments.contains("--playback-checks") { try playbackChecks(); print("All playback checks passed"); return }
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/NativeChecks-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try check(try Validation.recordingDate("2024-02-29") == "2024-02-29", "leap recording date")
        for date in ["2023-02-29", "2026-13-01", "2026-1-1", "2026-10-11T00:00:00"] { try rejects("invalid date \(date)") { _ = try Validation.recordingDate(date) } }
        let file = root.appendingPathComponent("ok"); try Data().write(to: file)
        try check(try Validation.owned(file.path, root: root) == file, "owned file")
        try rejects("traversal") { _ = try Validation.owned(root.appendingPathComponent("../outside").path, root: root) }
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("escape"), withDestinationURL: URL(fileURLWithPath: "/tmp"))
        try rejects("symlink escape") { _ = try Validation.owned(root.appendingPathComponent("escape/file").path, root: root) }
        try rejects("protocol mismatch") { _ = try WireCodec.decode(Data(#"{"sequence":1,"protocol_version":2,"kind":"snapshot","payload":{}}"#.utf8), after: 0) }
        try rejects("sequence replay") { _ = try WireCodec.decode(Data(#"{"sequence":1,"protocol_version":1,"kind":"snapshot","payload":{}}"#.utf8), after: 1) }
        try rejects("oversize line") { _ = try WireCodec.decode(Data(repeating: 32, count: WireCodec.limit + 1), after: 0) }
        let nanoseconds: Int64 = 1_791_693_860_206_585_244
        let precise = try JSONValue.encoded(["mtime_ns":nanoseconds,"size":16_044])
        let encoded = try JSONEncoder().encode(precise)
        try check(String(decoding:encoded,as:UTF8.self).contains(String(nanoseconds)), "nanosecond revision retains exact integer wire encoding")
        try check(try precise.decoded([String:Int64].self)["mtime_ns"] == nanoseconds, "nanosecond revision round trip")
        let fractional = try JSONDecoder().decode(JSONValue.self,from:Data("1.25".utf8))
        try check(String(decoding:try JSONEncoder().encode(fractional),as:UTF8.self) == "1.25", "fractional wire values remain fractional")
        let transcript = try JSONDecoder().decode(Transcript.self, from: Data(#"{"text":"한국어 English 42","segments":[{"start_s":1.25,"end_s":2.5,"text":"한국어 English 42"}],"alignment_status":"completed"}"#.utf8))
        try check(try TranscriptExport.render(transcript, title: "회의 — 2026-10-10", format: "srt").contains("00:00:01,250 --> 00:00:02,500"), "aligned global timestamp export")
        let failed = try JSONDecoder().decode(Transcript.self, from: Data(#"{"text":"보존한 원문","segments":[],"alignment_status":"failed"}"#.utf8))
        try rejects("failed alignment cannot export SRT") { _ = try TranscriptExport.render(failed, title: "녹음", format: "srt") }
        try check(try TranscriptExport.render(failed, title: "녹음", format: "txt").contains("보존한 원문"), "failed alignment preserves text export")
        try transcriptDisplayChecks()
        try notesChecks()
        try await cloudFolderChecks()
        try playbackChecks()
        guard CommandLine.arguments.contains("--broker-checks") else { print("All UI/protocol native checks passed; broker checks require --broker-checks"); return }
        let incoming = root.appendingPathComponent("incoming.wav"); try wave().write(to: incoming)
        let state = root.appendingPathComponent("state")
        let broker = ImportBroker(root: state)
        let memo = try await broker.importFile(incoming)
        try check(memo.recorded_at == nil, "import never uses mtime as date")
        try check(memo.completion_evidence == "finished", "decoded stable import is finished")
        try check(!memo.source_reference.contains(incoming.path), "source reference is opaque")
        let reopened = ImportBroker(root: state)
        let again = try await reopened.importFile(incoming)
        try check(again.uuid == memo.uuid && again.source_reference == memo.source_reference, "durable import identity after restart")
        try FileManager.default.removeItem(at: incoming)
        let snapshot = try await reopened.snapshot(reference: memo.source_reference, jobID: "test-job")
        try check(snapshot.revision_before == snapshot.revision_after && FileManager.default.fileExists(atPath: snapshot.owned_path), "import survives removal of external file")
        let attributes = try FileManager.default.attributesOfItem(atPath:snapshot.owned_path)
        try check((attributes[.size] as? NSNumber)?.int64Value == snapshot.revision_after.size, "snapshot owned size matches wire revision")
        var ownedStat = stat(); _ = stat(snapshot.owned_path,&ownedStat)
        let ownedNanoseconds = Int64(ownedStat.st_mtimespec.tv_sec) * 1_000_000_000 + Int64(ownedStat.st_mtimespec.tv_nsec)
        try check(ownedNanoseconds == snapshot.revision_after.mtime_ns, "snapshot actual owned mtime matches wire revision")
        let bad = root.appendingPathComponent("bad.wav"); try Data("not audio".utf8).write(to: bad)
        do { _ = try await reopened.importFile(bad); throw NativeFailure.invalid("FAIL: invalid audio accepted") } catch { try check(!(error.localizedDescription.contains("FAIL:")), "invalid media rejected") }
        try await publicationChecks(root: root)
        try await notesPublicationChecks(root:root)
        try await lifecycleChecks(root:root)
        try await sourceChecks(root:root)
        print("All native checks passed")
    }

}
