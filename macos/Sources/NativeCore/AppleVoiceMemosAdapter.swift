import Foundation
import AVFoundation
import CSQLite

public struct AppleVoiceMemosAdapter: VerifiedSourceAdapter {
    private let recordingsRoot: URL
    private let editingRisk: @Sendable () -> Bool
    public init(recordingsRoot: URL, editingRisk: @escaping @Sendable () -> Bool) {
        self.recordingsRoot = recordingsRoot; self.editingRisk = editingRisk
    }
    // Observed macOS 27.0.1 store. This hash differs from the shipped .mom/.omo;
    // acceptance pins the actual store, not an assumed framework-model match.
    private static let hashes = ["CloudRecording":"wzISBP+96pkUsBpdE2V3vJH08CnDBpBi8U/vSlVVosQ=","DatabaseProperty":"DdeyItMrmgzYUVyA8NUAc8cS1Sr4LwwTo+KneZZrPBI=","EntityRevision":"MCYSLwlQNrzkytEuk0Paa2vv7h+rbxnn3DWtIuHpxa0=","Folder":"BTuJxZB4F1ci2UMqAPo0Fx2Oif08rXM0z+/UQoivHc0=","Migration":"C9+RC8Owb0OTnIogkfdqVeaZV1hChUC6VuqvEwC0DUU=","Recording":"l+6Nf+h4pgpvs9n/EqyKB5n5y0F2UwNh6/6d/evM+L8="]
    private static let columns: [String:String] = [
        "Z_PK":"INTEGER","Z_ENT":"INTEGER","Z_OPT":"INTEGER","ZAUDIOFUTUREFLAGS":"INTEGER","ZFLAGS":"INTEGER","ZSHAREDFLAGS":"INTEGER","ZSILENCEREMOVERENABLED":"INTEGER","ZSKIPSILENCEENABLED":"INTEGER","ZSTUDIOMIXENABLED":"INTEGER","ZFOLDER":"INTEGER",
        "ZDATE":"TIMESTAMP","ZDURATION":"FLOAT","ZEVICTIONDATE":"TIMESTAMP","ZLOCALDURATION":"FLOAT","ZMTLAYERMIX":"FLOAT","ZPLAYBACKPOSITION":"FLOAT","ZPLAYBACKRATE":"FLOAT","ZPLAYBACKSPEED":"FLOAT","ZSTUDIOMIXLEVEL":"FLOAT",
        "ZCUSTOMLABEL":"VARCHAR","ZCUSTOMLABELFORSORTING":"VARCHAR","ZENCRYPTEDTITLE":"VARCHAR","ZPATH":"VARCHAR","ZUNIQUEID":"VARCHAR","ZAUDIOFUTUREUUIDS":"BLOB","ZAUDIODIGEST":"BLOB","ZAUDIOFUTURE":"BLOB","ZMTAUDIOFUTURE":"BLOB","ZVERSIONEDAUDIOFUTURE":"BLOB"
    ]
    public func accepts(_ diagnostic: SourceSchemaDiagnostic) -> Bool {
        guard diagnostic.model_hashes == Self.hashes,
              let table = diagnostic.tables.first(where: { $0.name == "ZCLOUDRECORDING" }),
              table.columns.count == Self.columns.count,
              table.columns.allSatisfy({ Self.columns[$0.name] == $0.type && !$0.not_null && $0.primary_key == ($0.name == "Z_PK") }) else { return false }
        return true
    }
    public func recordings(databaseSnapshot: URL) throws -> [SourceRecording] {
        var db: OpaquePointer?
        guard sqlite3_open_v2(databaseSnapshot.path,&db,SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,nil) == SQLITE_OK else {
            sqlite3_close(db); throw BrokerFailure("source_access","음성 메모 스냅샷을 읽지 못했습니다.")
        }
        defer { sqlite3_close(db) }
        guard sqlite3_db_readonly(db,"main") == 1, sqlite3_exec(db,"PRAGMA query_only=ON",nil,nil,nil) == SQLITE_OK else { throw BrokerFailure("source_access","읽기 전용 상태를 확인하지 못했습니다.") }
        var query: OpaquePointer?
        // RCCloudRecording.creationDate aliases date; deletionDate aliases
        // evictionDate; fileName uses the path key. Core Data NSDate uses 2001.
        // title -> localTitle -> customLabelForSorting in the installed
        // framework. customLabel can retain an old date label after migration.
        let sql = "SELECT ZUNIQUEID,ZPATH,ZDATE,ZDURATION,ZLOCALDURATION,ZCUSTOMLABEL,ZENCRYPTEDTITLE,ZFLAGS,Z_OPT,ZCUSTOMLABELFORSORTING FROM ZCLOUDRECORDING WHERE ZEVICTIONDATE IS NULL ORDER BY Z_PK"
        guard sqlite3_prepare_v2(db,sql,-1,&query,nil) == SQLITE_OK else { throw BrokerFailure("source_metadata","음성 메모 목록을 읽지 못했습니다.") }
        defer { sqlite3_finalize(query) }
        func string(_ index: Int32) -> String? { guard sqlite3_column_type(query,index) == SQLITE_TEXT, let text = sqlite3_column_text(query,index) else { return nil }; return String(cString:text) }
        func number(_ index: Int32) -> Double? {
            guard [SQLITE_INTEGER,SQLITE_FLOAT].contains(sqlite3_column_type(query,index)) else { return nil }
            let value = sqlite3_column_double(query,index); return value.isFinite ? value : nil
        }
        let editing = editingRisk()
        var result: [SourceRecording] = []
        var code = sqlite3_step(query)
        while code == SQLITE_ROW {
            guard result.count < 50_000, let uuid = string(0), uuid.utf8.count <= 256, (try? Validation.component(uuid)) != nil else { throw BrokerFailure("source_metadata","음성 메모 식별자를 확인하지 못했습니다.") }
            let path = string(1)
            var url: URL?
            // Apple fileName is a relative filename. Reject packages, traversal,
            // absolute paths and links; unsupported media remains waiting.
            if let path, (try? Validation.component(path)) != nil,
               ["m4a","wav","mp3","aif","aiff","caf","qta"].contains(URL(fileURLWithPath:path).pathExtension.lowercased()) {
                let candidate = recordingsRoot.appendingPathComponent(path)
                if (try? Validation.owned(candidate.path,root:recordingsRoot)) != nil, (try? BrokerFiles.stamp(candidate)) != nil { url = candidate }
            }
            let cloudDuration = number(3), localDuration = number(4)
            var duration = (localDuration ?? cloudDuration).flatMap { $0 > 0 ? $0 : nil }
            let date = number(2).flatMap { seconds -> String? in
                guard abs(seconds) < 63_000_000_000 else { return nil }
                return ISO8601DateFormatter().string(from:Date(timeIntervalSinceReferenceDate:seconds))
            }
            let flagsKnown = sqlite3_column_type(query,7) == SQLITE_INTEGER
            let flags = sqlite3_column_int64(query,7)
            let revisionKnown = sqlite3_column_type(query,8) == SQLITE_INTEGER
            // Installed getters corroborate playable=4, needsDownload=2,
            // needsExport=256. Playable alone does not identify an idle editor.
            var lengthsAgree = cloudDuration != nil && localDuration != nil && cloudDuration! > 0 && localDuration! > 0 && abs(cloudDuration! - localDuration!) <= 0.25
            // Current spatial recordings are plain QuickTime .qta files. Their
            // older scalar duration fields may be zero. Read the audio header
            // without guessing a duration from the title or file timestamps;
            // snapshot() still requires a complete decode and stable source.
            if let url, url.pathExtension.lowercased() == "qta" {
                lengthsAgree = false; duration = nil
                if let audio = try? AVAudioFile(forReading:url),
                   audio.length > 0, audio.processingFormat.sampleRate.isFinite, audio.processingFormat.sampleRate > 0 {
                    let measured = Double(audio.length) / audio.processingFormat.sampleRate
                    let known = [cloudDuration,localDuration].compactMap { $0 }.filter { $0 > 0 }
                    lengthsAgree = measured.isFinite && measured > 0 && known.allSatisfy { abs($0 - measured) <= 0.25 }
                    duration = measured
                }
            }
            let ready = !editing && url != nil && flagsKnown && revisionKnown && flags & 4 == 4 && flags & 258 == 0 && lengthsAgree
            let title = [string(9),string(5),string(6)].compactMap { $0 }.first { !$0.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty }
            result.append(SourceRecording(uuid:uuid,sourceURL:url,recordedAt:date,recordedAtSource:date == nil ? "unknown" : "voice_memos_zdate",originalTimezone:nil,duration:duration,title:title,completionEvidence:ready ? "finished" : "unknown",metadataRevision:revisionKnown ? String(sqlite3_column_int64(query,8)) : nil))
            code = sqlite3_step(query)
        }
        guard code == SQLITE_DONE else { throw BrokerFailure("source_metadata","음성 메모 목록 확인이 중단되었습니다.") }
        return result
    }
}
