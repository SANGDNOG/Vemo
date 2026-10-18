import Foundation
import Darwin

public enum MobileSyncNamespace: Sendable {
    case localFolder
    case googleDrive
}

/// Exchanges completed recording/result JSON with a user-selected LOCAL synced
/// folder. Successful writes are local receipts, not remote upload confirmations.
public actor MobileSyncBroker {
    private struct Entry: Codable {
        let scope: String
        let recording: MobileSyncRecording
        var memo: Memo
        var mapping: MobileSyncMapping
    }
    private struct Registry: Codable {
        var schemaVersion = 1
        var processorID: String
        var configuration: MobileSyncConfiguration?
        var entries: [String: Entry] = [:]
    }
    private struct Result: Encodable {
        let schema_version = 1
        let recording_id: String
        let audio_sha256: String
        let processor_id: String
        let title: String
        let recorded_at: String
        let transcript: String
        let notes_markdown: String
    }
    private let root: URL
    private let namespace: MobileSyncNamespace
    private var busy = false
    private var native: URL {
        let base = root.appendingPathComponent("native")
        return namespace == .googleDrive ? base.appendingPathComponent("google-mobile") : base
    }
    private var registryURL: URL { native.appendingPathComponent("mobile-sync.json") }

    public init(root: URL, namespace: MobileSyncNamespace = .localFolder) {
        self.root = root.standardizedFileURL
        self.namespace = namespace
    }

    /// Persist identity before remote root registration, retaining the real
    /// artifact root while isolating provider-specific mappings and locks.
    public func processorIdentity() throws -> String {
        let lock = try begin(); defer { end(lock) }
        let registry = try load()
        try save(registry)
        return registry.processorID
    }

    private func begin() throws -> Int32 {
        guard !busy else { throw BrokerFailure("broker_busy", "모바일 폴더 작업이 진행 중입니다.") }
        try BrokerFiles.directory(native)
        let lock = try BrokerFiles.lock(native.appendingPathComponent("mobile-sync.lock"))
        busy = true
        return lock
    }
    private func end(_ fd: Int32) { busy = false; close(fd) }
    private func load() throws -> Registry {
        try BrokerFiles.noLinks(registryURL)
        guard FileManager.default.fileExists(atPath: registryURL.path) else { return Registry(processorID: UUID().uuidString.lowercased()) }
        let value = try JSONDecoder().decode(Registry.self, from: BrokerFiles.read(registryURL))
        guard value.schemaVersion == 1 else { throw BrokerFailure("mobile_registry", "모바일 연결 기록 버전을 지원하지 않습니다.") }
        try MobileSyncValidation.uuid(value.processorID)
        return value
    }
    private func save(_ registry: Registry) throws {
        let data = try BrokerFiles.json(registry)
        guard data.count <= WireCodec.limit else { throw BrokerFailure("oversize_file", "모바일 연결 기록이 크기 제한을 초과했습니다.") }
        try BrokerFiles.atomic(data, to: registryURL)
    }
    private func configured(_ registry: Registry) throws -> MobileSyncConfiguration {
        guard let config = registry.configuration, config.processorID == registry.processorID else { throw BrokerFailure("mobile_not_configured", "모바일 동기화 폴더를 연결하세요.") }
        return config
    }
    private func directory(_ config: MobileSyncConfiguration) throws -> MobileSyncFiles.Directory {
        let directory = try MobileSyncFiles.Directory(config.syncRoot)
        var info = stat()
        guard fstat(directory.fd, &info) == 0, info.st_dev == config.device, info.st_ino == config.inode else { throw BrokerFailure("sync_root_changed", "연결한 동기화 폴더가 교체되었습니다. 폴더를 다시 연결하세요.") }
        return directory
    }
    private func processor(_ directory: MobileSyncFiles.Directory) throws -> MobileSyncProcessor {
        let data = try directory.read("processor.json")
        try MobileSyncValidation.object(data, keys: ["schema_version", "processor_id", "display_name"])
        return try JSONDecoder().decode(MobileSyncProcessor.self, from: data)
    }
    private func owner(_ directory: MobileSyncFiles.Directory, config: MobileSyncConfiguration) throws {
        guard try processor(directory).processorID == config.processorID else { throw BrokerFailure("processor_conflict", "이 동기화 폴더는 다른 처리기에 등록되어 있습니다.") }
    }
    private func current(_ config: MobileSyncConfiguration) throws {
        _ = try directory(config)
    }

    @discardableResult
    public func register(syncRoot: URL, displayName: String) throws -> MobileSyncConfiguration {
        let lock = try begin(); defer { end(lock) }
        guard syncRoot.isFileURL, syncRoot.standardizedFileURL.path != "/",
              !displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !displayName.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              displayName.utf8.count <= 1024 else { throw BrokerFailure("invalid_sync_configuration", "로컬 동기화 폴더와 처리기 이름을 확인하세요.") }
        let path = syncRoot.standardizedFileURL
        // Drive's owned mirror is intentionally inside stateRoot. Exempt only
        // its canonical digest directory; user-selected local folders and all
        // other internal directories retain the overlap prohibition.
        let googleCache = root.appendingPathComponent("native/google-drive/cache")
        let isGoogleCache = namespace == .googleDrive
            && path.deletingLastPathComponent().path == googleCache.path
            && (try? MobileSyncValidation.hash(path.lastPathComponent)) != nil
        if isGoogleCache { try BrokerFiles.noLinks(path) }
        else { try FolderConnection.validate(input: root, output: path) }
        let directory = try MobileSyncFiles.Directory(path)
        var info = stat(); guard fstat(directory.fd, &info) == 0 else { throw BrokerFiles.fail() }
        var registry = try load()
        // Persist the device identity before claiming the cloud owner. A crash
        // between claim and configuration save must not generate another owner.
        try save(registry)
        let config = MobileSyncConfiguration(syncRoot: path, processorID: registry.processorID, displayName: displayName, device: info.st_dev, inode: info.st_ino)
        if try directory.exists("processor.json") { try owner(directory, config: config) }
        else {
            do { try directory.atomic(BrokerFiles.json(MobileSyncProcessor(processorID: config.processorID, displayName: displayName)), name: "processor.json", replace: false) }
            catch let error as BrokerFailure where error.code == "processor_conflict" { try owner(directory, config: config) }
        }
        try current(config); try owner(directory, config: config)
        registry.configuration = config
        try save(registry)
        return config
    }

    public func configuration() throws -> MobileSyncConfiguration? { try load().configuration }
    public func disconnect() throws {
        let lock = try begin(); defer { end(lock) }
        var registry = try load(); registry.configuration = nil; try save(registry)
    }
    public func mappings() throws -> [MobileSyncMapping] {
        let registry = try load()
        guard let config = registry.configuration else { return [] }
        return registry.entries.values.filter { $0.scope == config.scope }.map(\.mapping).sorted { $0.recordingID < $1.recordingID }
    }
    public func markDelivered(sourceReference: String, jobID: String) throws {
        let lock = try begin(); defer { end(lock) }
        _ = try Validation.component(jobID)
        var registry = try load(); let config = try configured(registry)
        guard let key = registry.entries.first(where: { $0.value.scope == config.scope && $0.value.mapping.sourceReference == sourceReference })?.key else { throw BrokerFailure("unknown_mobile_mapping", "현재 폴더의 모바일 가져오기 기록이 없습니다.") }
        if let previous = registry.entries[key]?.mapping.jobID, previous != jobID { throw BrokerFailure("mobile_job_conflict", "모바일 녹음에 다른 작업이 이미 연결되어 있습니다.") }
        registry.entries[key]?.mapping.jobID = jobID; registry.entries[key]?.mapping.delivered = true
        try save(registry)
    }
    public func bindJob(memoUUID: String, jobID: String) throws {
        let registry = try load(); let config = try configured(registry)
        guard let entry = registry.entries.values.first(where: { $0.scope == config.scope && $0.mapping.memoUUID == memoUUID }) else { throw BrokerFailure("unknown_mobile_mapping", "현재 폴더의 모바일 작업이 아닙니다.") }
        try markDelivered(sourceReference: entry.mapping.sourceReference, jobID: jobID)
    }
    private func issue(_ error: Error, id: String?) -> MobileSyncIssue {
        let failure = error as? BrokerFailure
        return MobileSyncIssue(recordingID: id, code: failure?.code ?? "invalid_recording", message: failure?.message ?? "모바일 녹음을 확인하지 못했습니다. 완료 정보와 파일을 확인하세요.")
    }

    /// Imports verified audio into ImportBroker's durable owned cache. Pending
    /// memos may be replayed until bindJob/markDelivered; their identity is stable.
    public func scan(importBroker: ImportBroker) async throws -> MobileSyncScan {
        let lock = try begin(); defer { end(lock) }
        var registry = try load(); let config = try configured(registry)
        let cloud = try directory(config); try owner(cloud, config: config)
        var report = MobileSyncScan()
        guard try cloud.exists("recordings") else { return report }
        let recordings = try cloud.child("recordings")
        let staging = native.appendingPathComponent("mobile-staging"); try BrokerFiles.directory(staging)
        var itemCount = 0
        for id in try recordings.names() {
            do {
                try MobileSyncValidation.uuid(id)
                let revisions = try recordings.child(id)
                for hash in try revisions.names() {
                    itemCount += 1
                    guard itemCount <= 50_000 else { throw BrokerFailure("sync_item_limit", "동기화 항목 수가 제한을 초과했습니다.") }
                    do {
                        try Task.checkCancellation()
                        try MobileSyncValidation.hash(hash)
                        let item = try revisions.child(hash)
                        guard try item.exists("recording.json") else { report.skipped += 1; continue }
                        let data = try item.read("recording.json"), request = try MobileSyncRecording.decode(data)
                        guard request.recording_id == id, request.audio_sha256 == hash else { throw BrokerFailure("mobile_path_conflict", "모바일 폴더와 완료 정보의 식별자가 다릅니다.") }
                        guard request.mode == "desktop", request.target_processor_id == config.processorID else { report.skipped += 1; continue }
                        let key = config.scope + ":" + id
                        if let existing = registry.entries[key] {
                            guard existing.recording == request else { throw BrokerFailure("mobile_identity_conflict", "동일 모바일 녹음의 완료 정보가 변경되었습니다. 기존 항목을 보존했습니다.") }
                            if existing.mapping.suppressed { report.skipped += 1; continue }
                            if existing.mapping.delivered {
                                // The request remains immutable. Consult the tiny shared
                                // tombstone registry but never open/copy/hash settled audio
                                // every polling tick. Publication rechecks source hashes.
                                do {
                                    try await importBroker.validateMobileImport(reference: existing.mapping.sourceReference, uuid: existing.mapping.memoUUID, hash: hash)
                                } catch let error as BrokerFailure where error.code == "import_purged" {
                                    registry.entries[key]?.mapping.suppressed = true; try save(registry)
                                }
                                report.skipped += 1; continue
                            }
                        }
                        let reference = "import:mobile:" + config.scope + ":" + id + ":" + hash
                        let localUUID = MobileSyncValidation.localUUID(reference)
                        let temporary = staging.appendingPathComponent(UUID().uuidString + ".m4a")
                        defer { try? FileManager.default.removeItem(at: temporary) }
                        try item.copyAudio("audio.m4a", to: temporary, bytes: request.audio_bytes, hash: hash)
                        guard try item.read("recording.json") == data else { throw BrokerFailure("source_changing", "완료 정보가 변경 중입니다.") }
                        try current(config); try owner(cloud, config: config)
                        let metadata = MobileSyncImportMetadata(recording: request, memoUUID: localUUID, sourceReference: reference)
                        let memo: Memo
                        do { memo = try await importBroker.importMobileFile(temporary, metadata: metadata) }
                        catch let error as BrokerFailure where error.code == "import_purged" {
                            if registry.entries[key] != nil { registry.entries[key]?.mapping.suppressed = true; try save(registry) }
                            report.skipped += 1; continue
                        }
                        try current(config); try owner(cloud, config: config)
                        let mapping = registry.entries[key]?.mapping ?? MobileSyncMapping(recordingID: id, audioSHA256: hash, memoUUID: localUUID, sourceReference: reference, jobID: nil, delivered: false, suppressed: false)
                        registry.entries[key] = Entry(scope: config.scope, recording: request, memo: memo, mapping: mapping)
                        // ImportBroker is committed first. An interrupted journal write
                        // simply rediscovers the same assigned import, never a new job.
                        try save(registry)
                        if !mapping.delivered { report.pending.append(memo) }
                    } catch is CancellationError { throw CancellationError() }
                    catch { report.issues.append(issue(error, id: id)) }
                }
            } catch is CancellationError { throw CancellationError() }
            catch { report.issues.append(issue(error, id: id)) }
        }
        try current(config); try owner(cloud, config: config)
        return report
    }

    /// Writes exactly the v1 result fields, only for the current root's mapped job.
    /// Reads the job's owned original audio and validated transcript/notes artifacts.
    @discardableResult
    public func publish(job: Job) async throws -> URL {
        let lock = try begin(); defer { end(lock) }
        let registry = try load(), config = try configured(registry)
        let cloud = try directory(config); try owner(cloud, config: config)
        guard let entry = registry.entries.values.first(where: { $0.scope == config.scope && $0.mapping.jobID == job.id && $0.mapping.memoUUID == job.uuid }),
              !entry.mapping.suppressed, entry.mapping.delivered,
              job.deleted_at == nil, job.purge_pending != true, job.status == "completed", job.notes_status == "completed",
              job.recorded_at == entry.recording.recorded_at, let audioPath = job.audio_path,
              let artifactPath = job.artifact_path, let notesPath = job.notes_path else { throw BrokerFailure("mobile_result_not_ready", "현재 폴더에 연결된 녹음의 전사와 노트가 모두 완료되어야 결과를 저장할 수 있습니다.") }
        // Consult the shared import tombstone as well as the supplied job snapshot.
        try await ImportBroker(root: root).validateMobileImport(reference: entry.mapping.sourceReference, uuid: job.uuid, hash: entry.recording.audio_sha256)
        let jobArtifacts = root.appendingPathComponent("artifacts/" + (try Validation.component(job.id)))
        let audio = try Validation.owned(audioPath, root: root), artifact = try Validation.owned(artifactPath, root: jobArtifacts), notesPathURL = try Validation.owned(notesPath, root: jobArtifacts)
        let audioStamp = try BrokerFiles.stamp(audio), artifactStamp = try BrokerFiles.stamp(artifact), notesStamp = try BrokerFiles.stamp(notesPathURL)
        guard audioStamp.size == entry.recording.audio_bytes, try BrokerFiles.hash(audio) == entry.recording.audio_sha256 else { throw BrokerFailure("mobile_source_mismatch", "작업 원본 오디오가 모바일 녹음과 일치하지 않습니다.") }
        let transcript = try JSONDecoder().decode(Transcript.self, from: BrokerFiles.read(artifact))
        guard !transcript.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw BrokerFailure("empty_transcript", "전사문이 비어 있습니다.") }
        let notes = try NotesDisplay.validated(JSONDecoder().decode(RecordingNotes.self, from: BrokerFiles.read(notesPathURL)), text: transcript.text)
        guard !notes.summary.isEmpty || !notes.highlights.isEmpty || !notes.topics.isEmpty else { throw BrokerFailure("empty_notes", "검증된 노트 내용이 없습니다.") }
        let requestFolder = try cloud.child("recordings").child(entry.recording.recording_id).child(entry.recording.audio_sha256)
        guard try MobileSyncRecording.decode(requestFolder.read("recording.json")) == entry.recording else { throw BrokerFailure("mobile_identity_conflict", "모바일 녹음의 완료 정보가 변경되었습니다.") }
        let result = Result(recording_id: entry.recording.recording_id, audio_sha256: entry.recording.audio_sha256, processor_id: config.processorID, title: job.title, recorded_at: entry.recording.recorded_at, transcript: transcript.text, notes_markdown: NotesDisplay.markdown(notes, title: job.title))
        let data = try BrokerFiles.json(result)
        let timeline = try BrokerFiles.json(TranscriptTimeline(recordingID: result.recording_id,
            audioSHA256: result.audio_sha256, processorID: config.processorID, transcript: transcript))
        guard data.count <= WireCodec.limit, timeline.count <= WireCodec.limit else { throw BrokerFailure("oversize_file", "모바일 결과 JSON이 8 MiB 제한을 초과했습니다.") }
        guard audioStamp == (try BrokerFiles.stamp(audio)), artifactStamp == (try BrokerFiles.stamp(artifact)), notesStamp == (try BrokerFiles.stamp(notesPathURL)) else { throw BrokerFailure("source_changing", "처리 결과가 변경 중입니다. 다음 검사에서 다시 시도합니다.") }
        try current(config); try owner(cloud, config: config); try Task.checkCancellation()
        let output = try cloud.child("results", create: true).child(entry.recording.recording_id, create: true).child(entry.recording.audio_sha256, create: true)
        var pending: [(String, Data)] = []
        // Preflight both identities before replacing either file.
        for (name, bytes) in [("result.json", data), (TranscriptTimeline.filename, timeline)] {
            if try output.exists(name) {
                let previous = try output.read(name)
                if previous == bytes { continue }
                let object = try JSONSerialization.jsonObject(with: previous) as? [String: Any]
                guard object?["recording_id"] as? String == result.recording_id,
                      object?["audio_sha256"] as? String == result.audio_sha256,
                      object?["processor_id"] as? String == config.processorID else {
                    throw BrokerFailure("mobile_result_conflict", "기존 모바일 결과의 식별자가 다릅니다. 기존 파일을 보존했습니다.")
                }
            }
            pending.append((name, bytes))
        }
        for (name, bytes) in pending {
            try current(config); try owner(cloud, config: config); try Task.checkCancellation()
            guard try MobileSyncRecording.decode(requestFolder.read("recording.json")) == entry.recording else {
                throw BrokerFailure("mobile_identity_conflict", "모바일 녹음의 완료 정보가 변경되었습니다.")
            }
            try output.atomic(bytes, name: name, replace: true)
        }
        // Optional metadata never makes a valid v1 result fail. The owned result
        // above establishes recording/processor authority for this directory.
        if let speakers = try? SpeakerDocument.load(job: job, root: root, transcript: transcript),
           let bytes = try? BrokerFiles.json(speakers) {
            do {
                try current(config); try owner(cloud, config: config); try Task.checkCancellation()
                guard try MobileSyncRecording.decode(requestFolder.read("recording.json")) == entry.recording else {
                    throw BrokerFailure("mobile_identity_conflict", "모바일 녹음 정보가 변경되었습니다.")
                }
                if try output.exists(SpeakerDocument.filename) {
                    let previous = try output.read(SpeakerDocument.filename)
                    _ = try SpeakerDocument.decode(previous, audioHash: result.audio_sha256, transcript: transcript)
                }
                try output.atomic(bytes, name: SpeakerDocument.filename, replace: true)
            } catch is CancellationError { throw CancellationError() }
            catch { /* Preserve ordinary result and any existing optional sidecar. */ }
        }
        return output.url.appendingPathComponent("result.json")
    }
}
