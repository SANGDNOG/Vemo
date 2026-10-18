import Foundation
import CryptoKit
import NativeCore

func speakerChecks() throws {
    let hash = String(repeating: "a", count: 64)
    let text = "Hello. "
    let digest = SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    let transcript = try JSONDecoder().decode(Transcript.self, from: Data(#"{"text":"Hello. ","segments":[{"start_s":0,"end_s":2,"text":"Hello. "}],"alignment_status":"completed"}"#.utf8))
    let base: [String: Any] = ["schema_version": 1, "audio_sha256": hash, "transcript_sha256": digest,
        "generation": "12345678-1234-4234-8234-123456789abc",
        "speakers": [["id": "speaker_1", "name": "화자 1"]],
        "turns": [["start_s": 0, "end_s": 2, "speaker_id": "speaker_1"]],
        "segments": [["start_s": 0, "end_s": 2, "text": text, "speaker_id": "speaker_1"]]]
    func decode(_ value: [String: Any]) throws -> SpeakerDocument {
        try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript)
    }
    let valid = try decode(base)
    let rows = TranscriptDisplay.rows(transcript)
    try check(rows.map(\.text).joined() == text && valid.speakerID(for: rows[0]) == "speaker_1", "speaker sentence rows retain exact text and label")
    var value = base; value["audio_sha256"] = String(repeating: "b", count: 64)
    try rejects("speaker stale audio rejected") { _ = try decode(value) }
    value = base; value["transcript_sha256"] = String(repeating: "b", count: 64)
    try rejects("speaker stale exact transcript rejected") { _ = try decode(value) }
    let json = String(decoding: try JSONSerialization.data(withJSONObject: base, options: [.sortedKeys]), as: UTF8.self)
    for (key, replacement) in [
        ("\"schema_version\":1", "\"schema_version\":1,\"schema_\\u0076ersion\":1"),
        ("\"name\":\"화자 1\"", "\"name\":\"화자 1\",\"na\\u006de\":\"화자 1\""),
        ("\"start_s\":0", "\"start_s\":0,\"start_s\":0")
    ] {
        let altered = json.replacingOccurrences(of: key, with: replacement)
        try check(altered != json, "duplicate fixture changed source")
        try rejects("speaker recursive duplicate rejected") {
            _ = try SpeakerDocument.decode(Data(altered.utf8), audioHash: hash, transcript: transcript)
        }
    }
    for array in ["speakers", "turns", "segments"] {
        value = base
        var items = value[array] as! [[String: Any]]; items[0]["extra"] = true; value[array] = items
        try rejects("speaker strict keys: \(array)") { _ = try decode(value) }
    }
    for name in ["bad\nname", "bad\u{200D}name", "bad\u{E000}name", "bad\u{0378}name"] {
        value = base; value["speakers"] = [["id": "speaker_1", "name": name]]
        try rejects("speaker Unicode C category name rejected") { _ = try decode(value) }
    }
    value = base
    value["turns"] = [["start_s": 0, "end_s": 1.2, "speaker_id": "speaker_1"], ["start_s": 0.8, "end_s": 2, "speaker_id": "speaker_1"]]
    _ = try decode(value); try check(true, "speaker same-speaker overlapping turns union")
    value["turns"] = [["start_s": 0, "end_s": 1, "speaker_id": "speaker_1"]]
    try rejects("speaker exact half coverage rejected") { _ = try decode(value) }
    value = base
    value["speakers"] = [["id": "speaker_1", "name": "화자 1"], ["id": "speaker_2", "name": "화자 2"]]
    value["turns"] = [["start_s": 0, "end_s": 2, "speaker_id": "speaker_1"], ["start_s": 1.9, "end_s": 2, "speaker_id": "speaker_2"]]
    try rejects("speaker competing overlap rejected") { _ = try decode(value) }
    value["segments"] = [["start_s": 0, "end_s": 2, "text": text, "speaker_id": NSNull()]]
    let unknown = try decode(value)
    let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(unknown)) as! [String: Any]
    try check((encoded["segments"] as! [[String: Any]])[0]["speaker_id"] is NSNull, "speaker unknown serializes explicit null")
    value = base
    value["segments"] = [["start_s": 0, "end_s": 2, "text": "Hello", "speaker_id": NSNull()], ["start_s": 1, "end_s": 1.5, "text": ". ", "speaker_id": NSNull()]]
    try rejects("speaker segment ends cannot run backward") { _ = try decode(value) }
    let grapheme = try JSONDecoder().decode(Transcript.self, from: Data(#"{"text":"a\u0338","segments":[{"start_s":0,"end_s":1,"text":"a"},{"start_s":1,"end_s":2,"text":"\u0338"}],"alignment_status":"completed"}"#.utf8))
    let plain = TranscriptDisplay.rows(grapheme)
    try check(plain.map(\.text).joined() == "a\u{0338}" && plain.allSatisfy { $0.timing == nil }, "speaker intra-grapheme boundary falls back to exact plain text")
    let longText = String(repeating: "a", count: 1500)
    value = base
    value["transcript_sha256"] = SHA256.hash(data: Data(longText.utf8)).map { String(format: "%02x", $0) }.joined()
    value["turns"] = (0..<1500).map { ["start_s": $0, "end_s": $0 + 1, "speaker_id": "speaker_1"] as [String: Any] }
    value["segments"] = (0..<1500).map { _ in ["start_s": 0, "end_s": 1500, "text": "a", "speaker_id": "speaker_1"] as [String: Any] }
    let longTranscript = try JSONDecoder().decode(Transcript.self, from: JSONSerialization.data(withJSONObject: ["text": longText, "segments": [], "alignment_status": "failed"]))
    try rejects("speaker assignment comparison budget enforced") {
        _ = try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: longTranscript)
    }
}
