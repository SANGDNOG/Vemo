import Foundation
import Darwin

public actor ImportBroker {
    private struct Entry: Codable { var memo: Memo; let path: String; let hash: String; var delivered: Bool; var purged: Bool? = nil }
    private struct Registry: Codable { var schema_version = 1; var entries: [String: Entry] = [:] }
    private let root: URL
    public init(root: URL) { self.root = root.standardizedFileURL }
    private var native: URL { root.appendingPathComponent("native") }
    private var registryURL: URL { native.appendingPathComponent("imports.json") }
    private func load() throws -> Registry {
        guard FileManager.default.fileExists(atPath: registryURL.path) else { return Registry() }
        let r = try JSONDecoder().decode(Registry.self, from: BrokerFiles.read(registryURL))
        guard r.schema_version == 1 else { throw BrokerFailure("unknown_registry", "가져오기 기록의 버전을 지원하지 않습니다.") }; return r
    }
    public func importFile(_ url: URL) async throws -> Memo {
        try BrokerFiles.directory(native)
        let lock = try BrokerFiles.lock(native.appendingPathComponent("imports.lock")); defer { close(lock) }
        var registry = try load()
        let copies = root.appendingPathComponent("snapshots/imports"); try BrokerFiles.directory(copies)
        let ext = url.pathExtension.lowercased()
        guard ["wav", "m4a", "mp3", "aiff", "aif", "caf", "aac", "flac", "mp4", "qta"].contains(ext) else { throw BrokerFailure("unsupported_media", "지원되는 오디오 파일을 선택하세요.") }
        let copy = copies.appendingPathComponent("\(UUID().uuidString).\(ext)")
        _ = try BrokerFiles.stableCopy(url, to: copy)
        var retained = false; defer { if !retained { try? FileManager.default.removeItem(at: copy) } }
        let duration = try BrokerFiles.decodeAudio(copy); let hash = try BrokerFiles.hash(copy)
        let matching = registry.entries.values.filter { $0.hash == hash }
        guard !matching.contains(where: { $0.purged == true }) else { throw purgedFailure() }
        if let existing = matching.first {
            let owned = try Validation.owned(existing.path, root: root)
            guard try BrokerFiles.hash(owned) == hash else { throw BrokerFailure("import_conflict", "보관한 가져오기 파일이 변경되었습니다. 원본과 작업 파일을 확인하세요.") }
            return existing.memo
        }
        let uuid = UUID().uuidString; let reference = "import:" + UUID().uuidString
        let memo = Memo(uuid: uuid, recorded_at: nil, recorded_at_source: "unknown", original_timezone: nil, duration_s: duration, media_revision: try BrokerFiles.stamp(copy).revision, completion_evidence: "finished", source_kind: "import", source_reference: reference, original_title: url.deletingPathExtension().lastPathComponent)
        registry.entries[reference] = Entry(memo: memo, path: copy.path, hash: hash, delivered: false)
        try BrokerFiles.atomic(BrokerFiles.json(registry), to: registryURL); retained = true
        return memo
    }
    private func purgedFailure() -> BrokerFailure { BrokerFailure("import_purged", "영구 삭제한 가져오기 녹음입니다. 같은 오디오를 다시 가져올 수 없습니다.") }
    public func pendingImports() throws -> [Memo] { try load().entries.values.filter { !$0.delivered && $0.purged != true }.map(\.memo) }
    public func markDelivered(reference: String) throws {
        try BrokerFiles.directory(native); let lock = try BrokerFiles.lock(native.appendingPathComponent("imports.lock")); defer { close(lock) }
        var registry = try load(); guard registry.entries[reference] != nil else { return }
        registry.entries[reference]?.delivered = true; try BrokerFiles.atomic(BrokerFiles.json(registry), to: registryURL)
    }
    public func snapshot(reference: String, jobID: String) async throws -> OwnedSnapshot {
        // Backend purge holds this same lock while marking/removing the owned cache.
        try BrokerFiles.directory(native)
        let lock = try BrokerFiles.lock(native.appendingPathComponent("imports.lock")); defer { close(lock) }
        guard let entry = try load().entries[reference] else { throw BrokerFailure("unknown_source", "가져오기 원본 기록을 찾지 못했습니다.") }
        guard entry.purged != true else { throw purgedFailure() }
        let source = try Validation.owned(entry.path, root: root)
        guard try BrokerFiles.hash(source) == entry.hash else { throw BrokerFailure("import_conflict", "보관된 오디오가 변경되었습니다.") }
        let dir = root.appendingPathComponent("snapshots/jobs/" + (try Validation.component(jobID))); try BrokerFiles.directory(dir)
        let copy = dir.appendingPathComponent(UUID().uuidString + "." + source.pathExtension)
        return try BrokerFiles.stableCopy(source, to: copy)
    }
}
