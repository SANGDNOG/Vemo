import XCTest
import AVFoundation
@testable import NativeCore

private actor DriveFixture: GoogleDriveTransport {
    struct Reply: Sendable {
        let status: Int
        let body: Data
        init(_ object: Any) throws { try self.init(200, object) }
        init(_ status: Int, _ object: Any) throws {
            self.status = status; self.body = try JSONSerialization.data(withJSONObject: object)
        }
        init(status: Int, bytes: Data) { self.status = status; self.body = bytes }
    }
    var replies: [Reply]
    var requests: [URLRequest] = []
    init(_ replies: [Reply]) { self.replies = replies }
    func send(_ request: URLRequest, maximumBytes: Int64, receive: @escaping @Sendable (Data) throws -> Void) async throws -> Int {
        requests.append(request)
        guard !replies.isEmpty else { throw GoogleDriveError.invalidResponse }
        let reply = replies.removeFirst()
        // Deliberately ignore the bound: the receiver must enforce it too.
        try receive(reply.body)
        return reply.status
    }
    func captured() -> [URLRequest] { requests }
    func add(_ values: [Reply]) { replies.append(contentsOf: values) }
}

private actor DrivePublicationGate {
    var allowed = true
    func invalidate() { allowed = false }
    func current() -> Bool { allowed }
}

