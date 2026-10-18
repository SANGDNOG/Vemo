import Foundation
import CryptoKit
import Darwin

/// One immutable account/root/processor binding per state directory. Reconnects
/// restore that binding; another account/root needs a separate mirror instance.
public actor GoogleDriveMirror {
    private struct RootIntent: Codable { let accountID: String; let processorID: String; let name: String; let id: String }
    private struct Journal: Codable {
        var version = 1
        var binding: GoogleDriveBinding?
        var rootIntent: RootIntent?
        var ids: [String: String] = [:]
    }
    public let client: GoogleDriveClient
    private let stateDirectory: URL
    private var busy = false
    public init(stateDirectory: URL, client: GoogleDriveClient) {
        self.stateDirectory = stateDirectory.standardizedFileURL; self.client = client
    }
    private func begin() throws -> Int32 {
        guard !busy else { throw GoogleDriveError.busy }
        try BrokerFiles.directory(stateDirectory)
        let fd = try BrokerFiles.lock(stateDirectory.appendingPathComponent("drive.lock"))
        busy = true; return fd
    }
    private func end(_ fd: Int32) { busy = false; close(fd) }
    private func directory() throws -> MobileSyncFiles.Directory { try MobileSyncFiles.Directory(stateDirectory) }
    private func load() throws -> Journal {
        let dir = try directory()
        guard try dir.exists("drive.json") else { return Journal() }
        let journal = try JSONDecoder().decode(Journal.self, from: dir.read("drive.json"))
        guard journal.version == 1, journal.ids.count <= 50_000 else { throw GoogleDriveError.invalidMetadata }
        if let binding = journal.binding { try validate(binding) }
        for id in journal.ids.values { try GoogleDriveClient.identifier(id) }
        return journal
    }
    private func save(_ journal: Journal) throws {
        try Task.checkCancellation()
        try directory().atomic(BrokerFiles.json(journal), name: "drive.json", replace: true)
    }
    private func validate(_ binding: GoogleDriveBinding) throws {
        try GoogleDriveClient.identifier(binding.accountID); try GoogleDriveClient.identifier(binding.rootID)
        try MobileSyncValidation.uuid(binding.processorID)
    }
    private func checked(_ journal: Journal) async throws -> GoogleDriveBinding {
        guard let binding = journal.binding else { throw GoogleDriveError.notConnected }
        try validate(binding)
        guard try await client.accountID() == binding.accountID else { throw GoogleDriveError.accountMismatch }
        let root = try await client.file(binding.rootID)
        guard root.id == binding.rootID else { throw GoogleDriveError.invalidMetadata }
        try GoogleDriveClient.validateRoot(root, processorID: binding.processorID)
        return binding
    }
    private func cache(_ binding: GoogleDriveBinding) throws -> MobileSyncFiles.Directory {
        let key = MobileSyncValidation.digest(Data("\(binding.accountID)/\(binding.rootID)/\(binding.processorID)".utf8))
        return try directory().child("cache", create: true).child(key, create: true)
    }
    public func listRoots() async throws -> [GoogleDriveFile] { try await client.listRoots() }
    public func restore() throws -> GoogleDriveBinding? {
        let lock = try begin(); defer { end(lock) }
        return try load().binding
    }
    /// Read the current name only after validating the immutable account/root binding.
    public func boundRootDisplayName() async throws -> String {
        let lock = try begin(); defer { end(lock) }
        guard let binding = try load().binding else { throw GoogleDriveError.notConnected }
        try validate(binding)
        guard try await client.accountID() == binding.accountID else { throw GoogleDriveError.accountMismatch }
        let root = try await client.file(binding.rootID)
        guard root.id == binding.rootID else { throw GoogleDriveError.invalidMetadata }
        try GoogleDriveClient.validateRoot(root, processorID: binding.processorID)
        return root.name
    }
    public func cacheURL() throws -> URL {
        let lock = try begin(); defer { end(lock) }
        guard let binding = try load().binding else { throw GoogleDriveError.notConnected }
        return try cache(binding).url
    }
    @discardableResult
    public func connect(binding: GoogleDriveBinding) async throws -> URL {
        let lock = try begin(); defer { end(lock) }
        try validate(binding)
        var journal = try load()
        if let previous = journal.binding, previous != binding { throw GoogleDriveError.conflict }
        if let intent = journal.rootIntent,
           intent.accountID != binding.accountID || intent.id != binding.rootID || intent.processorID != binding.processorID { throw GoogleDriveError.conflict }
        journal.binding = binding
        _ = try await checked(journal)
        try save(journal)
        return try cache(binding).url
    }
    @discardableResult
    public func createRoot(displayName: String, processorID: String) async throws -> GoogleDriveBinding {
        let lock = try begin(); defer { end(lock) }
        try MobileSyncValidation.uuid(processorID)
        guard !displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              displayName.utf8.count <= 1024, !displayName.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw GoogleDriveError.invalidMetadata }
        var journal = try load()
        if let binding = journal.binding {
            guard binding.processorID == processorID else { throw GoogleDriveError.conflict }
            return try await checked(journal)
        }
        let account = try await client.accountID()
        if journal.rootIntent == nil {
            let roots = try await client.listRoots()
            guard !roots.contains(where: { $0.properties?["vemoProcessor"] == processorID }) else { throw GoogleDriveError.conflict }
            let name = roots.contains(where: { $0.name == displayName })
                ? Self.distinctRootName(displayName, processorID: processorID) : displayName
            guard name.utf8.count <= 1024, !roots.contains(where: { $0.name == name }) else { throw GoogleDriveError.conflict }
            let id = try await client.allocateID()
            journal.rootIntent = RootIntent(accountID: account, processorID: processorID, name: name, id: id)
            try save(journal) // Commit identity before the first remotely mutating request.
        }
        guard let intent = journal.rootIntent, intent.accountID == account,
              intent.processorID == processorID,
              (intent.name == displayName || intent.name == Self.distinctRootName(displayName, processorID: processorID)) else { throw GoogleDriveError.conflict }
        let root: GoogleDriveFile
        do { root = try await client.file(intent.id) }
        catch GoogleDriveError.http(404) {
            root = try await client.create(id: intent.id, name: intent.name, parent: nil, mime: GoogleDriveClient.folderMIME,
                properties: ["vemoProtocol": "1", "vemoKind": "root", "vemoProcessor": processorID])
        }
        guard root.id == intent.id, root.name == intent.name else { throw GoogleDriveError.conflict }
        try GoogleDriveClient.validateRoot(root, processorID: processorID)
        let binding = GoogleDriveBinding(accountID: account, rootID: root.id, processorID: processorID)
        journal.binding = binding; try save(journal)
        return binding
    }
    private static func distinctRootName(_ base: String, processorID: String) -> String {
        base + " (" + processorID.prefix(8) + ")"
    }
    private func child(parent: String, path: String, mime: String) async throws -> GoogleDriveFile? {
        let name = String(path.split(separator: "/").last!)
        let files = try await client.children(parent)
        let key = GoogleDriveClient.properties(path: path)["vemoKey"]
        let candidates = files.filter { $0.name == name || $0.properties?["vemoKey"] == key }
        guard candidates.count <= 1 else { throw GoogleDriveError.conflict }
        guard let file = candidates.first else { return nil }
        try GoogleDriveClient.validate(file, parent: parent, path: path, mime: mime)
        return file
    }
    private func ensure(parent: String, path: String, mime: String, journal: inout Journal,
                        shouldPublish: @escaping @Sendable () async -> Bool = { true }) async throws -> GoogleDriveFile {
        try await GoogleDriveClient.checkPublication(shouldPublish)
        if let existing = try await child(parent: parent, path: path, mime: mime) {
            if let id = journal.ids[path], id != existing.id { throw GoogleDriveError.conflict }
            return existing
        }
        if journal.ids[path] == nil {
            journal.ids[path] = try await client.allocateID(); try save(journal)
        }
        let id = journal.ids[path]!
        let file: GoogleDriveFile
        do { file = try await client.file(id) }
        catch GoogleDriveError.http(404) {
            file = try await client.create(id: id, name: String(path.split(separator: "/").last!), parent: parent, mime: mime,
                                           properties: GoogleDriveClient.properties(path: path), shouldPublish: shouldPublish)
        }
        guard file.id == id else { throw GoogleDriveError.conflict }
        try GoogleDriveClient.validate(file, parent: parent, path: path, mime: mime)
        return file
    }
    /// Explicit registration only. Use a broker with the real artifacts root and
    /// the independent Google registry namespace; its identity must match binding.
    public func prepare(broker: MobileSyncBroker, displayName: String) async throws -> MobileSyncConfiguration {
        let lock = try begin(); defer { end(lock) }
        var journal = try load(); let binding = try await checked(journal)
        let local = try cache(binding)
        guard try await broker.processorIdentity() == binding.processorID else { throw GoogleDriveError.conflict }
        if let previous = try await broker.configuration(), previous.syncRoot != local.url || previous.processorID != binding.processorID {
            throw GoogleDriveError.conflict
        }
        let config = try await broker.register(syncRoot: local.url, displayName: displayName)
        guard config.processorID == binding.processorID else { throw GoogleDriveError.conflict }
        let processor = MobileSyncProcessor(processorID: binding.processorID, displayName: displayName)
        let bytes = try BrokerFiles.json(processor)
        let remote = try await ensure(parent: binding.rootID, path: "processor.json", mime: GoogleDriveClient.jsonMIME, journal: &journal)
        if let size = remote.size, size != "0" {
            let existing = try await client.json(remote)
            try MobileSyncValidation.object(existing, keys: ["schema_version", "processor_id", "display_name"])
            guard try JSONDecoder().decode(MobileSyncProcessor.self, from: existing).processorID == binding.processorID else { throw GoogleDriveError.conflict }
            try local.atomic(existing, name: "processor.json", replace: true)
        } else {
            // Only a locally journaled, preallocated empty file can be completed.
            guard journal.ids["processor.json"] == remote.id else { throw GoogleDriveError.conflict }
            try await client.upload(id: remote.id, data: bytes)
            let confirmed = try await client.file(remote.id)
            try GoogleDriveClient.validate(confirmed, parent: binding.rootID, path: "processor.json", mime: GoogleDriveClient.jsonMIME)
            guard try await client.json(confirmed) == bytes else { throw GoogleDriveError.conflict }
            try local.atomic(bytes, name: "processor.json", replace: true)
        }
        if let previous = try await broker.configuration(), previous.syncRoot != local.url || previous.processorID != binding.processorID {
            throw GoogleDriveError.conflict
        }
        return config
    }
    private func owner(_ binding: GoogleDriveBinding, local: MobileSyncFiles.Directory) async throws {
        guard let remote = try await child(parent: binding.rootID, path: "processor.json", mime: GoogleDriveClient.jsonMIME) else { throw GoogleDriveError.conflict }
        let bytes = try await client.json(remote)
        try MobileSyncValidation.object(bytes, keys: ["schema_version", "processor_id", "display_name"])
        let processor = try JSONDecoder().decode(MobileSyncProcessor.self, from: bytes)
        let localProcessor = try JSONDecoder().decode(MobileSyncProcessor.self, from: local.read("processor.json"))
        guard processor.processorID == binding.processorID, localProcessor.processorID == binding.processorID else { throw GoogleDriveError.conflict }
    }
    private func folders(parent: String, prefix: String, hash: Bool = false) async throws -> [GoogleDriveFile] {
        let files = try await client.children(parent)
        var names: Set<String> = []
        for file in files {
            if hash { try MobileSyncValidation.hash(file.name) } else { try MobileSyncValidation.uuid(file.name) }
            guard names.insert(file.name).inserted else { throw GoogleDriveError.conflict }
            try GoogleDriveClient.validate(file, parent: parent, path: prefix + "/" + file.name, mime: GoogleDriveClient.folderMIME)
        }
        return files
    }
    /// Audio is validated and committed first; the exact v1 completion marker is
    /// committed last so MobileSyncBroker never observes a partial recording.
    @discardableResult
    public func pull() async throws -> URL {
        let lock = try begin(); defer { end(lock) }
        let journal = try load(), binding = try await checked(journal), local = try cache(binding)
        try await owner(binding, local: local)
        guard let recordings = try await child(parent: binding.rootID, path: "recordings", mime: GoogleDriveClient.folderMIME) else { return local.url }
        var count = 0
        for recording in try await folders(parent: recordings.id, prefix: "recordings") {
            let prefix = "recordings/" + recording.name
            for revision in try await folders(parent: recording.id, prefix: prefix, hash: true) {
                try Task.checkCancellation(); count += 1
                guard count <= 50_000 else { throw GoogleDriveError.limit }
                let path = prefix + "/" + revision.name
                guard let marker = try await child(parent: revision.id, path: path + "/recording.json", mime: GoogleDriveClient.jsonMIME) else { continue }
                let bytes = try await client.json(marker), request = try MobileSyncRecording.decode(bytes)
                guard request.recording_id == recording.name, request.audio_sha256 == revision.name else { throw GoogleDriveError.conflict }
                guard request.mode == "desktop", request.target_processor_id == binding.processorID else { continue }
                let output = try local.child("recordings", create: true).child(recording.name, create: true).child(revision.name, create: true)
                if try output.exists("recording.json") {
                    guard try output.read("recording.json") == bytes else { throw GoogleDriveError.conflict }
                    continue
                }
                guard let audio = try await child(parent: revision.id, path: path + "/audio.m4a", mime: GoogleDriveClient.audioMIME),
                      audio.size == String(request.audio_bytes) else { throw GoogleDriveError.invalidMetadata }
                let sink = try GoogleDriveAudioSink(directory: output, bytes: request.audio_bytes, hash: request.audio_sha256)
                defer { sink.cleanup() }
                try await client.download(id: audio.id, maximumBytes: request.audio_bytes) { try sink.append($0) }
                guard try await client.json(marker) == bytes else { throw GoogleDriveError.conflict }
                _ = try await checked(journal)
                try sink.commit()
                try output.atomic(bytes, name: "recording.json", replace: false)
            }
        }
        return local.url
    }
    /// Broker validates job ownership, original audio, tombstones and artifacts.
    /// A successful return means the remote result has been read back exactly.
    /// shouldPublish must inspect live job state; false throws staleJob so the
    /// coordinator can retry a fresh snapshot. It is checked across network awaits.
    public func pushResult(job: Job, broker: MobileSyncBroker,
                           shouldPublish: @escaping @Sendable () async -> Bool = { true }) async throws {
        let lock = try begin(); defer { end(lock) }
        var journal = try load(); let binding = try await checked(journal), local = try cache(binding)
        try await owner(binding, local: local)
        guard let config = try await broker.configuration(), config.syncRoot == local.url, config.processorID == binding.processorID else { throw GoogleDriveError.conflict }
        try await GoogleDriveClient.checkPublication(shouldPublish)
        let resultURL = try await broker.publish(job: job)
        let relative = String(resultURL.path.dropFirst(local.url.path.count + 1))
        let parts = relative.split(separator: "/").map(String.init)
        guard parts.count == 4, parts[0] == "results", parts[3] == "result.json" else { throw GoogleDriveError.invalidMetadata }
        try MobileSyncValidation.uuid(parts[1]); try MobileSyncValidation.hash(parts[2])
        let output = try local.child(parts[0]).child(parts[1]).child(parts[2])
        let bytes = try output.read(parts[3])
        let timelineBytes = try output.read(TranscriptTimeline.filename)
        try MobileSyncValidation.object(timelineBytes, keys: TranscriptTimeline.keys)
        try MobileSyncValidation.object(bytes, keys: ["schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown"])
        let object = try JSONSerialization.jsonObject(with: bytes) as? [String: Any]
        guard object?["processor_id"] as? String == binding.processorID, object?["recording_id"] as? String == parts[1],
              object?["audio_sha256"] as? String == parts[2] else { throw GoogleDriveError.conflict }
        // Recheck immutable remote request before publishing a locally processed job.
        var remoteParent = binding.rootID, requestPath = ""
        for component in ["recordings", parts[1], parts[2]] {
            requestPath = requestPath.isEmpty ? component : requestPath + "/" + component
            guard let folder = try await child(parent: remoteParent, path: requestPath, mime: GoogleDriveClient.folderMIME) else { throw GoogleDriveError.conflict }
            remoteParent = folder.id
        }
        guard let marker = try await child(parent: remoteParent, path: requestPath + "/recording.json", mime: GoogleDriveClient.jsonMIME),
              try await client.json(marker) == local.child("recordings").child(parts[1]).child(parts[2]).read("recording.json") else { throw GoogleDriveError.conflict }
        var parent = binding.rootID, path = ""
        for component in parts.prefix(3) {
            path = path.isEmpty ? component : path + "/" + component
            parent = try await ensure(parent: parent, path: path, mime: GoogleDriveClient.folderMIME, journal: &journal, shouldPublish: shouldPublish).id
        }
        let resultKeys: Set<String> = ["schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown"]
        let timelinePath = parts.prefix(3).joined(separator: "/") + "/" + TranscriptTimeline.filename
        for (filePath, payload, keys) in [(relative, bytes, resultKeys), (timelinePath, timelineBytes, TranscriptTimeline.keys)] {
            let remote = try await ensure(parent: parent, path: filePath, mime: GoogleDriveClient.jsonMIME, journal: &journal, shouldPublish: shouldPublish)
            if let size = remote.size, size != "0" {
                let previous = try await client.json(remote)
                if previous == payload {
                    try await GoogleDriveClient.checkPublication(shouldPublish)
                    continue
                }
                try MobileSyncValidation.object(previous, keys: keys)
                let old = try JSONSerialization.jsonObject(with: previous) as? [String: Any]
                guard old?["processor_id"] as? String == binding.processorID, old?["recording_id"] as? String == parts[1],
                      old?["audio_sha256"] as? String == parts[2] else { throw GoogleDriveError.conflict }
            } else if journal.ids[filePath] != remote.id { throw GoogleDriveError.conflict }
            _ = try await checked(journal)
            try await GoogleDriveClient.checkPublication(shouldPublish)
            try await owner(binding, local: local)
            try await GoogleDriveClient.checkPublication(shouldPublish)
            try await client.upload(id: remote.id, data: payload, shouldPublish: shouldPublish)
            let confirmed = try await client.file(remote.id)
            try GoogleDriveClient.validate(confirmed, parent: parent, path: filePath, mime: GoogleDriveClient.jsonMIME)
            guard try await client.json(confirmed) == payload else { throw GoogleDriveError.conflict }
            try await GoogleDriveClient.checkPublication(shouldPublish)
        }
        // Keep v1 result publication independent of optional speaker failures.
        // Canonical speakers.json has no processor envelope; authority comes
        // from the validated directory and the result published above.
        do {
            guard try output.exists(SpeakerDocument.filename) else { return }
            let payload = try output.read(SpeakerDocument.filename)
            let resultObject = try JSONSerialization.jsonObject(with: bytes) as? [String: Any]
            guard let text = resultObject?["transcript"] as? String else { return }
            let transcript = Transcript(text: text, segments: [], alignment_status: "failed")
            _ = try SpeakerDocument.decode(payload, audioHash: parts[2], transcript: transcript)
            let filePath = parts.prefix(3).joined(separator: "/") + "/" + SpeakerDocument.filename
            let remote = try await ensure(parent: parent, path: filePath, mime: GoogleDriveClient.jsonMIME, journal: &journal, shouldPublish: shouldPublish)
            if let size = remote.size, size != "0" {
                let previous = try await client.json(remote)
                _ = try SpeakerDocument.decode(previous, audioHash: parts[2], transcript: transcript)
                if previous == payload { return }
            } else if journal.ids[filePath] != remote.id { throw GoogleDriveError.conflict }
            _ = try await checked(journal)
            try await owner(binding, local: local)
            try await GoogleDriveClient.checkPublication(shouldPublish)
            try await client.upload(id: remote.id, data: payload, shouldPublish: shouldPublish)
            let confirmed = try await client.file(remote.id)
            try GoogleDriveClient.validate(confirmed, parent: parent, path: filePath, mime: GoogleDriveClient.jsonMIME)
            guard try await client.json(confirmed) == payload else { throw GoogleDriveError.conflict }
            try await GoogleDriveClient.checkPublication(shouldPublish)
        } catch is CancellationError { throw CancellationError() }
        catch GoogleDriveError.staleJob { throw GoogleDriveError.staleJob }
        catch { /* Ordinary result is already complete; preserve old optional data. */ }
    }
}

