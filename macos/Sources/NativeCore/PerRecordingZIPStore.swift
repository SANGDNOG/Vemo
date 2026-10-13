import Foundation
import Darwin
import CryptoKit

public actor PerRecordingZIPStore {
    private let root: URL
    public init(root: URL) { self.root = root.standardizedFileURL }

    /// nil means an intact archive already exists for this recording/content/destination.
    /// The caller schedules changes and bounded reconciliation; this actor owns no timer.
    public func exportIfNeeded(job: Job, destination: URL,
                               organization: LibraryOrganization = LibraryOrganization(),
                               progress: @escaping @Sendable (String) async -> Void = { _ in }) async throws -> URL? {
        let result = try await export(job:job,destination:destination,organization:organization,progress:progress)
        return result.created ? result.url : nil
    }

    public func relocateIfNeeded(jobID: String, destination: URL, organization: LibraryOrganization,
                                  progress: @escaping @Sendable (String) async -> Void = { _ in }) async throws -> URL? {
        try Task.checkCancellation()
        let destination = destination.standardizedFileURL
        let rootStamp = try destinationStamp(destination)
        let components = try ZIPFolderLayout.components(jobID:jobID,organization:organization)
        let bucket = try receiptBucket(destination:destination,jobID:jobID)
        guard try exists(bucket) else { return nil }
        let lock = try BrokerFiles.lock(bucket.appendingPathComponent(".lock")); defer { close(lock) }
        let target = try folder(destination,components:components)
        return try await relocate(jobID:jobID,destination:destination,target:target,bucket:bucket,rootStamp:rootStamp,progress:progress)
    }

    // Persist full nanosecond stamps, including ctime/inode, so an unchanged large audio
    // is not hashed again on reconciliation or restart. Paths alone are never a cache key.
    private struct Stamp: Codable, Equatable {
        let device: Int32, inode: UInt64, size: Int64, modified: Int64, changed: Int64
        init(_ stamp: FileStamp) {
            device = stamp.device; inode = stamp.inode; size = stamp.size
            modified = stamp.modified; changed = stamp.changed
        }
        init(_ url: URL) throws { self.init(try BrokerFiles.stamp(url)) }
    }
    private struct Inputs: Codable, Equatable {
        let jobID: String, uuid: String, title: String
        let recordingDate: String?, recordedAt: String?
        let audioPath: String, artifactPath: String, notesPath: String, audioName: String
        let audioStamp: Stamp, artifactStamp: Stamp, notesStamp: Stamp
    }
    private struct Content: Codable {
        let schemaVersion: Int
        let jobID: String, uuid: String, title: String
        let recordingDate: String?, recordedAt: String?
        let audioName: String, audioHash: String, artifactHash: String, notesHash: String
    }
    private struct Receipt: Codable {
        let schemaVersion: Int
        var destination: String
        let jobID: String, fingerprint: String, audioHash: String
        var configuredRoot: String? = nil
        var inputs: Inputs
        var archiveName: String, archiveHash: String
        var archiveStamp: Stamp?
    }
    private struct MoveIntent: Codable {
        let schemaVersion: Int
        let source: Receipt
        let target: Receipt
        let sourceStamp: Stamp
    }
    private struct CachedReceipt { let stamp: Stamp; let value: Receipt }
    private var cache: [String:CachedReceipt] = [:]

    private func hash(_ data: Data) -> String {
        SHA256.hash(data:data).map { String(format:"%02x",$0) }.joined()
    }
    private func isHash(_ value: String) -> Bool {
        value.count == 64 && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
    private func snapshot(_ job: Job) throws -> Inputs {
        guard job.deleted_at == nil, ["completed","needs_recording_date"].contains(job.status),
              job.notes_status == "completed", let audioPath = job.audio_path,
              let artifactPath = job.artifact_path, let notesPath = job.notes_path else {
            throw NativeFailure.invalid("휴지통에 없는 녹음의 전사와 노트가 모두 완료되어야 ZIP을 저장할 수 있습니다.")
        }
        let audio = try Validation.owned(audioPath,root:root)
        let artifact = try Validation.owned(artifactPath,root:root)
        let notes = try Validation.owned(notesPath,root:root)
        let audioStamp = try Stamp(audio)
        guard audioStamp.size > 0 else { throw NativeFailure.invalid("원본 오디오가 비어 있습니다.") }
        let ext = audio.pathExtension
        guard !ext.isEmpty, ext.utf8.count <= 32, ext.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) }) else {
            throw NativeFailure.invalid("원본 오디오의 안전한 파일 확장자가 필요합니다.")
        }
        return Inputs(jobID:job.id,uuid:job.uuid,title:job.title,recordingDate:job.recording_date,recordedAt:job.recorded_at,
                      audioPath:audio.path,artifactPath:artifact.path,notesPath:notes.path,audioName:"original." + ext,
                      audioStamp:audioStamp,artifactStamp:try Stamp(artifact),notesStamp:try Stamp(notes))
    }
    private func unchanged(_ inputs: Inputs, job: Job) throws {
        guard inputs == (try snapshot(job)) else {
            throw NativeFailure.invalid("ZIP 입력이 변경되었습니다. 동기화가 끝나면 다시 시도하세요.")
        }
    }
    private func stableMetadata(_ path: String, stamp: Stamp) throws -> Data {
        let url = URL(fileURLWithPath:path)
        guard stamp == (try Stamp(url)) else { throw BrokerFiles.fail("changing_file") }
        let data = try BrokerFiles.read(url)
        guard stamp == (try Stamp(url)) else { throw BrokerFiles.fail("changing_file") }
        return data
    }
    private func exists(_ url: URL) throws -> Bool {
        try BrokerFiles.noLinks(url)
        var s = stat()
        if lstat(url.path,&s) == 0 { return true }
        guard errno == ENOENT else { throw BrokerFiles.fail() }
        return false
    }
    private func destinationStamp(_ destination: URL) throws -> Stamp {
        try BrokerFiles.noLinks(destination)
        var s = stat()
        guard lstat(destination.path,&s) == 0, s.st_mode & S_IFMT == S_IFDIR else {
            throw NativeFailure.invalid("ZIP 저장 폴더를 사용할 수 없습니다. 폴더 연결 후 다시 시도하세요.")
        }
        return Stamp(FileStamp(s))
    }
    private func sameDestination(_ destination: URL, stamp: Stamp) throws {
        let current = try destinationStamp(destination)
        guard current.device == stamp.device, current.inode == stamp.inode else {
            throw NativeFailure.invalid("ZIP 저장 폴더가 변경되었습니다. 다시 시도하세요.")
        }
    }
    private func receiptBucket(destination: URL, jobID: String) throws -> URL {
        // Keep the original flat-export bucket key. Folder changes never change identity.
        root.appendingPathComponent("native/recording-zips")
            .appendingPathComponent(hash(try BrokerFiles.json([destination.path,jobID])))
    }
    private func folder(_ destination: URL, components: [String]) throws -> URL {
        var result = destination
        for component in components {
            _ = try Validation.component(component)
            result.appendPathComponent(component,isDirectory:true)
            try BrokerFiles.directory(result)
        }
        return result.standardizedFileURL
    }
    private func validate(_ receipt: Receipt, destination: URL, jobID: String, fingerprint: String? = nil) throws {
        _ = try Validation.component(receipt.archiveName)
        let location = URL(fileURLWithPath:receipt.destination).standardizedFileURL
        let belongs = receipt.configuredRoot.map { $0 == destination.path && (location.path == destination.path || location.path.hasPrefix(destination.path + "/")) }
            ?? (location.path == destination.path)
        guard receipt.schemaVersion == 1, belongs, receipt.destination == location.path, receipt.jobID == jobID,
              receipt.inputs.jobID == jobID, isHash(receipt.fingerprint), isHash(receipt.audioHash), isHash(receipt.archiveHash),
              receipt.archiveName.hasSuffix(".zip"), fingerprint == nil || fingerprint == receipt.fingerprint else {
            throw BrokerFiles.fail("zip_receipt_conflict","ZIP 저장 기록이 일치하지 않습니다. 기존 파일을 보존했습니다.")
        }
    }
    private func load(_ url: URL, destination: URL, jobID: String, fingerprint: String? = nil) throws -> Receipt? {
        guard try exists(url) else { cache.removeValue(forKey:url.path); return nil }
        let stamp = try Stamp(url)
        let receipt: Receipt
        if let cached = cache[url.path], cached.stamp == stamp { receipt = cached.value }
        else {
            receipt = try JSONDecoder().decode(Receipt.self,from:stableMetadata(url.path,stamp:stamp))
            cache[url.path] = CachedReceipt(stamp:stamp,value:receipt)
        }
        try validate(receipt,destination:destination,jobID:jobID,fingerprint:fingerprint)
        return receipt
    }
    private func save(_ receipt: Receipt, journal: URL, current: URL?) throws {
        try BrokerFiles.atomic(BrokerFiles.json(receipt),to:journal)
        if let current { try BrokerFiles.atomic(BrokerFiles.json(receipt),to:current) }
    }
    private func validArchive(_ receipt: Receipt, destination: URL) throws -> Stamp? {
        let archive = destination.appendingPathComponent(receipt.archiveName)
        // A user directory/symlink is a collision, never an archive or a cleanup target.
        try BrokerFiles.noLinks(destination)
        var s = stat()
        if lstat(archive.path,&s) != 0 {
            guard errno == ENOENT else { throw BrokerFiles.fail() }
            return nil
        }
        guard s.st_mode & S_IFMT == S_IFREG else { return nil }
        let stamp = try Stamp(archive)
        if let expected = receipt.archiveStamp, expected == stamp { return stamp }
        guard try BrokerFiles.hash(archive) == receipt.archiveHash else { return nil }
        guard stamp == (try Stamp(archive)) else { throw BrokerFiles.fail("zip_archive_changed") }
        return stamp
    }
    private func verifiedMoveSource(_ receipt: Receipt) throws -> Stamp? {
        let directory = URL(fileURLWithPath:receipt.destination)
        let archive = directory.appendingPathComponent(receipt.archiveName)
        try BrokerFiles.noLinks(directory)
        var s = stat()
        if lstat(archive.path,&s) != 0 {
            guard errno == ENOENT else { throw BrokerFiles.fail() }
            return nil
        }
        guard s.st_mode & S_IFMT == S_IFREG else { throw BrokerFiles.fail("zip_archive_changed") }
        let stamp = try Stamp(archive)
        guard try BrokerFiles.hash(archive) == receipt.archiveHash, stamp == (try Stamp(archive)) else {
            throw BrokerFiles.fail("zip_archive_changed","이동할 ZIP이 변경되었습니다. 기존 파일과 기록을 보존했습니다.")
        }
        return stamp
    }
    private func moveCommit(_ intent: MoveIntent, stamp: Stamp, bucket: URL, destination: URL, jobID: String) throws {
        var receipt = intent.target
        receipt.archiveStamp = stamp
        let current = bucket.appendingPathComponent("current.json")
        let previous = try load(current,destination:destination,jobID:jobID)
        try save(receipt,journal:bucket.appendingPathComponent(receipt.fingerprint + ".json"),
                 current:previous?.fingerprint == receipt.fingerprint ? current : nil)
        let intentURL = bucket.appendingPathComponent("move.json")
        try FileManager.default.removeItem(at:intentURL)
        try BrokerFiles.syncDirectory(bucket)
    }
    private func resumeMove(jobID: String, destination: URL, bucket: URL) throws -> URL? {
        let intentURL = bucket.appendingPathComponent("move.json")
        guard try exists(intentURL) else { return nil }
        let intent = try JSONDecoder().decode(MoveIntent.self,from:BrokerFiles.read(intentURL))
        try validate(intent.source,destination:destination,jobID:jobID)
        try validate(intent.target,destination:destination,jobID:jobID,fingerprint:intent.source.fingerprint)
        guard intent.schemaVersion == 1, intent.source.archiveHash == intent.target.archiveHash else {
            throw BrokerFiles.fail("zip_move_conflict")
        }
        let source = URL(fileURLWithPath:intent.source.destination).appendingPathComponent(intent.source.archiveName)
        let target = URL(fileURLWithPath:intent.target.destination).appendingPathComponent(intent.target.archiveName)
        try BrokerFiles.noLinks(source.deletingLastPathComponent()); try BrokerFiles.noLinks(target.deletingLastPathComponent())
        var sourceStat = stat(), targetStat = stat()
        let hasSource = lstat(source.path,&sourceStat) == 0
        if !hasSource, errno != ENOENT { throw BrokerFiles.fail() }
        let hasTarget = lstat(target.path,&targetStat) == 0
        if !hasTarget, errno != ENOENT { throw BrokerFiles.fail() }
        if !hasSource, hasTarget {
            // Same inode is rename provenance. A coincidental same-hash foreign ZIP
            // appearing after intent is never sufficient evidence for adoption.
            guard targetStat.st_mode & S_IFMT == S_IFREG,
                  targetStat.st_dev == intent.sourceStamp.device, targetStat.st_ino == intent.sourceStamp.inode,
                  let stamp = try verifiedMoveSource(intent.target) else { throw BrokerFiles.fail("zip_move_conflict") }
            try BrokerFiles.syncDirectory(source.deletingLastPathComponent())
            try BrokerFiles.syncDirectory(target.deletingLastPathComponent())
            try moveCommit(intent,stamp:stamp,bucket:bucket,destination:destination,jobID:jobID)
            return target
        }
        // No rename happened. Preserve both files, remove only the private intent;
        // a fresh exclusive target will be selected if the source is still present.
        if hasSource { _ = try verifiedMoveSource(intent.source) }
        try FileManager.default.removeItem(at:intentURL)
        try BrokerFiles.syncDirectory(bucket)
        return nil
    }
    private func relocate(jobID: String, destination: URL, target: URL, bucket: URL, rootStamp: Stamp,
                          progress: @Sendable (String) async -> Void) async throws -> URL? {
        let currentURL = bucket.appendingPathComponent("current.json")
        let initial = try load(currentURL,destination:destination,jobID:jobID)
        let resumed = try resumeMove(jobID:jobID,destination:destination,bucket:bucket)
        let afterResume = try load(currentURL,destination:destination,jobID:jobID)
        var movedCurrent = resumed != nil && (initial?.destination != afterResume?.destination || initial?.archiveName != afterResume?.archiveName)
        var candidates: [(Receipt,Stamp)] = []
        let journals = try FileManager.default.contentsOfDirectory(at:bucket,includingPropertiesForKeys:nil)
            .filter { $0.pathExtension == "json" && $0.lastPathComponent != "current.json" && $0.lastPathComponent != "move.json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        // Preflight every receipt before relocating anything. Missing versions stay
        // recorded; malformed or changed versions cannot authorize a deletion.
        for journal in journals {
            let fingerprint = journal.deletingPathExtension().lastPathComponent
            guard isHash(fingerprint), let receipt = try load(journal,destination:destination,jobID:jobID,fingerprint:fingerprint) else {
                throw BrokerFiles.fail("zip_receipt_conflict")
            }
            guard receipt.destination != target.path else { continue }
            if let stamp = try verifiedMoveSource(receipt) { candidates.append((receipt,stamp)) }
        }
        let targetStamp = try destinationStamp(target)
        for (sourceReceipt,sourceStamp) in candidates {
            try Task.checkCancellation()
            await progress("ZIP 폴더 이동 준비 중")
            let source = URL(fileURLWithPath:sourceReceipt.destination).appendingPathComponent(sourceReceipt.archiveName)
            var targetReceipt = sourceReceipt
            targetReceipt.configuredRoot = destination.path; targetReceipt.destination = target.path; targetReceipt.archiveStamp = nil
            let base = name(sourceReceipt.inputs.title,recordingDate:sourceReceipt.inputs.recordingDate)
            var ordinal = 1
            while true {
                try Task.checkCancellation()
                targetReceipt.archiveName = base + (ordinal == 1 ? "" : " (\(ordinal))") + ".zip"
                let output = target.appendingPathComponent(targetReceipt.archiveName)
                var s = stat()
                if lstat(output.path,&s) == 0 { ordinal += 1; continue }
                guard errno == ENOENT else { throw BrokerFiles.fail() }
                let intent = MoveIntent(schemaVersion:1,source:sourceReceipt,target:targetReceipt,sourceStamp:sourceStamp)
                try BrokerFiles.atomic(BrokerFiles.json(intent),to:bucket.appendingPathComponent("move.json"))
                await progress("ZIP 폴더 이동 직전")
                try sameDestination(destination,stamp:rootStamp); try sameDestination(target,stamp:targetStamp)
                guard try verifiedMoveSource(sourceReceipt) == sourceStamp else { throw BrokerFiles.fail("zip_archive_changed") }
                try Task.checkCancellation()
                if renamex_np(source.path,output.path,UInt32(RENAME_EXCL)) != 0 {
                    guard errno == EEXIST else { throw BrokerFiles.fail() }
                    ordinal += 1; continue
                }
                // Once rename removes the source, finish durable receipts even if cancelled.
                try BrokerFiles.syncDirectory(source.deletingLastPathComponent()); try BrokerFiles.syncDirectory(target)
                guard let stamp = try verifiedMoveSource(targetReceipt), stamp.device == sourceStamp.device, stamp.inode == sourceStamp.inode else {
                    throw BrokerFiles.fail("zip_move_conflict")
                }
                try moveCommit(intent,stamp:stamp,bucket:bucket,destination:destination,jobID:jobID)
                if sourceReceipt.fingerprint == initial?.fingerprint { movedCurrent = true }
                break
            }
        }
        guard movedCurrent, let current = try load(currentURL,destination:destination,jobID:jobID) else { return nil }
        return URL(fileURLWithPath:current.destination).appendingPathComponent(current.archiveName)
    }
    private func name(_ title: String, recordingDate: String?) -> String {
        let forbidden = CharacterSet.controlCharacters.union(CharacterSet(charactersIn:"/\\:*?\"<>|"))
        let clean = title.unicodeScalars.map { forbidden.contains($0) ? "_" : String($0) }.joined()
            .trimmingCharacters(in:CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn:".")))
        let suffix = recordingDate.flatMap { date in
            (try? Validation.recordingDate(date)) == nil ? nil : " — " + date
        } ?? ""
        let dateSuffix = clean.hasSuffix(suffix) ? suffix : ""
        let topic = clean.dropLast(dateSuffix.count)
        var result = ""
        for character in topic {
            guard result.utf8.count + String(character).utf8.count <= 120 - dateSuffix.utf8.count else { break }
            result.append(character)
        }
        return (result.isEmpty ? "녹음" : result) + dateSuffix
    }
    private func execute(_ executable: String, _ arguments: [String], log: URL) throws {
        try Task.checkCancellation()
        try BrokerFiles.writeNew(Data(),to:log)
        let output = try FileHandle(forWritingTo:log)
        defer { try? output.close() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath:executable); process.arguments = arguments
        process.standardOutput = output; process.standardError = output
        try process.run(); process.waitUntilExit()
        try Task.checkCancellation()
        guard process.terminationReason == .exit, process.terminationStatus == 0 else {
            throw NativeFailure.invalid("ZIP 압축 또는 무결성 확인에 실패했습니다. 저장 공간과 폴더 권한을 확인하세요.")
        }
    }

    // Used by exportOne as well, to return the existing URL on a verified replay.
    func export(job: Job, destination: URL, organization: LibraryOrganization = LibraryOrganization(),
                progress: @Sendable (String) async -> Void) async throws -> (url: URL, created: Bool) {
        try Task.checkCancellation()
        await progress("ZIP 입력 확인 중")
        let configuredRoot = destination.standardizedFileURL
        let rootStamp = try destinationStamp(configuredRoot)
        let components = try ZIPFolderLayout.components(jobID:job.id,organization:organization)
        let inputs = try snapshot(job)
        let bucket = try receiptBucket(destination:configuredRoot,jobID:job.id)
        try BrokerFiles.directory(bucket)
        // Covers exports and relocations across actor instances/processes.
        let lock = try BrokerFiles.lock(bucket.appendingPathComponent(".lock")); defer { close(lock) }
        let destination = try folder(configuredRoot,components:components)
        let folderStamp = try destinationStamp(destination)
        let relocated = try await relocate(jobID:job.id,destination:configuredRoot,target:destination,bucket:bucket,rootStamp:rootStamp,progress:progress)
        let current = bucket.appendingPathComponent("current.json")
        let previous = try load(current,destination:configuredRoot,jobID:job.id)
        if var receipt = previous, receipt.inputs == inputs, receipt.destination == destination.path,
           let stamp = try validArchive(receipt,destination:destination) {
            try unchanged(inputs,job:job); try sameDestination(destination,stamp:folderStamp); try Task.checkCancellation()
            if receipt.archiveStamp != stamp {
                receipt.archiveStamp = stamp
                try save(receipt,journal:bucket.appendingPathComponent(receipt.fingerprint + ".json"),current:current)
            }
            return (destination.appendingPathComponent(receipt.archiveName),relocated != nil)
        }

        let artifact = try stableMetadata(inputs.artifactPath,stamp:inputs.artifactStamp)
        let transcript = try JSONDecoder().decode(Transcript.self,from:artifact)
        guard !transcript.text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty else {
            throw NativeFailure.invalid("전사문이 비어 있어 ZIP을 만들지 못했습니다.")
        }
        let notesData = try stableMetadata(inputs.notesPath,stamp:inputs.notesStamp)
        let notes = try NotesDisplay.validated(JSONDecoder().decode(RecordingNotes.self,from:notesData),text:transcript.text)
        let audioHash: String
        if let previous, previous.inputs.audioPath == inputs.audioPath, previous.inputs.audioStamp == inputs.audioStamp {
            audioHash = previous.audioHash
        } else {
            await progress("원본 오디오 확인 중")
            audioHash = try BrokerFiles.hash(URL(fileURLWithPath:inputs.audioPath))
        }
        try unchanged(inputs,job:job)
        let fingerprint = hash(try BrokerFiles.json(Content(schemaVersion:1,jobID:job.id,uuid:job.uuid,title:job.title,
            recordingDate:job.recording_date,recordedAt:job.recorded_at,audioName:inputs.audioName,
            audioHash:audioHash,artifactHash:hash(artifact),notesHash:hash(notesData))))
        let journal = bucket.appendingPathComponent(fingerprint + ".json")
        if var receipt = try load(journal,destination:configuredRoot,jobID:job.id,fingerprint:fingerprint), receipt.destination == destination.path,
           let stamp = try validArchive(receipt,destination:destination) {
            try unchanged(inputs,job:job); try sameDestination(destination,stamp:folderStamp); try Task.checkCancellation()
            // A pending intent may have survived rename without the directory fsync.
            if receipt.archiveStamp == nil { try BrokerFiles.syncDirectory(destination) }
            receipt.inputs = inputs; receipt.archiveStamp = stamp
            try save(receipt,journal:journal,current:current)
            return (destination.appendingPathComponent(receipt.archiveName),relocated != nil)
        }

        let fm = FileManager.default
        let scratch = fm.urls(for:.cachesDirectory,in:.userDomainMask)[0].appendingPathComponent("Vemo-ZIP-" + UUID().uuidString)
        let payload = scratch.appendingPathComponent("payload")
        let temporary = destination.appendingPathComponent(".vemo-zip-" + UUID().uuidString + ".part")
        defer { try? fm.removeItem(at:scratch); try? fm.removeItem(at:temporary) }
        try BrokerFiles.directory(payload)
        await progress("녹음 복사 중")
        try unchanged(inputs,job:job)
        let stagedAudio = payload.appendingPathComponent(inputs.audioName)
        _ = try BrokerFiles.stableCopy(URL(fileURLWithPath:inputs.audioPath),to:stagedAudio)
        guard try BrokerFiles.hash(stagedAudio) == audioHash else { throw BrokerFiles.fail("changing_file") }
        try BrokerFiles.writeNew(Data(try TranscriptExport.render(transcript,title:job.title,format:"txt").utf8),to:payload.appendingPathComponent("transcript.txt"))
        try BrokerFiles.writeNew(Data(NotesDisplay.markdown(notes,title:job.title).utf8),to:payload.appendingPathComponent("notes.md"))
        await progress("ZIP 압축 중 · 1개 녹음")
        try sameDestination(destination,stamp:folderStamp)
        try execute("/usr/bin/ditto",["-c","-k","--norsrc","--noextattr","--noqtn",payload.path,temporary.path],log:scratch.appendingPathComponent("archive.log"))
        await progress("ZIP 무결성 확인 중")
        try execute("/usr/bin/unzip",["-tqq",temporary.path],log:scratch.appendingPathComponent("verify.log"))
        let archiveHash = try BrokerFiles.hash(temporary)
        let fd = try BrokerFiles.readFD(temporary)
        defer { close(fd) }
        guard fsync(fd) == 0 else { throw BrokerFiles.fail() }
        try unchanged(inputs,job:job); try sameDestination(destination,stamp:folderStamp); try Task.checkCancellation()
        let base = name(job.title,recordingDate:job.recording_date)
        var ordinal = 1
        while true {
            try Task.checkCancellation()
            let archiveName = base + (ordinal == 1 ? "" : " (\(ordinal))") + ".zip"
            let target = destination.appendingPathComponent(archiveName)
            // Inspect existence without following a foreign file or symlink.
            var s = stat()
            if lstat(target.path,&s) == 0 { ordinal += 1; continue }
            guard errno == ENOENT else { throw BrokerFiles.fail() }
            var receipt = Receipt(schemaVersion:1,destination:destination.path,jobID:job.id,fingerprint:fingerprint,
                audioHash:audioHash,configuredRoot:configuredRoot.path,inputs:inputs,archiveName:archiveName,archiveHash:archiveHash,archiveStamp:nil)
            // This durable intent precedes rename. On crash, only these exact ZIP bytes
            // can be adopted; an arbitrary matching cloud filename is never evidence.
            try save(receipt,journal:journal,current:nil)
            await progress("ZIP 저장 직전")
            try unchanged(inputs,job:job)
            try sameDestination(destination,stamp:folderStamp); try Task.checkCancellation()
            if renamex_np(temporary.path,target.path,UInt32(RENAME_EXCL)) != 0 {
                guard errno == EEXIST else { throw BrokerFiles.fail() }
                ordinal += 1; continue
            }
            // After publication, finish the receipt even if cancellation arrives.
            try BrokerFiles.syncDirectory(destination)
            let publishedStamp = try Stamp(target)
            guard try BrokerFiles.hash(target) == archiveHash, publishedStamp == (try Stamp(target)) else {
                throw BrokerFiles.fail("zip_archive_changed")
            }
            receipt.archiveStamp = publishedStamp
            try save(receipt,journal:journal,current:current)
            return (target,true)
        }
    }
}
