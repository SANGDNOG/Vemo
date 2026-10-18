import XCTest
import AVFoundation
import CryptoKit
import Darwin
@testable import NativeCore

final class MobileSyncBrokerTests: XCTestCase, @unchecked Sendable {
    private struct Fixture {
        let base: URL
        var state: URL { base.appendingPathComponent("state") }
        var cloud: URL { base.appendingPathComponent("synced") }
        init() throws {
            base = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/VemoMobileSyncTests-" + UUID().uuidString)
            try FileManager.default.createDirectory(at: state, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: cloud, withIntermediateDirectories: true)
        }
        func cleanup() { try? FileManager.default.removeItem(at: base) }
        func audio() throws -> URL {
            let path = base.appendingPathComponent(UUID().uuidString + ".m4a")
            do {
                let writer = try AVAudioFile(forWriting: path, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 32000])
                let buffer = AVAudioPCMBuffer(pcmFormat: writer.processingFormat, frameCapacity: 16000)!
                buffer.frameLength = 16000
                for i in 0..<16000 { buffer.floatChannelData![0][i] = Float(sin(Double(i) * .pi / 20)) * 0.1 }
                try writer.write(from: buffer)
            }
            return path
        }
        @discardableResult func request(processor: String, id: String = UUID().uuidString.lowercased(), audio: URL, edits: [String: Any] = [:], omitAudio: Bool = false) throws -> URL {
            let hash = try BrokerFiles.hash(audio)
            let folder = cloud.appendingPathComponent("recordings/\(id)/\(hash)")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            if !omitAudio { try FileManager.default.copyItem(at: audio, to: folder.appendingPathComponent("audio.m4a")) }
            var object: [String: Any] = ["schema_version": 1, "recording_id": id, "audio_sha256": hash,
                "audio_bytes": try BrokerFiles.stamp(audio).size, "audio_file": "audio.m4a",
                "recorded_at": "2026-10-17T00:30:00+14:00", "title": "폰 녹음", "mode": "desktop",
                "target_processor_id": processor, "completed": true]
            object.merge(edits) { _, new in new }
            try JSONSerialization.data(withJSONObject: object).write(to: folder.appendingPathComponent("recording.json"))
            return folder
        }
    }

    func testRegistrationPersistsLocalProcessorAndRefusesOtherOwner() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let first = try await MobileSyncBroker(root: f.state).register(syncRoot: f.cloud, displayName: "Mac")
        let broker = MobileSyncBroker(root: f.state)
        let again = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        XCTAssertEqual(first.processorID, again.processorID)
        let other = UUID().uuidString.lowercased()
        let data = try JSONSerialization.data(withJSONObject: ["schema_version": 1, "processor_id": other, "display_name": "Other"])
        try data.write(to: f.cloud.appendingPathComponent("processor.json"))
        do { _ = try await broker.register(syncRoot: f.cloud, displayName: "Mac"); XCTFail("must preserve another owner") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "processor_conflict") }
        XCTAssertEqual(try Data(contentsOf: f.cloud.appendingPathComponent("processor.json")), data)
    }

    func testScanPreservesMobileIdentityDateAndTitleAcrossCrashReplay() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state), imports = ImportBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        let audio = try f.audio()
        let unrelated = try await imports.importFile(audio)
        try f.request(processor: config.processorID, audio: audio)
        let first = try await broker.scan(importBroker: imports)
        XCTAssertTrue(first.issues.isEmpty)
        let memo = try XCTUnwrap(first.pending.first)
        XCTAssertNotEqual(memo.uuid, unrelated.uuid)
        XCTAssertNotEqual(memo.source_reference, unrelated.source_reference)
        XCTAssertEqual(memo.recorded_at, "2026-10-17T00:30:00+14:00")
        XCTAssertEqual(memo.recorded_at_source, "mobile_sync")
        XCTAssertEqual(memo.original_title, "폰 녹음")
        XCTAssertEqual(memo.original_timezone, "UTC+14:00")
        // Simulate a crash after ImportBroker committed but before mobile mapping
        // committed: retain processor/configuration, drop only the mobile entries.
        let mobileRegistryURL = f.state.appendingPathComponent("native/mobile-sync.json")
        var mobileRegistry = try JSONSerialization.jsonObject(with: Data(contentsOf: mobileRegistryURL)) as! [String: Any]
        mobileRegistry["entries"] = [:]
        try JSONSerialization.data(withJSONObject: mobileRegistry).write(to: mobileRegistryURL)
        let restart = MobileSyncBroker(root: f.state)
        let replay = try await restart.scan(importBroker: ImportBroker(root: f.state))
        XCTAssertEqual(replay.pending.map(\.uuid), [memo.uuid])
        try await restart.markDelivered(sourceReference: memo.source_reference, jobID: "job-1")
        let settled = try await restart.scan(importBroker: imports)
        XCTAssertTrue(settled.pending.isEmpty)
        let mappings = try await restart.mappings()
        XCTAssertEqual(mappings.first?.jobID, "job-1")
    }

    func testDeliveredScanReadsManifestAndTombstoneWithoutReopeningAudio() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state), imports = ImportBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        let folder = try f.request(processor: config.processorID, audio: f.audio())
        let initial = try await broker.scan(importBroker: imports)
        let memo = try XCTUnwrap(initial.pending.first)
        try await broker.bindJob(memoUUID: memo.uuid, jobID: "settled-job")
        // A provider can evict already imported audio. Polling still needs no
        // content read; this would fail if scan opened/copied the audio again.
        try FileManager.default.removeItem(at: folder.appendingPathComponent("audio.m4a"))
        let repeated = try await broker.scan(importBroker: imports)
        XCTAssertTrue(repeated.pending.isEmpty)
        XCTAssertTrue(repeated.issues.isEmpty)
        XCTAssertEqual(repeated.skipped, 1)
        var request = try JSONSerialization.jsonObject(with: Data(contentsOf: folder.appendingPathComponent("recording.json"))) as! [String: Any]
        request["title"] = "changed identity"
        try JSONSerialization.data(withJSONObject: request).write(to: folder.appendingPathComponent("recording.json"))
        let changed = try await broker.scan(importBroker: imports)
        XCTAssertEqual(changed.issues.first?.code, "mobile_identity_conflict")
    }

    func testDuplicateWireKeysAreRejectedBeforeFoundationCollapsesThem() throws {
        let valid = #"{"schema_version":1,"recording_id":"11111111-1111-4111-8111-111111111111","audio_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","audio_bytes":123,"audio_file":"audio.m4a","recorded_at":"2026-10-17T17:00:00+09:00","title":"same","mode":"desktop","target_processor_id":"22222222-2222-4222-8222-222222222222","completed":true}"#
        XCTAssertEqual(try MobileSyncRecording.decode(Data(valid.utf8)).title, "same")
        for duplicate in [#","title":"same""#, #","completed":false"#, #","t\u0069tle":"different""#] {
            let json = String(valid.dropLast()) + duplicate + "}"
            XCTAssertThrowsError(try MobileSyncRecording.decode(Data(json.utf8)))
        }
        let processor = #"{"schema_version":1,"processor_id":"11111111-1111-4111-8111-111111111111","display_name":"Mac","processor_\u0069d":"11111111-1111-4111-8111-111111111111"}"#
        XCTAssertThrowsError(try MobileSyncValidation.object(Data(processor.utf8), keys: ["schema_version", "processor_id", "display_name"]))
    }

    func testJSONKeyCheckHandlesEscapedValuesScopesAndNestingBound() throws {
        // Key-like text inside strings is content; repeated names in distinct
        // nested objects are also valid. Escaped key aliases in one object are not.
        let valid = #"{"payload":[{"title":"quoted \\\"title\\\":true, } ] \\"},{"title":"other"}]}"#
        try MobileSyncValidation.object(Data(valid.utf8), keys: ["payload"])
        let duplicate = #"{"payload":[{"title":"one","t\u0069tle":"two"}]}"#
        XCTAssertThrowsError(try MobileSyncValidation.object(Data(duplicate.utf8), keys: ["payload"]))
        let maximumDepth = "{\"payload\":" + String(repeating: "[", count: 23) + "0" + String(repeating: "]", count: 23) + "}"
        try MobileSyncValidation.object(Data(maximumDepth.utf8), keys: ["payload"])
        let excessiveDepth = "{\"payload\":" + String(repeating: "[", count: 24) + "0" + String(repeating: "]", count: 24) + "}"
        XCTAssertThrowsError(try MobileSyncValidation.object(Data(excessiveDepth.utf8), keys: ["payload"]))
    }

    func testFixedOffsetsPreserveRecordedCivilTimeWithoutInventingGeography() throws {
        for (input, expected) in [
            ("2026-10-17T00:01:00+14:00", "UTC+14:00"),
            ("2026-10-17T23:59:00-12:00", "UTC-12:00"),
            ("2026-10-17T00:00:00+05:45", "UTC+05:45"),
            ("2026-10-17T00:00:00-03:30", "UTC-03:30"),
            ("2026-10-17T00:00:00+00:17", "UTC+00:17"),
            ("2026-10-17T00:00:00.123456789Z", "UTC")
        ] { XCTAssertEqual(try MobileSyncValidation.timezone(input), expected) }
        for input in ["2026-10-17T00:00:00+14:01", "2026-10-17T00:00:00-15:00", "2026-10-17T24:00:00Z", "2026-02-30T00:00:00Z"] {
            XCTAssertThrowsError(try MobileSyncValidation.timezone(input))
        }
    }

    func testScanIsolatesIncompleteInvalidAndWrongProcessorRequests() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        let audio = try f.audio()
        try f.request(processor: config.processorID, audio: audio)
        try f.request(processor: config.processorID, audio: audio, omitAudio: true)
        try f.request(processor: config.processorID, audio: audio, edits: ["audio_bytes": 1])
        try f.request(processor: config.processorID, audio: audio, edits: ["recorded_at": "2026-02-30T12:00:00+09:00"])
        try f.request(processor: config.processorID, audio: audio, edits: ["recorded_at": "2026-10-17T12:00:00"])
        try f.request(processor: config.processorID, audio: audio, edits: ["audio_file": "../outside.m4a"])
        try f.request(processor: config.processorID, audio: audio, edits: ["completed": false])
        try f.request(processor: UUID().uuidString.lowercased(), audio: audio)
        try f.request(processor: config.processorID, audio: audio, edits: ["mode": "record_only", "target_processor_id": NSNull()])
        let report = try await broker.scan(importBroker: ImportBroker(root: f.state))
        XCTAssertEqual(report.pending.count, 1)
        XCTAssertEqual(report.issues.count, 6)
    }

    func testRejectsHashMismatchSymlinkAndConflictingRecordingRevision() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state), imports = ImportBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        let audio = try f.audio(), id = UUID().uuidString.lowercased()
        let good = try f.request(processor: config.processorID, id: id, audio: audio)
        let first = try await broker.scan(importBroker: imports)
        XCTAssertEqual(first.pending.count, 1)
        let bad = try f.request(processor: config.processorID, audio: audio)
        let bytes = try Data(contentsOf: audio)
        var corrupted = bytes; corrupted[corrupted.count - 1] ^= 1
        try corrupted.write(to: bad.appendingPathComponent("audio.m4a"))
        let linked = try f.request(processor: config.processorID, audio: audio, omitAudio: true)
        try FileManager.default.createSymbolicLink(at: linked.appendingPathComponent("audio.m4a"), withDestinationURL: audio)
        let secondAudio = f.base.appendingPathComponent("different.m4a")
        try corrupted.write(to: secondAudio)
        try f.request(processor: config.processorID, id: id, audio: secondAudio)
        let report = try await broker.scan(importBroker: imports)
        XCTAssertEqual(report.pending.count, 1)
        XCTAssertEqual(report.issues.count, 3)
        XCTAssertEqual(try Data(contentsOf: good.appendingPathComponent("audio.m4a")), bytes)
    }

    func testPurgedImportIsNotRecreatedByScanOrRestart() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        try f.request(processor: config.processorID, audio: f.audio())
        let report = try await broker.scan(importBroker: ImportBroker(root: f.state))
        let memo = try XCTUnwrap(report.pending.first)
        let registryURL = f.state.appendingPathComponent("native/imports.json")
        var registry = try JSONSerialization.jsonObject(with: Data(contentsOf: registryURL)) as! [String: Any]
        var entries = registry["entries"] as! [String: [String: Any]]
        entries[memo.source_reference]!["purged"] = true
        let owned = entries[memo.source_reference]!["path"] as! String
        registry["entries"] = entries
        try JSONSerialization.data(withJSONObject: registry).write(to: registryURL)
        try FileManager.default.removeItem(atPath: owned)
        let retry = try await MobileSyncBroker(root: f.state).scan(importBroker: ImportBroker(root: f.state))
        XCTAssertTrue(retry.pending.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: owned))
    }

    func testPublishValidatesAudioNotesAndMappingAndDoesNotWriteOldRoot() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let broker = MobileSyncBroker(root: f.state), imports = ImportBroker(root: f.state)
        let config = try await broker.register(syncRoot: f.cloud, displayName: "Mac")
        let folder = try f.request(processor: config.processorID, audio: f.audio())
        let report = try await broker.scan(importBroker: imports)
        let memo = try XCTUnwrap(report.pending.first)
        try await broker.markDelivered(sourceReference: memo.source_reference, jobID: "job-1")
        let snapshot = try await imports.snapshot(reference: memo.source_reference, jobID: "job-1")
        let text = "정확한 전사입니다."
        let artifacts = f.state.appendingPathComponent("artifacts/job-1")
        try FileManager.default.createDirectory(at: artifacts, withIntermediateDirectories: true)
        let artifact = artifacts.appendingPathComponent("transcript.json"), notes = artifacts.appendingPathComponent("notes.json")
        try JSONSerialization.data(withJSONObject: ["text": text, "segments": [], "alignment_status": "completed"]).write(to: artifact)
        func writeNotes(hash: String) throws {
            try JSONSerialization.data(withJSONObject: ["schema_version": 1, "source_sha256": hash,
                "summary": [["text": "요약", "quotes": [text]]], "highlights": [["quote": text]],
                "topics": [["keyword": "키워드", "summary": "내용", "quotes": [text]]]]).write(to: notes)
        }
        try writeNotes(hash: String(repeating: "0", count: 64))
        var job = try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: [
            "id": "job-1", "uuid": memo.uuid, "topic": "생성 제목", "recording_date": "2026-10-17", "recorded_at": memo.recorded_at!,
            "status": "completed", "stage": "completed", "elapsed_seconds": 0, "title_status": "completed", "alignment_status": "completed", "notes_status": "completed",
            "audio_path": snapshot.owned_path, "artifact_path": artifact.path, "notes_path": notes.path]))
        do { _ = try await broker.publish(job: job); XCTFail("stale notes") } catch {}
        try writeNotes(hash: SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined())
        job.deleted_at = "2026-10-17"
        do { _ = try await broker.publish(job: job); XCTFail("deleted") } catch {}
        job.deleted_at = nil
        let output = try await broker.publish(job: job)
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as! [String: Any]
        XCTAssertEqual(Set(object.keys), Set(["schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown"]))
        XCTAssertEqual(object["recording_id"] as? String, folder.deletingLastPathComponent().lastPathComponent)
        XCTAssertEqual(object["audio_sha256"] as? String, try BrokerFiles.hash(URL(fileURLWithPath: snapshot.owned_path)))
        XCTAssertEqual(object["processor_id"] as? String, config.processorID)
        XCTAssertEqual(object["transcript"] as? String, text)
        XCTAssertEqual(object["recorded_at"] as? String, memo.recorded_at)
        XCTAssertTrue((object["notes_markdown"] as? String)?.contains(text) == true)
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("audio.m4a").path))
        let retry = try await broker.publish(job: job)
        XCTAssertEqual(retry, output)
        let resultBytes = try Data(contentsOf: output)
        let timelineURL = output.deletingLastPathComponent().appendingPathComponent("transcript-timeline.json")
        if FileManager.default.fileExists(atPath: timelineURL.path) { try FileManager.default.removeItem(at: timelineURL) }
        _ = try await broker.publish(job: job)
        let timeline = try JSONSerialization.jsonObject(with: Data(contentsOf: timelineURL)) as! [String: Any]
        XCTAssertEqual(Set(timeline.keys), Set(["schema_version", "recording_id", "audio_sha256", "processor_id", "transcript_sha256", "segments"]))
        XCTAssertEqual(timeline["schema_version"] as? Int, 1)
        for key in ["recording_id", "audio_sha256", "processor_id"] {
            XCTAssertEqual(timeline[key] as? String, object[key] as? String)
        }
        XCTAssertEqual(timeline["transcript_sha256"] as? String, MobileSyncValidation.digest(Data(text.utf8)))
        let rows = try XCTUnwrap(timeline["segments"] as? [[String: Any]])
        XCTAssertEqual(rows.map { $0["text"] as! String }.joined(), text)
        XCTAssertTrue(rows.allSatisfy { $0["start"] is NSNull && $0["end"] is NSNull })
        // Alignment can arrive later without changing result.json.
        try JSONSerialization.data(withJSONObject: ["text": text,
            "segments": [["start_s": 1.25, "end_s": 2.75, "text": text]], "alignment_status": "completed"]).write(to: artifact)
        _ = try await broker.publish(job: job)
        let aligned = try JSONSerialization.jsonObject(with: Data(contentsOf: timelineURL)) as! [String: Any]
        let timed = try XCTUnwrap((aligned["segments"] as? [[String: Any]])?.first)
        XCTAssertEqual(timed["start"] as? Double, 1.25)
        XCTAssertEqual(timed["end"] as? Double, 2.75)
        XCTAssertEqual(try Data(contentsOf: output), resultBytes)
        var foreign = aligned
        foreign["processor_id"] = UUID().uuidString.lowercased()
        let foreignBytes = try JSONSerialization.data(withJSONObject: foreign)
        try foreignBytes.write(to: timelineURL)
        do { _ = try await broker.publish(job: job); XCTFail("foreign timeline") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "mobile_result_conflict") }
        XCTAssertEqual(try Data(contentsOf: timelineURL), foreignBytes)
        try FileManager.default.removeItem(at: timelineURL)
        let wrongAudio = f.state.appendingPathComponent("wrong.m4a")
        try Data(repeating: 0, count: Int(try BrokerFiles.stamp(URL(fileURLWithPath: snapshot.owned_path)).size)).write(to: wrongAudio)
        job.audio_path = wrongAudio.path
        do { _ = try await broker.publish(job: job); XCTFail("wrong source audio") } catch {}
        XCTAssertEqual(try Data(contentsOf: output), resultBytes)
        job.audio_path = snapshot.owned_path
        var conflicting = object; conflicting["audio_sha256"] = String(repeating: "f", count: 64)
        let conflictBytes = try JSONSerialization.data(withJSONObject: conflicting)
        try conflictBytes.write(to: output)
        do { _ = try await broker.publish(job: job); XCTFail("unrelated existing result") } catch {}
        XCTAssertEqual(try Data(contentsOf: output), conflictBytes)
        let other = f.base.appendingPathComponent("other-sync")
        try FileManager.default.createDirectory(at: other, withIntermediateDirectories: true)
        _ = try await broker.register(syncRoot: other, displayName: "Mac")
        let activeMappings = try await broker.mappings()
        XCTAssertTrue(activeMappings.isEmpty)
        let before = try Data(contentsOf: output)
        do { _ = try await broker.publish(job: job); XCTFail("old-root mapping") } catch {}
        XCTAssertEqual(try Data(contentsOf: output), before)
        XCTAssertFalse(FileManager.default.fileExists(atPath: other.appendingPathComponent("results").path))
    }
}
