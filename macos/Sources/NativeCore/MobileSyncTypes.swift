import Foundation
import CryptoKit

public struct MobileSyncProcessor: Codable, Sendable, Equatable {
    public let processorID: String
    public let displayName: String
    enum CodingKeys: String, CodingKey { case processorID = "processor_id", displayName = "display_name", schemaVersion = "schema_version" }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        guard try c.decode(Int.self, forKey: .schemaVersion) == 1 else { throw BrokerFailure("mobile_schema", "모바일 연결 버전을 지원하지 않습니다.") }
        processorID = try c.decode(String.self, forKey: .processorID)
        displayName = try c.decode(String.self, forKey: .displayName)
        try MobileSyncValidation.uuid(processorID)
    }
    init(processorID: String, displayName: String) { self.processorID = processorID; self.displayName = displayName }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(1, forKey: .schemaVersion); try c.encode(processorID, forKey: .processorID); try c.encode(displayName, forKey: .displayName)
    }
}

public struct MobileSyncConfiguration: Codable, Sendable {
    public let syncRoot: URL
    public let processorID: String
    public let displayName: String
    let device: Int32
    let inode: UInt64
    var scope: String { MobileSyncValidation.digest(Data("\(syncRoot.path):\(device):\(inode)".utf8)) }
}

public struct MobileSyncMapping: Codable, Sendable {
    public let recordingID: String
    public let audioSHA256: String
    public let memoUUID: String
    public let sourceReference: String
    public var jobID: String?
    public var delivered: Bool
    public var suppressed: Bool
}

public struct MobileSyncIssue: Sendable {
    public let recordingID: String?
    public let code: String
    public let message: String
}

public struct MobileSyncScan: Sendable {
    public var pending: [Memo] = []
    public var issues: [MobileSyncIssue] = []
    public var skipped: Int = 0
}

struct MobileSyncRecording: Codable, Equatable, Sendable {
    let schema_version: Int
    let recording_id: String
    let audio_sha256: String
    let audio_bytes: Int64
    let audio_file: String
    let recorded_at: String
    let title: String
    let mode: String
    let target_processor_id: String?
    let completed: Bool

    static func decode(_ data: Data) throws -> Self {
        let keys: Set<String> = ["schema_version", "recording_id", "audio_sha256", "audio_bytes", "audio_file", "recorded_at", "title", "mode", "target_processor_id", "completed"]
        try MobileSyncValidation.object(data, keys: keys)
        let value = try JSONDecoder().decode(Self.self, from: data)
        guard value.schema_version == 1, value.completed, value.audio_file == "audio.m4a",
              value.audio_bytes > 0, value.audio_bytes <= MobileSyncFiles.maximumAudioBytes,
              ["desktop", "record_only"].contains(value.mode) else { throw BrokerFailure("invalid_recording", "완료된 모바일 녹음 정보가 올바르지 않습니다.") }
        try MobileSyncValidation.uuid(value.recording_id); try MobileSyncValidation.hash(value.audio_sha256)
        if let target = value.target_processor_id { try MobileSyncValidation.uuid(target) }
        guard value.mode != "desktop" || value.target_processor_id != nil else { throw BrokerFailure("invalid_recording", "지정 처리기가 없습니다.") }
        _ = try MobileSyncValidation.timezone(value.recorded_at)
        guard !value.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !value.title.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { throw BrokerFailure("invalid_recording", "녹음 제목이 올바르지 않습니다.") }
        return value
    }
}

// Internal, verified metadata: ordinary imports retain their existing behavior.
struct MobileSyncImportMetadata: Sendable {
    let recording: MobileSyncRecording
    let memoUUID: String
    let sourceReference: String
}

