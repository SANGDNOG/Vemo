import Foundation
import Darwin
import CryptoKit

public actor PublishBroker {
    private let stateRoot: URL
    public init(stateRoot: URL) { self.stateRoot = stateRoot.standardizedFileURL }
    private func conflict() -> BrokerFailure { BrokerFailure("publication_conflict", "결과 파일 또는 manifest가 변경되었습니다. 사용자 파일과 로컬 결과를 보존하고 게시를 중지했습니다.") }
    private func readManifest(_ url: URL) throws -> PublicationManifest {
        let m = try JSONDecoder().decode(PublicationManifest.self, from: BrokerFiles.read(url))
        guard m.schema_version == 1, !m.files.isEmpty, m.files.count <= 16 else { throw conflict() }
        _ = try Validation.recordingDate(m.recording_date)
        for c in [m.publication_id, m.job_id, m.uuid] { _ = try Validation.component(c) }
        guard Set(m.files.map(\.name)).count == m.files.count else { throw conflict() }
        for f in m.files {
            _ = try Validation.component(f.name)
            guard f.name != "manifest.json", !f.name.hasPrefix("."), ["txt", "md", "srt", "vtt", "json"].contains(URL(fileURLWithPath:f.name).pathExtension),
                  f.sha256.range(of: "^[a-f0-9]{64}$", options:.regularExpression) != nil else { throw conflict() }
        }
        return m
    }
    private func equivalent(_ a: PublicationManifest, _ b: PublicationManifest) -> Bool {
        a.schema_version == b.schema_version && a.publication_id == b.publication_id && a.job_id == b.job_id && a.uuid == b.uuid && a.recording_date == b.recording_date && a.title == b.title && a.revision == b.revision && a.files.map(\.name) == b.files.map(\.name) && a.files.map(\.sha256) == b.files.map(\.sha256)
    }
    private func verify(_ m: PublicationManifest, in version: URL) throws {
        try BrokerFiles.noLinks(version)
        for f in m.files {
            let expected = version.appendingPathComponent(f.name)
            guard URL(fileURLWithPath:f.path).standardizedFileURL.path == expected.path, try BrokerFiles.hash(expected) == f.sha256 else { throw conflict() }
        }
        let names = Set(try FileManager.default.contentsOfDirectory(atPath:version.path))
        guard names == Set(m.files.map(\.name) + ["manifest.json"]) else { throw conflict() }
    }
    private func location(uuid: String, day: String, output: URL) throws -> URL {
        _ = try Validation.component(uuid); _ = try Validation.recordingDate(day)
        return output.standardizedFileURL.appendingPathComponent(String(day.prefix(4))).appendingPathComponent(String(day.dropFirst(5).prefix(2))).appendingPathComponent(uuid)
    }
    private func receipt(_ m: PublicationManifest, at path: URL) -> PublicationReceipt { PublicationReceipt(publication_id:m.publication_id, output_manifest_path:path.path, artifact_hashes:Dictionary(uniqueKeysWithValues:m.files.map { ($0.name,$0.sha256) })) }
    private struct Activation: Codable { let output_root: String; let uuid: String; let publication_id: String; let prior: Data?; var activated: Bool }
    public func publish(manifestPath: String, jobID: String, publicationID: String, outputRoot: URL) async throws -> PublicationReceipt {
        let sourceURL = try Validation.owned(manifestPath, root:stateRoot.appendingPathComponent("artifacts"))
        let source = try readManifest(sourceURL)
        guard source.job_id == jobID, source.publication_id == publicationID else { throw BrokerFailure("stale_publication", "게시 요청과 결과 manifest가 일치하지 않습니다.") }
        for f in source.files {
            let file = try Validation.owned(f.path, root:stateRoot.appendingPathComponent("artifacts"))
            guard try BrokerFiles.hash(file) == f.sha256 else { throw BrokerFailure("local_artifact_conflict", "로컬 결과 hash가 일치하지 않습니다. 로컬 파일을 보존했습니다.") }
        }
        try BrokerFiles.directory(outputRoot)
        let lock = try BrokerFiles.lock(outputRoot.appendingPathComponent(".native-publish.lock")); defer { close(lock) }
        let folder = try location(uuid:source.uuid, day:source.recording_date, output:outputRoot)
        let versions = folder.appendingPathComponent("versions"), stageRoot = folder.appendingPathComponent(".staging")
        try BrokerFiles.directory(versions); try BrokerFiles.directory(stageRoot)
        let active = folder.appendingPathComponent("manifest.json"), version = versions.appendingPathComponent(publicationID), staging = stageRoot.appendingPathComponent(publicationID)
        var target = source
        target.files = source.files.map { PublicationManifest.File(name:$0.name, path:version.appendingPathComponent($0.name).path, sha256:$0.sha256) }
        let targetData = try BrokerFiles.json(target)
        let activeData = FileManager.default.fileExists(atPath:active.path) ? try BrokerFiles.read(active) : nil
        if let data = activeData {
            let existing = try JSONDecoder().decode(PublicationManifest.self, from:data)
            guard existing.uuid == source.uuid, existing.recording_date == source.recording_date else { throw conflict() }
            let currentVersion = versions.appendingPathComponent(try Validation.component(existing.publication_id))
            let immutable = try readManifest(currentVersion.appendingPathComponent("manifest.json"))
            guard equivalent(existing,immutable), existing.files.map(\.path) == immutable.files.map(\.path) else { throw conflict() }
            try verify(existing,in:currentVersion)
            if existing.publication_id == publicationID {
                guard equivalent(existing,target) else { throw conflict() }
                return receipt(existing,at:currentVersion.appendingPathComponent("manifest.json"))
            }
        }
        let journalDir = stateRoot.appendingPathComponent("native/publications"); try BrokerFiles.directory(journalDir)
        let key = try BrokerFiles.json([outputRoot.standardizedFileURL.path,source.uuid,publicationID])
        let journalHash = SHA256.hash(data:key).map { String(format:"%02x",$0) }.joined()
        let journal = journalDir.appendingPathComponent(journalHash + ".json")
        var activation: Activation
        if FileManager.default.fileExists(atPath:journal.path) {
            activation = try JSONDecoder().decode(Activation.self,from:BrokerFiles.read(journal))
            guard activation.output_root == outputRoot.standardizedFileURL.path, activation.uuid == source.uuid, activation.publication_id == publicationID else { throw conflict() }
        } else {
            // A revision directory alone is not activation evidence. Exact active replay was verified above.
            guard !FileManager.default.fileExists(atPath:version.path) else { throw conflict() }
            activation = Activation(output_root:outputRoot.standardizedFileURL.path,uuid:source.uuid,publication_id:publicationID,prior:activeData,activated:false)
            try BrokerFiles.atomic(BrokerFiles.json(activation),to:journal)
        }
        if FileManager.default.fileExists(atPath:version.path) {
            let m = try readManifest(version.appendingPathComponent("manifest.json")); guard equivalent(m,target), m.files.map(\.path) == target.files.map(\.path) else { throw conflict() }; try verify(m,in:version)
            if activation.activated { return receipt(m,at:version.appendingPathComponent("manifest.json")) }
        } else {
            try BrokerFiles.directory(staging)
            let stagingManifest = staging.appendingPathComponent("manifest.json")
            if FileManager.default.fileExists(atPath:stagingManifest.path) {
                let m = try readManifest(stagingManifest); guard equivalent(m,target), m.files.map(\.path) == target.files.map(\.path) else { throw conflict() }
            } else {
                guard try FileManager.default.contentsOfDirectory(atPath:staging.path).isEmpty else { throw conflict() }
                let temp = stageRoot.appendingPathComponent(".manifest-" + UUID().uuidString + ".tmp")
                defer { try? FileManager.default.removeItem(at:temp) }
                try BrokerFiles.writeNew(targetData,to:temp)
                guard renamex_np(temp.path,stagingManifest.path,UInt32(RENAME_EXCL)) == 0 else { throw conflict() }; try BrokerFiles.syncDirectory(staging)
            }
            let allowed = Set(target.files.map(\.name) + ["manifest.json"])
            guard Set(try FileManager.default.contentsOfDirectory(atPath:staging.path)).isSubset(of:allowed) else { throw conflict() }
            for (input,f) in zip(source.files,target.files) {
                let destination = staging.appendingPathComponent(f.name)
                if FileManager.default.fileExists(atPath:destination.path) {
                    guard try BrokerFiles.hash(destination) == f.sha256 else { throw conflict() }
                } else {
                    // Write to a temporary owned staging file. A torn copy is never an artifact.
                    let temp = stageRoot.appendingPathComponent(".copy-" + UUID().uuidString)
                    defer { try? FileManager.default.removeItem(at:temp) }
                    _ = try BrokerFiles.stableCopy(URL(fileURLWithPath:input.path),to:temp)
                    guard try BrokerFiles.hash(temp) == f.sha256 else { throw conflict() }
                    guard renamex_np(temp.path,destination.path,UInt32(RENAME_EXCL)) == 0 else { throw conflict() }; try BrokerFiles.syncDirectory(staging)
                }
            }
            // Paths in the manifest name the final immutable location, so validate staging bytes separately.
            for f in target.files { guard try BrokerFiles.hash(staging.appendingPathComponent(f.name)) == f.sha256 else { throw conflict() } }
            guard renamex_np(staging.path,version.path,UInt32(RENAME_EXCL)) == 0 else { throw conflict() }; try BrokerFiles.syncDirectory(versions); try BrokerFiles.syncDirectory(stageRoot)
            try verify(target,in:version)
        }
        guard activation.prior == activeData else { throw conflict() }
        // Cooperating writers are locked. Recheck externally editable active bytes immediately before rename.
        let current = FileManager.default.fileExists(atPath:active.path) ? try BrokerFiles.read(active) : nil
        guard current == activeData else { throw conflict() }
        try BrokerFiles.atomic(targetData,to:active)
        activation.activated = true; try BrokerFiles.atomic(BrokerFiles.json(activation),to:journal)
        try verify(target,in:version)
        guard try BrokerFiles.read(active) == targetData else { throw conflict() }
        return receipt(target,at:version.appendingPathComponent("manifest.json"))
    }
    public func activeRevision(uuid: String, recordingDate: String, outputRoot: URL) throws -> URL {
        let folder = try location(uuid:uuid,day:recordingDate,output:outputRoot)
        let m = try readManifest(folder.appendingPathComponent("manifest.json"))
        guard m.uuid == uuid, m.recording_date == recordingDate else { throw conflict() }
        let version = folder.appendingPathComponent("versions/" + m.publication_id)
        let immutable = try readManifest(version.appendingPathComponent("manifest.json"))
        guard equivalent(m,immutable), m.files.map(\.path) == immutable.files.map(\.path) else { throw conflict() }; try verify(m,in:version); return version
    }
}