final class GoogleDriveTests: XCTestCase, @unchecked Sendable {
    let processor = "11111111-1111-4111-8111-111111111111"
    private func root(_ id: String = "root123", name: String = "Vemo") -> [String: Any] {
        ["id": id, "name": name, "mimeType": GoogleDriveClient.folderMIME, "trashed": false,
         "properties": ["vemoProtocol": "1", "vemoKind": "root", "vemoProcessor": processor]]
    }
    private func client(_ fixture: DriveFixture) -> GoogleDriveClient {
        GoogleDriveClient(accessToken: { "synthetic-token" }, transport: fixture)
    }
    private func temporary() throws -> URL {
        let path = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("drive-tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: path) }
        return path
    }
    func testInitialRestoreAndImmutableBinding() async throws {
        let fixture = DriveFixture([try .init(["user": ["permissionId": "account1"]]), try .init(root())])
        let mirror = GoogleDriveMirror(stateDirectory: try temporary().appendingPathComponent("new"), client: client(fixture))
        let empty = try await mirror.restore(); XCTAssertNil(empty)
        let binding = GoogleDriveBinding(accountID: "account1", rootID: "root123", processorID: processor)
        _ = try await mirror.connect(binding: binding)
        let restored = try await mirror.restore(); XCTAssertEqual(restored, binding)
        do {
            _ = try await mirror.connect(binding: .init(accountID: "account2", rootID: "other", processorID: processor))
            XCTFail("must preserve first binding")
        } catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let requests = await fixture.captured(); XCTAssertEqual(requests.count, 2)
    }
    func testWrongAccountFailsBeforeRootLookup() async throws {
        let fixture = DriveFixture([try .init(["user": ["permissionId": "different"]])])
        let mirror = GoogleDriveMirror(stateDirectory: try temporary(), client: client(fixture))
        do {
            _ = try await mirror.connect(binding: .init(accountID: "account1", rootID: "root123", processorID: processor))
            XCTFail("wrong account")
        } catch { XCTAssertEqual(error as? GoogleDriveError, .accountMismatch) }
        let restored = try await mirror.restore(); XCTAssertNil(restored)
    }
    func testRootPaginationAndDuplicateNamesFailClosed() async throws {
        let fixture = DriveFixture([try .init(["files": [root()], "nextPageToken": "page2"]),
                                    try .init(["files": [root("root2")]])])
        do { _ = try await client(fixture).listRoots(); XCTFail("duplicate root names") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let requests = await fixture.captured()
        XCTAssertEqual(requests.count, 2)
        XCTAssertTrue(requests.allSatisfy { $0.url?.host == "www.googleapis.com" && $0.url?.scheme == "https" })
        XCTAssertEqual(URLComponents(url: requests[1].url!, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "pageToken" })?.value, "page2")
    }
    func testRepeatedPaginationTokenAndHTTPFailures() async throws {
        let fixture = DriveFixture([try .init(["files": [], "nextPageToken": "repeat"]),
                                    try .init(["files": [], "nextPageToken": "repeat"])])
        do { _ = try await client(fixture).listRoots(); XCTFail("loop") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .limit) }
        for status in [301, 401, 403] {
            let denied = DriveFixture([try .init(status, ["error": "synthetic"])])
            do { _ = try await client(denied).accountID(); XCTFail("HTTP failure") }
            catch { XCTAssertEqual(error as? GoogleDriveError, .http(status)) }
        }
    }
    func testMetadataExactnessAndPathKey() throws {
        let path = "recordings/\(processor)/" + String(repeating: "a", count: 64) + "/audio.m4a"
        let properties = GoogleDriveClient.properties(path: path)
        XCTAssertEqual(properties["vemoKey"]?.count, 64)
        let valid = GoogleDriveFile(id: "audio", name: "audio.m4a", mimeType: "audio/mp4", parents: ["parent"], properties: properties, size: "1", trashed: false)
        try GoogleDriveClient.validate(valid, parent: "parent", path: path, mime: "audio/mp4")
        XCTAssertThrowsError(try GoogleDriveClient.validate(valid, parent: "other", path: path, mime: "audio/mp4"))
        XCTAssertThrowsError(try GoogleDriveClient.validate(valid, parent: "parent", path: path, mime: "application/json"))
        XCTAssertThrowsError(try GoogleDriveClient.validate(valid, parent: "parent", path: path + "x", mime: "audio/mp4"))
        XCTAssertThrowsError(try GoogleDriveClient.identifier("../escape"))
    }
    func testJSONBoundAndShortBody() async throws {
        let file = GoogleDriveFile(id: "json", name: "recording.json", mimeType: "application/json", parents: ["parent"], properties: [:], size: "1", trashed: false)
        let fixture = DriveFixture([.init(status: 200, bytes: Data([1, 2]))])
        do { _ = try await client(fixture).json(file); XCTFail("over limit") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .limit) }
        let short = DriveFixture([.init(status: 200, bytes: Data())])
        do { _ = try await client(short).json(file); XCTFail("short") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .invalidResponse) }
    }
    func testSymlinkStateRejected() async throws {
        let directory = try temporary(), target = try temporary()
        let link = directory.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)
        let mirror = GoogleDriveMirror(stateDirectory: link, client: client(DriveFixture([])))
        do { _ = try await mirror.restore(); XCTFail("symlink") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "unsafe_path") }
    }
    func testLostCreateAcknowledgementReusesPersistedID() async throws {
        let directory = try temporary()
        let fixture = DriveFixture([
            try .init(["user": ["permissionId": "account1"]]), try .init(["files": []]), try .init(["ids": ["reserved"]]),
            try .init(404, ["error": "missing"]), try .init(503, ["error": "lost acknowledgement"])
        ])
        let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(fixture))
        do { _ = try await mirror.createRoot(displayName: "Vemo", processorID: processor); XCTFail("lost ACK") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .http(503)) }
        let retry = DriveFixture([try .init(["user": ["permissionId": "account1"]]), try .init(root("reserved"))])
        let restored = GoogleDriveMirror(stateDirectory: directory, client: client(retry))
        let binding = try await restored.createRoot(displayName: "Vemo", processorID: processor)
        XCTAssertEqual(binding.rootID, "reserved")
        let requests = await retry.captured()
        XCTAssertEqual(requests.map(\.httpMethod), ["GET", "GET"])
    }
    func testForeignNameCollisionJournalsDistinctRootAndResumesLostCreate() async throws {
        let directory = try temporary(), name = "Vemo 테스트", chosen = "Vemo 테스트 (11111111)"
        var foreign = root("foreign", name: name)
        foreign["properties"] = ["vemoProtocol": "1", "vemoKind": "root",
                                 "vemoProcessor": "22222222-2222-4222-8222-222222222222"]
        let fixture = DriveFixture([
            try .init(["user": ["permissionId": "account1"]]), try .init(["files": [foreign]]),
            try .init(["ids": ["reserved"]]), try .init(404, ["error": "missing"]),
            try .init(503, ["error": "lost acknowledgement"])
        ])
        let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(fixture))
        do { _ = try await mirror.createRoot(displayName: name, processorID: processor); XCTFail("lost ACK") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .http(503)) }
        let journal = try JSONSerialization.jsonObject(with: Data(contentsOf: directory.appendingPathComponent("drive.json"))) as! [String: Any]
        let intent = journal["rootIntent"] as! [String: Any]
        XCTAssertEqual(intent["name"] as? String, chosen)
        XCTAssertEqual(intent["id"] as? String, "reserved")
        let sent = await fixture.captured()
        XCTAssertEqual(sent.map(\.httpMethod), ["GET", "GET", "GET", "GET", "POST"])
        let body = try JSONSerialization.jsonObject(with: sent.last!.httpBody!) as! [String: Any]
        XCTAssertEqual(body["name"] as? String, chosen)
        XCTAssertEqual(body["id"] as? String, "reserved")
        XCTAssertFalse(sent.contains { $0.url?.path.hasSuffix("/foreign") == true })
        // A retry after a genuinely absent create must send the identical ID/name.
        let retry = DriveFixture([try .init(["user": ["permissionId": "account1"]]),
            try .init(404, ["error": "missing"]), try .init(root("reserved", name: chosen))])
        let resumed = GoogleDriveMirror(stateDirectory: directory, client: client(retry))
        let binding = try await resumed.createRoot(displayName: name, processorID: processor)
        XCTAssertEqual(binding.rootID, "reserved")
        let retried = await retry.captured()
        let retryBody = try JSONSerialization.jsonObject(with: retried.last!.httpBody!) as! [String: Any]
        XCTAssertEqual(retryBody["name"] as? String, chosen)
        XCTAssertEqual(retryBody["id"] as? String, "reserved")
        // Once bound, a requested name must never rename or replace the root.
        await retry.add([try .init(["user": ["permissionId": "account1"]]), try .init(root("reserved", name: chosen))])
        let unchanged = try await resumed.createRoot(displayName: "Other", processorID: processor)
        XCTAssertEqual(unchanged, binding)
        await retry.add([try .init(["user": ["permissionId": "account1"]]), try .init(root("reserved", name: "Renamed on Drive"))])
        let remoteName = try await resumed.boundRootDisplayName()
        XCTAssertEqual(remoteName, "Renamed on Drive")
        let persisted = try await resumed.restore(); XCTAssertEqual(persisted, binding)
    }

    func testRootIntentRejectsWrongIdentityAndUnrelatedName() async throws {
        let directory = try temporary()
        let intent: [String: Any] = ["accountID": "account1", "processorID": processor,
            "name": "Vemo (11111111)", "id": "reserved"]
        let bytes = try JSONSerialization.data(withJSONObject: ["version": 1, "rootIntent": intent, "ids": [:]])
        try bytes.write(to: directory.appendingPathComponent("drive.json"))
        for (account, requestedProcessor, name) in [
            ("different", processor, "Vemo"),
            ("account1", "22222222-2222-4222-8222-222222222222", "Vemo"),
            ("account1", processor, "Unrelated")
        ] {
            let fixture = DriveFixture([try .init(["user": ["permissionId": account]])])
            let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(fixture))
            do { _ = try await mirror.createRoot(displayName: name, processorID: requestedProcessor); XCTFail("intent mismatch") }
            catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
            let requests = await fixture.captured()
            XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent("drive.json")), bytes)
        }
        let retry = DriveFixture([try .init(["user": ["permissionId": "account1"]]),
                                  try .init(root("reserved", name: "Vemo (11111111)"))])
        let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(retry))
        let binding = try await mirror.createRoot(displayName: "Vemo", processorID: processor)
        XCTAssertEqual(binding.rootID, "reserved")
    }

    func testExistingProcessorRootFailsBeforeAllocationEvenWithDifferentName() async throws {
        let directory = try temporary()
        let fixture = DriveFixture([try .init(["user": ["permissionId": "account1"]]),
                                   try .init(["files": [root("existing", name: "Other")]])])
        let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(fixture))
        do { _ = try await mirror.createRoot(displayName: "Vemo", processorID: processor); XCTFail("duplicate processor") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let requests = await fixture.captured(); XCTAssertEqual(requests.count, 2)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.appendingPathComponent("drive.json").path))
    }

    func testFallbackNameCollisionFailsBeforeAllocatingOrWriting() async throws {
        let directory = try temporary()
        var foreign = root("foreign")
        foreign["properties"] = ["vemoProtocol": "1", "vemoKind": "root",
                                 "vemoProcessor": "22222222-2222-4222-8222-222222222222"]
        var collision = root("collision", name: "Vemo (11111111)")
        collision["properties"] = ["vemoProtocol": "1", "vemoKind": "root",
                                   "vemoProcessor": "33333333-3333-4333-8333-333333333333"]
        let fixture = DriveFixture([try .init(["user": ["permissionId": "account1"]]),
                                   try .init(["files": [foreign, collision]])])
        let mirror = GoogleDriveMirror(stateDirectory: directory, client: client(fixture))
        do { _ = try await mirror.createRoot(displayName: "Vemo", processorID: processor); XCTFail("collision") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let requests = await fixture.captured(); XCTAssertEqual(requests.count, 2)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.appendingPathComponent("drive.json").path))
    }

    func testPullCommitsAudioBeforeMarkerAndRejectsBadHash() async throws {
        for corrupt in [false, true] {
            let audio = Data([1, 2, 3]), hash = MobileSyncValidation.digest(audio)
            let id = "22222222-2222-4222-8222-222222222222"
            let path = "recordings/" + id + "/" + hash
            func item(_ id: String, _ path: String, _ parent: String, _ mime: String, _ size: Int? = nil) -> [String: Any] {
                var value: [String: Any] = ["id": id, "name": String(path.split(separator: "/").last!), "parents": [parent],
                    "mimeType": mime, "properties": GoogleDriveClient.properties(path: path), "trashed": false]
                if let size { value["size"] = String(size) }
                return value
            }
            let request: [String: Any] = ["schema_version": 1, "recording_id": id, "audio_sha256": hash,
                "audio_bytes": 3, "audio_file": "audio.m4a", "recorded_at": "2026-10-17T12:00:00+09:00", "title": "Synthetic",
                "mode": "desktop", "target_processor_id": processor, "completed": true]
            let marker = try JSONSerialization.data(withJSONObject: request)
            let processorBytes = try BrokerFiles.json(MobileSyncProcessor(processorID: processor, displayName: "Synthetic"))
            let leaves = [item("marker", path + "/recording.json", "revision", GoogleDriveClient.jsonMIME, marker.count),
                          item("audio", path + "/audio.m4a", "revision", GoogleDriveClient.audioMIME, 3)]
            let account = try DriveFixture.Reply(["user": ["permissionId": "account1"]])
            let fixture = DriveFixture([
                account, try .init(root()), account, try .init(root()),
                try .init(["files": [item("processor", "processor.json", "root123", GoogleDriveClient.jsonMIME, processorBytes.count)]]),
                .init(status: 200, bytes: processorBytes),
                try .init(["files": [item("recordings", "recordings", "root123", GoogleDriveClient.folderMIME)]]),
                try .init(["files": [item("recording", "recordings/" + id, "recordings", GoogleDriveClient.folderMIME)]]),
                try .init(["files": [item("revision", path, "recording", GoogleDriveClient.folderMIME)]]),
                try .init(["files": leaves]), .init(status: 200, bytes: marker),
                try .init(["files": leaves]), .init(status: 200, bytes: corrupt ? Data([4, 5, 6]) : audio),
                .init(status: 200, bytes: marker), account, try .init(root())
            ])
            let mirror = GoogleDriveMirror(stateDirectory: try temporary(), client: client(fixture))
            let cache = try await mirror.connect(binding: .init(accountID: "account1", rootID: "root123", processorID: processor))
            try MobileSyncFiles.Directory(cache).atomic(processorBytes, name: "processor.json", replace: false)
            let destination = cache.appendingPathComponent(path)
            if corrupt {
                do { _ = try await mirror.pull(); XCTFail("bad hash") }
                catch { XCTAssertEqual(error as? GoogleDriveError, .invalidResponse) }
                XCTAssertFalse(FileManager.default.fileExists(atPath: destination.appendingPathComponent("recording.json").path))
                XCTAssertFalse(FileManager.default.fileExists(atPath: destination.appendingPathComponent("audio.m4a").path))
            } else {
                _ = try await mirror.pull()
                XCTAssertEqual(try Data(contentsOf: destination.appendingPathComponent("audio.m4a")), audio)
                XCTAssertEqual(try Data(contentsOf: destination.appendingPathComponent("recording.json")), marker)
            }
        }
    }

    func testCancellationAndInvalidTokenDoNotSendRequests() async throws {
        let fixture = DriveFixture([])
        let invalid = GoogleDriveClient(accessToken: { "unsafe\r\ntoken" }, transport: fixture)
        do { _ = try await invalid.accountID(); XCTFail("header injection") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .invalidMetadata) }
        let cancelled = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await self.client(fixture).accountID()
        }
        do { _ = try await cancelled.value; XCTFail("cancelled") }
        catch { XCTAssertTrue(error is CancellationError) }
        let requests = await fixture.captured(); XCTAssertTrue(requests.isEmpty)
    }
    func testWrongProcessorPrepareDoesNotMutateRemoteOrRebindBroker() async throws {
        let fixture = DriveFixture([try .init(["user": ["permissionId": "account1"]]), try .init(root()),
                                    try .init(["user": ["permissionId": "account1"]]), try .init(root())])
        let mirror = GoogleDriveMirror(stateDirectory: try temporary(), client: client(fixture))
        _ = try await mirror.connect(binding: .init(accountID: "account1", rootID: "root123", processorID: processor))
        let broker = MobileSyncBroker(root: try temporary())
        do { _ = try await mirror.prepare(broker: broker, displayName: "Synthetic"); XCTFail("wrong processor") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let config = try await broker.configuration(); XCTAssertNil(config)
        let requests = await fixture.captured(); XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "GET" })
    }

    func testFinalLiveGuardRejectsStaleJobWithoutUpload() async throws {
        let state = try temporary(), mirrorState = try temporary()
        let broker = MobileSyncBroker(root: state)
        let processor = try await broker.processorIdentity()
        func rootObject() -> [String: Any] {
            ["id": "root123", "name": "Vemo", "mimeType": GoogleDriveClient.folderMIME, "trashed": false,
             "properties": ["vemoProtocol": "1", "vemoKind": "root", "vemoProcessor": processor]]
        }
        func item(_ id: String, _ path: String, _ parent: String, _ mime: String, _ size: Int? = nil) -> [String: Any] {
            var object: [String: Any] = ["id": id, "name": String(path.split(separator: "/").last!), "parents": [parent],
                "mimeType": mime, "properties": GoogleDriveClient.properties(path: path), "trashed": false]
            if let size { object["size"] = String(size) }
            return object
        }
        let processorBytes = try BrokerFiles.json(MobileSyncProcessor(processorID: processor, displayName: "Synthetic"))
        let account = try DriveFixture.Reply(["user": ["permissionId": "account1"]])
        let fixture = DriveFixture([account, try .init(rootObject())])
        let mirror = GoogleDriveMirror(stateDirectory: mirrorState, client: client(fixture))
        let cache = try await mirror.connect(binding: .init(accountID: "account1", rootID: "root123", processorID: processor))
        _ = try await broker.register(syncRoot: cache, displayName: "Synthetic")
        let source = state.appendingPathComponent("synthetic.m4a")
        do {
            let writer = try AVAudioFile(forWriting: source, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 32000])
            let buffer = AVAudioPCMBuffer(pcmFormat: writer.processingFormat, frameCapacity: 1600)!
            buffer.frameLength = 1600
            for i in 0..<1600 { buffer.floatChannelData![0][i] = Float(sin(Double(i) / 20)) * 0.1 }
            try writer.write(from: buffer)
        }
        let hash = try BrokerFiles.hash(source), id = UUID().uuidString.lowercased()
        let requestPath = "recordings/" + id + "/" + hash
        let local = try MobileSyncFiles.Directory(cache).child("recordings", create: true).child(id, create: true).child(hash, create: true)
        try FileManager.default.copyItem(at: source, to: local.url.appendingPathComponent("audio.m4a"))
        let marker = try JSONSerialization.data(withJSONObject: ["schema_version": 1, "recording_id": id, "audio_sha256": hash,
            "audio_bytes": try BrokerFiles.stamp(source).size, "audio_file": "audio.m4a", "recorded_at": "2026-10-17T12:00:00+09:00",
            "title": "Synthetic", "mode": "desktop", "target_processor_id": processor, "completed": true])
        try local.atomic(marker, name: "recording.json", replace: false)
        let imports = ImportBroker(root: state), scan = try await broker.scan(importBroker: imports)
        guard let memo = scan.pending.first else { XCTFail("missing import"); return }
        try await broker.markDelivered(sourceReference: memo.source_reference, jobID: "live-job")
        let snapshot = try await imports.snapshot(reference: memo.source_reference, jobID: "live-job")
        let artifacts = state.appendingPathComponent("artifacts/live-job")
        try BrokerFiles.directory(artifacts)
        let transcript = artifacts.appendingPathComponent("transcript.json"), notes = artifacts.appendingPathComponent("notes.json")
        let text = "Synthetic transcript."
        try JSONSerialization.data(withJSONObject: ["text": text, "segments": [], "alignment_status": "completed"]).write(to: transcript)
        try JSONSerialization.data(withJSONObject: ["schema_version": 1, "source_sha256": MobileSyncValidation.digest(Data(text.utf8)),
            "summary": [["text": "Synthetic", "quotes": [text]]], "highlights": [], "topics": []]).write(to: notes)
        let job = try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: [
            "id": "live-job", "uuid": memo.uuid, "topic": "Synthetic", "recording_date": "2026-10-17", "recorded_at": memo.recorded_at!,
            "status": "completed", "stage": "completed", "elapsed_seconds": 0, "title_status": "completed",
            "alignment_status": "completed", "notes_status": "completed", "audio_path": snapshot.owned_path,
            "artifact_path": transcript.path, "notes_path": notes.path]))
        let output = try await broker.publish(job: job)
        var old = try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as! [String: Any]
        old["title"] = "Previous title"
        let previous = try JSONSerialization.data(withJSONObject: old)
        let resultPath = "results/" + id + "/" + hash
        await fixture.add([
            account, try .init(rootObject()),
            try .init(["files": [item("processor", "processor.json", "root123", GoogleDriveClient.jsonMIME, processorBytes.count)]]),
            .init(status: 200, bytes: processorBytes),
            try .init(["files": [item("recordings", "recordings", "root123", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("recording", "recordings/" + id, "recordings", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("revision", requestPath, "recording", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("marker", requestPath + "/recording.json", "revision", GoogleDriveClient.jsonMIME, marker.count)]]),
            .init(status: 200, bytes: marker),
            try .init(["files": [item("results", "results", "root123", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("resultRecording", "results/" + id, "results", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("resultRevision", resultPath, "resultRecording", GoogleDriveClient.folderMIME)]]),
            try .init(["files": [item("result", resultPath + "/result.json", "resultRevision", GoogleDriveClient.jsonMIME, previous.count)]]),
            .init(status: 200, bytes: previous), account, try .init(rootObject())
        ])
        do {
            try await mirror.pushResult(job: job, broker: broker, shouldPublish: {
                // The final binding GET is the 18th request, including connect.
                await fixture.captured().count < 18
            })
            XCTFail("stale snapshot accepted")
        } catch { XCTAssertEqual(error as? GoogleDriveError, .staleJob) }
        let requests = await fixture.captured()
        XCTAssertEqual(requests.count, 18)
        XCTAssertEqual(requests.last?.url?.path, "/drive/v3/files/root123")
        XCTAssertTrue(requests.allSatisfy { $0.httpMethod == "GET" })
    }

    func testMutationGuardRunsAfterTokenAwait() async throws {
        for upload in [false, true] {
            let gate = DrivePublicationGate(), fixture = DriveFixture([])
            let client = GoogleDriveClient(accessToken: {
                await gate.invalidate()
                return "synthetic-token"
            }, transport: fixture)
            do {
                if upload {
                    try await client.upload(id: "result", data: Data("{}".utf8), shouldPublish: { await gate.current() })
                } else {
                    _ = try await client.create(id: "folder", name: "results", parent: "root123", mime: GoogleDriveClient.folderMIME,
                        properties: GoogleDriveClient.properties(path: "results"), shouldPublish: { await gate.current() })
                }
                XCTFail("mutation after stale token await")
            } catch { XCTAssertEqual(error as? GoogleDriveError, .staleJob) }
            let requests = await fixture.captured(); XCTAssertTrue(requests.isEmpty)
        }
    }

    func testGooglePrepareAcceptsCanonicalInternalCacheAndRejectsOtherOverlap() async throws {
        let state = try temporary()
        let broker = MobileSyncBroker(root: state, namespace: .googleDrive)
        let processor = try await broker.processorIdentity()
        let rootObject: [String: Any] = ["id": "root123", "name": "Vemo", "mimeType": GoogleDriveClient.folderMIME,
            "trashed": false, "properties": ["vemoProtocol": "1", "vemoKind": "root", "vemoProcessor": processor]]
        let processorBytes = try BrokerFiles.json(MobileSyncProcessor(processorID: processor, displayName: "Synthetic"))
        let processorFile: [String: Any] = ["id": "processor", "name": "processor.json", "mimeType": GoogleDriveClient.jsonMIME,
            "parents": ["root123"], "trashed": false, "properties": GoogleDriveClient.properties(path: "processor.json"),
            "size": String(processorBytes.count)]
        let account = try DriveFixture.Reply(["user": ["permissionId": "account1"]])
        let fixture = DriveFixture([account, try .init(rootObject), account, try .init(rootObject),
            try .init(["files": [processorFile]]), .init(status: 200, bytes: processorBytes)])
        let stateDirectory = state.appendingPathComponent("native/google-drive")
        let mirror = GoogleDriveMirror(stateDirectory: stateDirectory, client: client(fixture))
        let cache = try await mirror.connect(binding: .init(accountID: "account1", rootID: "root123", processorID: processor))
        XCTAssertEqual(cache.deletingLastPathComponent(), stateDirectory.appendingPathComponent("cache", isDirectory: true))
        try MobileSyncValidation.hash(cache.lastPathComponent)
        let config = try await mirror.prepare(broker: broker, displayName: "Synthetic")
        XCTAssertEqual(config.syncRoot, cache)
        XCTAssertEqual(config.processorID, processor)
        XCTAssertEqual(try Data(contentsOf: cache.appendingPathComponent("processor.json")), processorBytes)
        let restored = try await MobileSyncBroker(root: state, namespace: .googleDrive).configuration()
        XCTAssertEqual(restored?.syncRoot, cache)

        let negatives = [
            state, state.appendingPathComponent("native"), stateDirectory, cache.deletingLastPathComponent(),
            stateDirectory.appendingPathComponent("cache/not-a-digest"),
            stateDirectory.appendingPathComponent("cache/" + String(repeating: "A", count: 64)),
            stateDirectory.appendingPathComponent("cache/" + String(repeating: "a", count: 63)),
            cache.appendingPathComponent("nested"),
            state.appendingPathComponent("native/other/cache/" + String(repeating: "a", count: 64))
        ]
        for path in negatives {
            try BrokerFiles.directory(path)
            do { _ = try await broker.register(syncRoot: path, displayName: "Synthetic"); XCTFail("arbitrary internal path") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "overlapping_folders") }
        }
        for localBroker in [MobileSyncBroker(root: state), MobileSyncBroker(root: state, namespace: .localFolder)] {
            do { _ = try await localBroker.register(syncRoot: cache, displayName: "Local"); XCTFail("local overlap") }
            catch { XCTAssertEqual((error as? BrokerFailure)?.code, "overlapping_folders") }
            let localConfig = try await localBroker.configuration(); XCTAssertNil(localConfig)
        }
        let linked = cache.deletingLastPathComponent().appendingPathComponent(String(repeating: "b", count: 64))
        try FileManager.default.createSymbolicLink(at: linked, withDestinationURL: try temporary())
        do { _ = try await broker.register(syncRoot: linked, displayName: "Synthetic"); XCTFail("cache symlink") }
        catch { XCTAssertEqual((error as? BrokerFailure)?.code, "unsafe_path") }
        let after = try await broker.configuration(); XCTAssertEqual(after?.syncRoot, cache)
    }

    func testRootPaginationRejectsDuplicateProcessorAcrossDifferentNames() async throws {
        let duplicate = DriveFixture([try .init(["files": [root()], "nextPageToken": "second"]),
                                      try .init(["files": [root("root2", name: "Other Vemo")]])])
        do { _ = try await client(duplicate).listRoots(); XCTFail("duplicate processor accepted") }
        catch { XCTAssertEqual(error as? GoogleDriveError, .conflict) }
        let requests = await duplicate.captured(); XCTAssertEqual(requests.count, 2)

        var distinctRoot = root("root2", name: "Other Vemo")
        distinctRoot["properties"] = ["vemoProtocol": "1", "vemoKind": "root",
                                      "vemoProcessor": "22222222-2222-4222-8222-222222222222"]
        let distinct = DriveFixture([try .init(["files": [root()], "nextPageToken": "second"]),
                                     try .init(["files": [distinctRoot]])])
        let roots = try await client(distinct).listRoots(); XCTAssertEqual(roots.count, 2)
    }

}
