import XCTest
import AVFoundation
import CryptoKit
import Darwin
@testable import NativeCore

final class MobileSyncNamespaceTests: XCTestCase, @unchecked Sendable {
    private struct Fixture {
        let base: URL
        var state: URL { base.appendingPathComponent("state") }
        var local: URL { base.appendingPathComponent("local-sync") }
        var google: URL { base.appendingPathComponent("google-staging") }
        var localNative: URL { state.appendingPathComponent("native") }
        var googleNative: URL { state.appendingPathComponent("native/google-mobile") }

        init() throws {
            // Avoid /tmp's symlink on macOS: production brokers reject linked ancestors.
            base = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Caches/VemoMobileNamespaceTests-" + UUID().uuidString)
            for path in [state, local, google] {
                try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
            }
        }
        func cleanup() { try? FileManager.default.removeItem(at: base) }
        func registry(_ native: URL) -> URL { native.appendingPathComponent("mobile-sync.json") }

        func audio() throws -> URL {
            let path = base.appendingPathComponent("synthetic.m4a")
            let writer = try AVAudioFile(forWriting: path, settings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 16000,
                AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 32000
            ])
            let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: writer.processingFormat, frameCapacity: 16000))
            buffer.frameLength = 16000
            for index in 0..<16000 {
                buffer.floatChannelData![0][index] = Float(sin(Double(index) * .pi / 20)) * 0.1
            }
            try writer.write(from: buffer)
            return path
        }

        func request(at cloud: URL, processor: String, id: String, audio: URL) throws {
            let hash = try BrokerFiles.hash(audio)
            let folder = cloud.appendingPathComponent("recordings/\(id)/\(hash)")
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: audio, to: folder.appendingPathComponent("audio.m4a"))
            let object: [String: Any] = [
                "schema_version": 1, "recording_id": id, "audio_sha256": hash,
                "audio_bytes": try BrokerFiles.stamp(audio).size, "audio_file": "audio.m4a",
                "recorded_at": "2026-10-17T00:30:00+05:45", "title": "Synthetic namespace recording",
                "mode": "desktop", "target_processor_id": processor, "completed": true
            ]
            try JSONSerialization.data(withJSONObject: object).write(to: folder.appendingPathComponent("recording.json"))
        }

        func completedJob(memo: Memo, snapshot: OwnedSnapshot, id: String) throws -> Job {
            let artifacts = state.appendingPathComponent("artifacts/" + id)
            try FileManager.default.createDirectory(at: artifacts, withIntermediateDirectories: true)
            let transcript = artifacts.appendingPathComponent("transcript.json")
            let notes = artifacts.appendingPathComponent("notes.json")
            let text = "Synthetic shared-root transcript.\n"
            try JSONSerialization.data(withJSONObject: ["text": text, "segments": [], "alignment_status": "completed"])
                .write(to: transcript)
            let hash = SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
            try JSONSerialization.data(withJSONObject: [
                "schema_version": 1, "source_sha256": hash,
                "summary": [["text": "Synthetic summary", "quotes": [text]]],
                "highlights": [["quote": text]], "topics": []
            ]).write(to: notes)
            return try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: [
                "id": id, "uuid": memo.uuid, "topic": "Synthetic title", "recording_date": "2026-10-17",
                "recorded_at": try XCTUnwrap(memo.recorded_at), "status": "completed", "stage": "completed",
                "elapsed_seconds": 0, "title_status": "completed", "alignment_status": "completed",
                "notes_status": "completed", "audio_path": snapshot.owned_path,
                "artifact_path": transcript.path, "notes_path": notes.path
            ]))
        }
    }

    func testDefaultNamespaceReusesExistingLocalRegistryAndProcessor() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        try FileManager.default.createDirectory(at: f.localNative, withIntermediateDirectories: true)
        let legacyID = "11111111-1111-4111-8111-111111111111"
        try JSONSerialization.data(withJSONObject: ["schemaVersion": 1, "processorID": legacyID, "entries": [:]])
            .write(to: f.registry(f.localNative))

        let config = try await MobileSyncBroker(root: f.state).register(syncRoot: f.local, displayName: "Local")
        XCTAssertEqual(config.processorID, legacyID)
        let explicitLocal = try await MobileSyncBroker(root: f.state, namespace: .localFolder).configuration()
        XCTAssertEqual(explicitLocal?.processorID, legacyID)
        XCTAssertEqual(explicitLocal?.syncRoot, f.local)
        XCTAssertTrue(FileManager.default.fileExists(atPath: f.localNative.appendingPathComponent("mobile-sync.lock").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.googleNative.path))
    }

    func testGoogleConfigurationAndDisconnectLeaveLocalRegistryUntouched() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let local = MobileSyncBroker(root: f.state)
        let localConfig = try await local.register(syncRoot: f.local, displayName: "Local")
        let localBytes = try Data(contentsOf: f.registry(f.localNative))
        let google = MobileSyncBroker(root: f.state, namespace: .googleDrive)
        let googleConfig = try await google.register(syncRoot: f.google, displayName: "Google")
        XCTAssertNotEqual(googleConfig.processorID, localConfig.processorID)
        XCTAssertEqual(try Data(contentsOf: f.registry(f.localNative)), localBytes)
        XCTAssertTrue(FileManager.default.fileExists(atPath: f.registry(f.googleNative).path))

        let restart = try await MobileSyncBroker(root: f.state, namespace: .googleDrive).configuration()
        XCTAssertEqual(restart?.processorID, googleConfig.processorID)
        XCTAssertEqual(restart?.syncRoot, f.google)
        try await google.disconnect()
        let disconnected = try await google.configuration()
        XCTAssertNil(disconnected)
        XCTAssertEqual(try Data(contentsOf: f.registry(f.localNative)), localBytes)
        let stillLocal = try await MobileSyncBroker(root: f.state).configuration()
        XCTAssertEqual(stillLocal?.syncRoot, f.local)

        let googleBytes = try Data(contentsOf: f.registry(f.googleNative))
        try await local.disconnect()
        XCTAssertEqual(try Data(contentsOf: f.registry(f.googleNative)), googleBytes)
        let reconnected = try await google.register(syncRoot: f.google, displayName: "Google")
        XCTAssertEqual(reconnected.processorID, googleConfig.processorID)
    }

    func testProcessorIdentityPersistsBeforeRegistrationAndIsNamespaced() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let local = MobileSyncBroker(root: f.state)
        let google = MobileSyncBroker(root: f.state, namespace: .googleDrive)
        let localID = try await local.processorIdentity()
        let localBytes = try Data(contentsOf: f.registry(f.localNative))
        let googleID = try await google.processorIdentity()
        XCTAssertNotNil(UUID(uuidString: localID))
        XCTAssertNotNil(UUID(uuidString: googleID))
        XCTAssertNotEqual(localID, googleID)
        XCTAssertEqual(try Data(contentsOf: f.registry(f.localNative)), localBytes)
        let localRestart = try await MobileSyncBroker(root: f.state).processorIdentity()
        let googleRestart = try await MobileSyncBroker(root: f.state, namespace: .googleDrive).processorIdentity()
        XCTAssertEqual(localRestart, localID)
        XCTAssertEqual(googleRestart, googleID)
        let localConfiguration = try await local.configuration()
        let googleConfiguration = try await google.configuration()
        XCTAssertNil(localConfiguration)
        XCTAssertNil(googleConfiguration)
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.local.appendingPathComponent("processor.json").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.google.appendingPathComponent("processor.json").path))

        // Identity discovery must not need a temporary registered cloud folder.
        let localConfig = try await local.register(syncRoot: f.local, displayName: "Local")
        let googleConfig = try await google.register(syncRoot: f.google, displayName: "Google")
        XCTAssertEqual(localConfig.processorID, localID)
        XCTAssertEqual(googleConfig.processorID, googleID)
    }

    func testEachNamespaceUsesItsOwnLock() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let local = MobileSyncBroker(root: f.state)
        let google = MobileSyncBroker(root: f.state, namespace: .googleDrive)
        _ = try await local.register(syncRoot: f.local, displayName: "Local")
        do {
            let fd = try BrokerFiles.lock(f.localNative.appendingPathComponent("mobile-sync.lock"))
            defer { close(fd) }
            _ = try await google.register(syncRoot: f.google, displayName: "Google")
            do { try await local.disconnect(); XCTFail("local operations must honor the legacy lock") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "broker_busy") }
            do { _ = try await local.processorIdentity(); XCTFail("identity must honor the legacy lock") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "broker_busy") }
        }
        do {
            let fd = try BrokerFiles.lock(f.googleNative.appendingPathComponent("mobile-sync.lock"))
            defer { close(fd) }
            try await local.disconnect()
            do { try await google.disconnect(); XCTFail("Google operations must honor the Google lock") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "broker_busy") }
            do { _ = try await google.processorIdentity(); XCTFail("identity must honor the Google lock") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "broker_busy") }
        }
    }

    func testMappingsStaySeparateWhileGooglePublishesFromSharedStateRoot() async throws {
        let f = try Fixture(); defer { f.cleanup() }
        let local = MobileSyncBroker(root: f.state)
        let google = MobileSyncBroker(root: f.state, namespace: .googleDrive)
        let imports = ImportBroker(root: f.state)
        let localConfig = try await local.register(syncRoot: f.local, displayName: "Local")
        let googleConfig = try await google.register(syncRoot: f.google, displayName: "Google")
        let id = "22222222-2222-4222-8222-222222222222"
        let audio = try f.audio()
        // Identical content and recording ID in separate provider roots must not
        // collapse mappings or overwrite either provider's job association.
        try f.request(at: f.local, processor: localConfig.processorID, id: id, audio: audio)
        try f.request(at: f.google, processor: googleConfig.processorID, id: id, audio: audio)
        let localScan = try await local.scan(importBroker: imports)
        let googleScan = try await google.scan(importBroker: imports)
        XCTAssertTrue(localScan.issues.isEmpty)
        XCTAssertTrue(googleScan.issues.isEmpty)
        XCTAssertEqual(localScan.pending.count, 1)
        XCTAssertEqual(googleScan.pending.count, 1)
        let localMemo = try XCTUnwrap(localScan.pending.first)
        let googleMemo = try XCTUnwrap(googleScan.pending.first)
        XCTAssertNotEqual(localMemo.uuid, googleMemo.uuid)
        XCTAssertNotEqual(localMemo.source_reference, googleMemo.source_reference)
        do { try await google.bindJob(memoUUID: localMemo.uuid, jobID: "wrong-job"); XCTFail("cross-namespace mapping") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "unknown_mobile_mapping") }
        try await local.bindJob(memoUUID: localMemo.uuid, jobID: "local-job")
        let localBytes = try Data(contentsOf: f.registry(f.localNative))
        try await google.bindJob(memoUUID: googleMemo.uuid, jobID: "google-job")
        XCTAssertEqual(try Data(contentsOf: f.registry(f.localNative)), localBytes)
        let localMappings = try await MobileSyncBroker(root: f.state).mappings()
        let googleMappings = try await MobileSyncBroker(root: f.state, namespace: .googleDrive).mappings()
        XCTAssertEqual(localMappings.map(\.jobID), ["local-job"])
        XCTAssertEqual(googleMappings.map(\.jobID), ["google-job"])
        XCTAssertEqual(googleMappings.map(\.memoUUID), [googleMemo.uuid])

        let snapshot = try await imports.snapshot(reference: googleMemo.source_reference, jobID: "google-job")
        XCTAssertTrue(snapshot.owned_path.hasPrefix(f.state.appendingPathComponent("snapshots/jobs/google-job").path + "/"))
        let job = try f.completedJob(memo: googleMemo, snapshot: snapshot, id: "google-job")
        let output = try await google.publish(job: job)
        let expected = f.google.appendingPathComponent("results/\(id)/\(try BrokerFiles.hash(audio))/result.json")
        XCTAssertEqual(output, expected)
        let result = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as? [String: Any])
        XCTAssertEqual(result["processor_id"] as? String, googleConfig.processorID)
        XCTAssertEqual(result["transcript"] as? String, "Synthetic shared-root transcript.\n")
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.local.appendingPathComponent("results").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.googleNative.appendingPathComponent("artifacts").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.googleNative.appendingPathComponent("imports.json").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: f.localNative.appendingPathComponent("imports.json").path))
        do { _ = try await local.publish(job: job); XCTFail("local broker cannot publish a Google job") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "mobile_result_not_ready") }
        XCTAssertEqual(try Data(contentsOf: f.registry(f.localNative)), localBytes)
    }
}
