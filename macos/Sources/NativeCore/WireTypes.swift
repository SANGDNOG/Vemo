import Foundation
public enum JSONValue: Codable, Sendable, Equatable {
    case string(String), integer(Int64), number(Double), bool(Bool), object([String:JSONValue]), array([JSONValue]), null
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null } else if let v = try? c.decode(Bool.self) { self = .bool(v) } else if let v = try? c.decode(String.self) { self = .string(v) } else if let v = try? c.decode(Int64.self) { self = .integer(v) } else if let v = try? c.decode(Double.self) { self = .number(v) } else if let v = try? c.decode([String:JSONValue].self) { self = .object(v) } else { self = .array(try c.decode([JSONValue].self)) }
    }
    public func encode(to encoder: Encoder) throws { var c = encoder.singleValueContainer(); switch self { case .string(let v): try c.encode(v); case .integer(let v): try c.encode(v); case .number(let v): try c.encode(v); case .bool(let v): try c.encode(v); case .object(let v): try c.encode(v); case .array(let v): try c.encode(v); case .null: try c.encodeNil() } }
    public var string: String? { if case .string(let v) = self { return v }; return nil }
    public var object: [String:JSONValue]? { if case .object(let v) = self { return v }; return nil }
    public var bool: Bool? { if case .bool(let v) = self { return v }; return nil }
    public var numeric: Double? { if case .integer(let v) = self { return Double(v) }; if case .number(let v) = self { return v }; return nil }
    public static func encoded<T: Encodable>(_ value: T) throws -> JSONValue { try JSONDecoder().decode(Self.self, from: JSONEncoder().encode(value)) }
    public func decoded<T: Decodable>(_ type: T.Type) throws -> T { try JSONDecoder().decode(type, from: JSONEncoder().encode(self)) }
}
public struct WireCommand: Codable, Sendable {
    public var request_id: String = UUID().uuidString
    public var protocol_version = 1
    public var kind: String
    public var payload: [String:JSONValue]
    public init(_ kind: String, _ payload: [String:JSONValue] = [:]) { self.kind = kind; self.payload = payload }
}
public struct WireEvent: Codable, Sendable {
    public var sequence: Int; public var protocol_version: Int; public var kind: String; public var payload: [String:JSONValue]
}
public struct MediaRevision: Codable, Sendable, Equatable { public var size: Int64; public var mtime_ns: Int64 }
public struct Memo: Codable, Sendable {
    public var uuid: String; public var recorded_at: String?; public var recorded_at_source: String
    public var original_timezone: String?; public var duration_s: Double?; public var media_revision: MediaRevision?
    public var completion_evidence: String; public var source_kind: String; public var source_reference: String; public var original_title: String?
    public var metadata_revision: String? = nil
}
public struct Job: Codable, Sendable, Identifiable {
    public var id: String; public var uuid: String; public var topic: String?; public var recording_date: String?; public var recorded_at: String?; public var duration_s: Double?
    public var status: String; public var wait_reason: String?; public var stage: String; public var completed_units: Int?; public var total_units: Int?; public var elapsed_seconds: Double
    public var artifact_path: String?; public var audio_path: String?; public var pcm_path: String?; public var error: JSONValue?; public var title_status: String; public var alignment_status: String; public var original_title: String?
    public var notes_path: String?; public var notes_status: String?; public var notes_error: JSONValue?
    public var deleted_at: String?
    /// QTA spatial streams are not supported by AVAudioPlayer. Wait for prepared PCM.
    /// The consumer must still validate the chosen path inside its owned state root.
    public var playback_path: String? {
        guard let audio_path else { return nil }
        if URL(fileURLWithPath: audio_path).pathExtension.lowercased() == "qta" {
            guard let pcm_path, !pcm_path.isEmpty else { return nil }
            return pcm_path
        }
        return audio_path
    }
    public var title: String { let name = topic ?? original_title ?? "음성 녹음"; return recording_date.map { "\(name) — \($0)" } ?? name }
    public var progress: String { if let c = completed_units, let t = total_units, t > 0 { return "\(c)/\(t) 구간" }; return "" }
    public var statusLabel: String { Self.labels[status] ?? status }
    private static let labels = ["unprocessed":"미처리", "waiting":"처리 대기", "preparing":"준비 중", "transcribing":"전사 중", "aligning":"시간 정렬 중", "titling":"제목 생성 중", "noting":"노트 정리 중", "saving":"저장 중", "completed":"완료", "needs_recording_date":"녹음일 확인 필요", "failed":"실패", "paused":"일시 중지"]
}
public struct Transcript: Codable, Sendable {
    public struct Segment: Codable, Sendable { public var start_s: Double; public var end_s: Double; public var text: String }
    public var text: String; public var segments: [Segment]; public var alignment_status: String
}
public struct PublicationManifest: Codable, Sendable {
    public struct File: Codable, Sendable { public var name: String; public var path: String; public var sha256: String }
    public var schema_version: Int; public var publication_id: String; public var job_id: String; public var uuid: String; public var recording_date: String; public var title: String; public var revision: JSONValue; public var files: [File]
}
public struct PublicationReceipt: Codable, Sendable { public var publication_id: String; public var output_manifest_path: String; public var artifact_hashes: [String:String] }
public enum WireCodec {
    public static let limit = 8 * 1024 * 1024
    public static func decode(_ data: Data, after sequence: Int) throws -> WireEvent {
        guard data.count <= limit else { throw NativeFailure.invalid("엔진 메시지가 8 MiB 제한을 초과했습니다.") }
        let event = try JSONDecoder().decode(WireEvent.self, from: data)
        guard event.protocol_version == 1, event.sequence > sequence else { throw NativeFailure.invalid("지원하지 않는 프로토콜 또는 이벤트 순서입니다.") }
        return event
    }
}