private final class GoogleDriveAudioSink: @unchecked Sendable {
    let directory: MobileSyncFiles.Directory
    let temporary = ".drive-" + UUID().uuidString.lowercased()
    private let fd: Int32
    private let lock = NSLock()
    private let bytes: Int64
    private let hash: String
    private var count: Int64 = 0
    private var digest = SHA256()
    init(directory: MobileSyncFiles.Directory, bytes: Int64, hash: String) throws {
        self.directory = directory; self.bytes = bytes; self.hash = hash
        fd = openat(directory.fd, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw BrokerFiles.fail() }
    }
    deinit { close(fd) }
    func append(_ data: Data) throws {
        lock.lock(); defer { lock.unlock() }
        try Task.checkCancellation()
        count += Int64(data.count)
        guard count <= bytes else { throw GoogleDriveError.limit }
        digest.update(data: data)
        try data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let n = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                if n < 0, errno == EINTR { continue }
                guard n > 0 else { throw BrokerFiles.fail() }
                offset += n
            }
        }
    }
    func commit() throws {
        lock.lock(); defer { lock.unlock() }
        try Task.checkCancellation()
        guard count == bytes, digest.finalize().map({ String(format: "%02x", $0) }).joined() == hash else { throw GoogleDriveError.invalidResponse }
        guard fsync(fd) == 0 else { throw BrokerFiles.fail() }
        if try directory.exists("audio.m4a") { let existing = try directory.readFD("audio.m4a"); close(existing) }
        guard renameat(directory.fd, temporary, directory.fd, "audio.m4a") == 0, fsync(directory.fd) == 0 else { throw BrokerFiles.fail() }
    }
    func cleanup() { unlinkat(directory.fd, temporary, 0) }
}
