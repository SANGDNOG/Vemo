import Foundation
import Darwin
import CSQLite

public struct SourceSchemaDiagnostic: Codable, Sendable {
    public struct Column: Codable, Sendable { public let name: String; public let type: String; public let not_null: Bool; public let primary_key: Bool }
    public struct Table: Codable, Sendable { public let name: String; public let columns: [Column] }
    public var schema_version = 1
    public let checked_at: String
    public let status: String
    public let schema_verified: Bool
    public let tables: [Table]
    public let model_hashes: [String:String]
    public let message: String
}
public struct SourceRecording: Sendable, Equatable {
    public let uuid: String; public let sourceURL: URL?; public let recordedAt: String?; public let recordedAtSource: String
    public let originalTimezone: String?; public let duration: Double?; public let title: String?; public let completionEvidence: String
    public let metadataRevision: String?
    public init(uuid: String, sourceURL: URL?, recordedAt: String?, recordedAtSource: String, originalTimezone: String?, duration: Double?, title: String?, completionEvidence: String, metadataRevision: String? = nil) {
        self.uuid = uuid; self.sourceURL = sourceURL; self.recordedAt = recordedAt; self.recordedAtSource = recordedAtSource
        self.originalTimezone = originalTimezone; self.duration = duration; self.title = title; self.completionEvidence = completionEvidence
        self.metadataRevision = metadataRevision
    }
}
public protocol VerifiedSourceAdapter: Sendable {
    // Implementations require corroborated schema/model/date/finish semantics. A guessed OS layout is not sufficient.
    func accepts(_ diagnostic: SourceSchemaDiagnostic) -> Bool
    func recordings(databaseSnapshot: URL) throws -> [SourceRecording]
}
// Unknown schemas remain unsupported. Source adapters only read owned DB/WAL copies.
public actor SourceBroker {
    private let stateRoot: URL
    private let database: URL
    private let adapter: (any VerifiedSourceAdapter)?
    private struct SourceEntry: Codable { let uuid: String; let reference: String }
    private struct SourceRegistry: Codable { var version = 1; var entries: [String:SourceEntry] = [:] }
    public init(root: URL, databaseURL: URL? = nil, adapter: (any VerifiedSourceAdapter)? = nil) {
        stateRoot = root; self.adapter = adapter
        database = databaseURL ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Group Containers/group.com.apple.VoiceMemos.shared/Recordings/CloudRecordings.db")
    }
    public func diagnostic() -> SourceSchemaDiagnostic { inspect().0 }
    private func inspect() -> (SourceSchemaDiagnostic, [SourceRecording]) {
        let now = ISO8601DateFormatter().string(from:Date())
        var db: OpaquePointer?
        var scratch: URL?
        defer { if let scratch { try? FileManager.default.removeItem(at:scratch) } }
        do {
            // Never let SQLite create/modify an Apple shm/WAL sidecar. Snapshot all SQLite input with read-only FDs.
            let dir = stateRoot.appendingPathComponent("native/schema-snapshots/" + UUID().uuidString); try BrokerFiles.directory(dir); scratch = dir
            let wal = URL(fileURLWithPath:database.path + "-wal")
            let before = try BrokerFiles.stamp(database)
            let walBefore = FileManager.default.fileExists(atPath:wal.path) ? try BrokerFiles.stamp(wal) : nil
            let copy = dir.appendingPathComponent("CloudRecordings.db")
            _ = try BrokerFiles.stableCopy(database,to:copy)
            if let walBefore, walBefore.size > 0 { _ = try BrokerFiles.stableCopy(wal,to:URL(fileURLWithPath:copy.path + "-wal")) }
            let walAfter = FileManager.default.fileExists(atPath:wal.path) ? try BrokerFiles.stamp(wal) : nil
            guard before == (try BrokerFiles.stamp(database)), walBefore == walAfter else { throw BrokerFailure("source_changing", "음성 메모가 동기화 중입니다. 잠시 후 다시 확인하세요.") }
            guard sqlite3_open_v2(copy.path,&db,SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,nil) == SQLITE_OK else { throw BrokerFailure("source_access", "음성 메모 DB를 읽기 전용으로 열지 못했습니다.") }
            defer { sqlite3_close(db); db = nil }
            guard sqlite3_db_readonly(db,"main") == 1, sqlite3_exec(db,"PRAGMA query_only=ON; BEGIN",nil,nil,nil) == SQLITE_OK else { throw BrokerFailure("source_access", "읽기 전용 진단을 시작하지 못했습니다.") }
            defer { sqlite3_exec(db,"ROLLBACK",nil,nil,nil) }
            sqlite3_busy_timeout(db,1000)
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(db,"SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",-1,&statement,nil) == SQLITE_OK else { throw BrokerFailure("schema_read", "DB 스키마를 읽지 못했습니다.") }
            var names: [String] = []
            var code = sqlite3_step(statement)
            while code == SQLITE_ROW {
                if let name = sqlite3_column_text(statement,0) { names.append(String(cString:name)) }
                guard names.count <= 256 else { sqlite3_finalize(statement); throw BrokerFailure("unknown_schema", "지원하지 않는 DB 스키마입니다.") }; code = sqlite3_step(statement)
            }
            sqlite3_finalize(statement); guard code == SQLITE_DONE else { throw BrokerFailure("schema_read","스키마 진단이 중단되었습니다.") }
            var tables: [SourceSchemaDiagnostic.Table] = []
            for name in names {
                let quoted = name.replacingOccurrences(of:"\"",with:"\"\"")
                guard sqlite3_prepare_v2(db,"PRAGMA table_info(\"\(quoted)\")",-1,&statement,nil) == SQLITE_OK else { throw BrokerFailure("schema_read", "DB 컬럼을 읽지 못했습니다.") }
                var columns: [SourceSchemaDiagnostic.Column] = []; code = sqlite3_step(statement)
                while code == SQLITE_ROW {
                    if let n = sqlite3_column_text(statement,1), let t = sqlite3_column_text(statement,2) { columns.append(.init(name:String(cString:n),type:String(cString:t),not_null:sqlite3_column_int(statement,3) != 0,primary_key:sqlite3_column_int(statement,5) != 0)) }
                    code = sqlite3_step(statement)
                }
                sqlite3_finalize(statement); guard code == SQLITE_DONE else { throw BrokerFailure("schema_read","컬럼 진단이 중단되었습니다.") }; tables.append(.init(name:name,columns:columns))
            }
            var hashes: [String:String] = [:]
            if names.contains("Z_METADATA"), sqlite3_prepare_v2(db,"SELECT Z_PLIST FROM Z_METADATA LIMIT 1",-1,&statement,nil) == SQLITE_OK {
                if sqlite3_step(statement) == SQLITE_ROW, let bytes = sqlite3_column_blob(statement,0) {
                    let count = Int(sqlite3_column_bytes(statement,0))
                    if count <= WireCodec.limit, let plist = try? PropertyListSerialization.propertyList(from:Data(bytes:bytes,count:count),format:nil) as? [String:Any], let model = plist["NSStoreModelVersionHashes"] as? [String:Data] { hashes = model.mapValues { $0.base64EncodedString() } }
                }
                sqlite3_finalize(statement)
            }
            let diagnostic = SourceSchemaDiagnostic(checked_at:now,status:"unsupported_schema",schema_verified:false,tables:tables,model_hashes:hashes,message:"DB를 읽기 전용으로 진단했습니다. 녹음 시작일·완료 상태 의미가 검증된 어댑터가 없어 직접 수집을 중지합니다. 공유 파일 폴더 또는 파일 가져오기를 사용하세요.")
            if let adapter, adapter.accepts(diagnostic) {
                let recordings = try adapter.recordings(databaseSnapshot:copy)
                return (.init(checked_at:now,status:"supported_schema",schema_verified:true,tables:tables,model_hashes:hashes,message:"검증된 읽기 전용 어댑터로 음성 메모를 확인했습니다."),recordings)
            }
            return (diagnostic,[])
        } catch {
            if db != nil { sqlite3_close(db) }
            let changing = (error as? BrokerFailure)?.code == "source_changing"
            return (.init(checked_at:now,status:changing ? "source_changing" : "source_access_failed",schema_verified:false,tables:[],model_hashes:[:],message:changing ? "음성 메모가 동기화 중입니다. 잠시 후 다시 확인하세요." : "음성 메모 저장소를 읽지 못했습니다. 앱의 전체 디스크 접근 권한과 Mac 음성 메모 동기화를 확인한 뒤 다시 진단하세요."),[])
        }
    }
    public func writeDiagnostic() async throws -> SourceSchemaDiagnostic {
        let result = diagnostic(); let folder = stateRoot.appendingPathComponent("native"); try BrokerFiles.directory(folder)
        try BrokerFiles.atomic(BrokerFiles.json(result),to:folder.appendingPathComponent("source-schema-diagnostic.json")); return result
    }
    private var registryURL: URL { stateRoot.appendingPathComponent("native/sources.json") }
    private func registry() throws -> SourceRegistry {
        guard FileManager.default.fileExists(atPath:registryURL.path) else { return SourceRegistry() }
        let r = try JSONDecoder().decode(SourceRegistry.self,from:BrokerFiles.read(registryURL))
        guard r.version == 1 else { throw BrokerFailure("unknown_registry","지원하지 않는 원본 기록 버전입니다.") }; return r
    }
    private func sourceURL(_ url: URL) throws -> URL {
        let source = try Validation.owned(url.path,root:database.deletingLastPathComponent()); try BrokerFiles.noLinks(source); return source
    }
    public func scan() async throws -> [Memo] {
        let (diagnostic, recordings) = inspect()
        let native = stateRoot.appendingPathComponent("native"); try BrokerFiles.directory(native)
        try BrokerFiles.atomic(BrokerFiles.json(diagnostic),to:native.appendingPathComponent("source-schema-diagnostic.json"))
        guard diagnostic.schema_verified else { throw BrokerFailure(diagnostic.status,diagnostic.message) }
        let lock = try BrokerFiles.lock(native.appendingPathComponent("sources.lock")); defer { close(lock) }
        var registry = try registry(); var memos: [Memo] = []
        guard Set(recordings.map(\.uuid)).count == recordings.count else { throw BrokerFailure("source_metadata","원본 식별자가 중복되어 수집을 중지했습니다.") }
        for recording in recordings {
            _ = try Validation.component(recording.uuid)
            guard ["finished","active","unknown"].contains(recording.completionEvidence) else { throw BrokerFailure("source_metadata","녹음 완료 상태가 유효하지 않습니다.") }
            if registry.entries[recording.uuid] == nil { registry.entries[recording.uuid] = SourceEntry(uuid:recording.uuid,reference:"source:" + UUID().uuidString) }
            let reference = registry.entries[recording.uuid]!.reference
            var revision: MediaRevision?
            if let url = recording.sourceURL { revision = try? BrokerFiles.stamp(sourceURL(url)).revision }
            let evidence = revision == nil ? "unknown" : recording.completionEvidence
            memos.append(Memo(uuid:recording.uuid,recorded_at:recording.recordedAt,recorded_at_source:recording.recordedAtSource,original_timezone:recording.originalTimezone,duration_s:recording.duration,media_revision:revision,completion_evidence:evidence,source_kind:"voice_memos",source_reference:reference,original_title:recording.title,metadata_revision:recording.metadataRevision))
        }
        try BrokerFiles.atomic(BrokerFiles.json(registry),to:registryURL); return memos
    }
    public func snapshot(reference: String, jobID: String) async throws -> OwnedSnapshot {
        let (diagnostic, recordings) = inspect()
        guard diagnostic.schema_verified else { throw BrokerFailure(diagnostic.status,diagnostic.message) }
        guard let entry = try registry().entries.values.first(where:{$0.reference == reference}), let recording = recordings.first(where:{$0.uuid == entry.uuid}) else { throw BrokerFailure("unknown_source","원본 녹음이 없거나 삭제되었습니다.") }
        guard recording.completionEvidence == "finished", let url = recording.sourceURL else { throw BrokerFailure("source_incomplete","녹음 완료와 오디오 동기화를 기다립니다.") }
        let original = try sourceURL(url)
        let folder = stateRoot.appendingPathComponent("snapshots/jobs/" + (try Validation.component(jobID))); try BrokerFiles.directory(folder)
        let target = folder.appendingPathComponent(UUID().uuidString + "." + (try Validation.component(original.pathExtension)))
        let before = try BrokerFiles.stamp(original)
        let snapshot = try BrokerFiles.stableCopy(original,to:target)
        do {
            let decodedDuration = try BrokerFiles.decodeAudio(target)
            if let expected = recording.duration, abs(expected - decodedDuration) > 0.25 { throw BrokerFailure("source_incomplete","녹음 길이와 다운로드한 오디오가 다릅니다. 동기화를 기다립니다.") }
            let (after, current) = inspect()
            guard after.schema_verified, let latest = current.first(where:{$0.uuid == entry.uuid}), latest == recording, latest.completionEvidence == "finished", before == (try BrokerFiles.stamp(original)) else { throw BrokerFailure("source_changing","복사 중 원본 상태가 변경되었습니다.") }
            return snapshot
        } catch { try? FileManager.default.removeItem(at:target); throw error }
    }
}