enum MobileSyncValidation {
    static func digest(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
    static func uuid(_ value: String) throws {
        guard let parsed = UUID(uuidString: value), parsed.uuidString.lowercased() == value else { throw BrokerFailure("invalid_uuid", "모바일 UUID가 올바르지 않습니다.") }
    }
    static func hash(_ value: String) throws {
        guard value.utf8.count == 64, value.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else { throw BrokerFailure("invalid_hash", "모바일 오디오 해시가 올바르지 않습니다.") }
    }
    static func localUUID(_ value: String) -> String {
        var bytes = Array(SHA256.hash(data: Data(value.utf8)).prefix(16))
        bytes[6] = (bytes[6] & 0x0f) | 0x50; bytes[8] = (bytes[8] & 0x3f) | 0x80
        let hex = bytes.map { String(format: "%02x", $0) }.joined()
        let chars = Array(hex)
        return [String(chars[0..<8]), String(chars[8..<12]), String(chars[12..<16]), String(chars[16..<20]), String(chars[20..<32])].joined(separator: "-")
    }
    static func object(_ data: Data, keys: Set<String>) throws {
        guard data.count <= WireCodec.limit, String(data: data, encoding: .utf8) != nil else { throw BrokerFailure("invalid_recording", "모바일 JSON이 올바르지 않습니다.") }
        // Foundation discards duplicate keys. Inspect the original structure first
        // so escaped aliases and conflicting identity/completion fields cannot hide.
        var structure = MobileSyncJSONKeys(bytes: Array(data))
        try structure.validate()
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any], Set(object.keys) == keys else { throw BrokerFailure("invalid_recording", "모바일 JSON 필드가 올바르지 않습니다.") }
        // JSONDecoder accepts some numeric coercions. Reject booleans/fractional byte counts explicitly.
        for name in ["schema_version", "audio_bytes"] where object[name] != nil {
            guard let number = object[name] as? NSNumber, String(cString: number.objCType) != "c", number.doubleValue.isFinite,
                  number.doubleValue.rounded(.towardZero) == number.doubleValue else { throw BrokerFailure("invalid_recording", "모바일 JSON 숫자가 올바르지 않습니다.") }
        }
        if let completed = object["completed"] { guard let number = completed as? NSNumber, String(cString: number.objCType) == "c" else { throw BrokerFailure("invalid_recording", "완료 표시가 올바르지 않습니다.") } }
    }
    static func timezone(_ value: String) throws -> String {
        let pattern = #"^([0-9]{4}-[0-9]{2}-[0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.[0-9]{1,9})?(Z|[+-][0-9]{2}:[0-9]{2})$"#
        guard value.utf8.count <= 40, value.range(of: pattern, options: .regularExpression) != nil else { throw BrokerFailure("invalid_date", "시간대가 포함된 녹음 시작 시간이 필요합니다.") }
        do { _ = try Validation.recordingDate(String(value.prefix(10))) } catch { throw BrokerFailure("invalid_date", "유효하지 않은 녹음 날짜입니다.") }
        let parts = Array(value.utf8)
        func n(_ a: Int) -> Int { Int(parts[a] - 48) * 10 + Int(parts[a + 1] - 48) }
        guard n(11) < 24, n(14) < 60, n(17) < 60 else { throw BrokerFailure("invalid_date", "유효하지 않은 녹음 시간입니다.") }
        let offset: Int
        if value.hasSuffix("Z") { offset = 0 }
        else {
            let suffix = Array(value.suffix(6).utf8)
            let hours = Int(suffix[1] - 48) * 10 + Int(suffix[2] - 48), minutes = Int(suffix[4] - 48) * 10 + Int(suffix[5] - 48)
            guard hours <= 14, minutes < 60, hours != 14 || minutes == 0 else { throw BrokerFailure("invalid_date", "유효하지 않은 시간대입니다.") }
            offset = (suffix[0] == 45 ? -1 : 1) * (hours * 3600 + minutes * 60)
        }
        // The engine accepts explicit fixed offsets. Preserve the phone's civil
        // date without guessing an IANA location from an offset alone.
        if offset == 0 { return "UTC" }
        return "UTC" + (offset < 0 ? "-" : "+") + String(format: "%02d:%02d", abs(offset) / 3600, (abs(offset) % 3600) / 60)
    }
}

// A bounded structural pass only: Foundation still validates scalar syntax and
// UTF-8/escape correctness. Each object owns its key set; string values never
// participate in key detection. Decode key tokens to catch Unicode escape aliases.
private struct MobileSyncJSONKeys {
    let bytes: [UInt8]
    private var position = 0
    init(bytes: [UInt8]) { self.bytes = bytes }
    private func invalid() -> BrokerFailure { BrokerFailure("invalid_recording", "모바일 JSON에 중복 필드 또는 올바르지 않은 구조가 있습니다.") }
    private mutating func whitespace() {
        while position < bytes.count, [9, 10, 13, 32].contains(bytes[position]) { position += 1 }
    }
    private mutating func take(_ byte: UInt8) -> Bool {
        guard position < bytes.count, bytes[position] == byte else { return false }
        position += 1; return true
    }
    private mutating func string() throws -> Range<Int> {
        let start = position
        guard take(34) else { throw invalid() }
        while position < bytes.count {
            let byte = bytes[position]; position += 1
            if byte == 34 { return start..<position }
            guard byte >= 32 else { throw invalid() }
            if byte == 92 {
                guard position < bytes.count else { throw invalid() }
                position += 1
            }
        }
        throw invalid()
    }
    mutating func validate() throws {
        try value(depth: 0); whitespace()
        guard position == bytes.count else { throw invalid() }
    }
    private mutating func value(depth: Int) throws {
        whitespace()
        guard position < bytes.count else { throw invalid() }
        switch bytes[position] {
        case 123: // object
            guard depth < 24 else { throw invalid() }
            position += 1; whitespace()
            if take(125) { return }
            var keys: Set<String> = []
            while true {
                whitespace()
                let token = try string()
                let key = try JSONDecoder().decode(String.self, from: Data(bytes[token]))
                guard keys.insert(key).inserted else { throw invalid() }
                whitespace(); guard take(58) else { throw invalid() }
                try value(depth: depth + 1); whitespace()
                if take(125) { return }
                guard take(44) else { throw invalid() }
            }
        case 91: // array
            guard depth < 24 else { throw invalid() }
            position += 1; whitespace()
            if take(93) { return }
            while true {
                try value(depth: depth + 1); whitespace()
                if take(93) { return }
                guard take(44) else { throw invalid() }
            }
        case 34:
            _ = try string()
        default:
            let start = position
            while position < bytes.count, ![9, 10, 13, 32, 44, 93, 125].contains(bytes[position]) { position += 1 }
            guard position > start else { throw invalid() }
        }
    }
}
