import XCTest
@testable import NativeCore

final class SpeakerDocumentTests: XCTestCase {
    private let hash = String(repeating: "a", count: 64)
    private func document(_ text: String = "Hello. ") -> [String: Any] {
        ["schema_version": 1, "audio_sha256": hash,
         "transcript_sha256": MobileSyncValidation.digest(Data(text.utf8)),
         "generation": "12345678-1234-4234-8234-123456789abc",
         "speakers": [["id": "speaker_1", "name": "화자 1"]],
         "turns": [["start_s": 0, "end_s": 2, "speaker_id": "speaker_1"]],
         "segments": [["start_s": 0, "end_s": 2, "text": text, "speaker_id": "speaker_1"]]]
    }
    func testOptionalSpeakerContractAndExactHash() throws {
        let transcript = Transcript(text: "Hello. ", segments: [.init(start_s: 0, end_s: 2, text: "Hello. ")], alignment_status: "completed")
        let bytes = try JSONSerialization.data(withJSONObject: document())
        let doc = try SpeakerDocument.decode(bytes, audioHash: hash, transcript: transcript)
        let rows = TranscriptDisplay.rows(transcript)
        XCTAssertEqual(rows.map(\.text).joined(), transcript.text)
        XCTAssertEqual(doc.speakerID(for: rows[0]), "speaker_1")
        XCTAssertThrowsError(try SpeakerDocument.decode(bytes, audioHash: String(repeating: "b", count: 64), transcript: transcript))
        let changed = Transcript(text: "Hello.\n", segments: [], alignment_status: "failed")
        XCTAssertThrowsError(try SpeakerDocument.decode(bytes, audioHash: hash, transcript: changed))
    }
    func testInvalidSpeakerMetadataIsRejected() throws {
        let transcript = Transcript(text: "Hello. ", segments: [], alignment_status: "failed")
        var invalid = document()
        invalid["speakers"] = [["id": "speaker_1", "name": "bad\nname"]]
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: invalid), audioHash: hash, transcript: transcript))
        invalid = document()
        invalid["segments"] = [["start_s": 0, "end_s": 2, "text": "Hello. ", "speaker_id": "missing"]]
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: invalid), audioHash: hash, transcript: transcript))
    }
    func testSegmentBoundaryInsideGraphemeRetainsPlainOriginalRow() {
        let text = "a\u{0338}"
        let transcript = Transcript(text: text, segments: [
            .init(start_s: 0, end_s: 1, text: "a"),
            .init(start_s: 1, end_s: 2, text: "\u{0338}")
        ], alignment_status: "completed")
        let rows = TranscriptDisplay.rows(transcript)
        XCTAssertEqual(rows.map(\.text).joined(), text)
        XCTAssertTrue(rows.allSatisfy { $0.timing == nil })
    }
}

extension SpeakerDocumentTests {
    func testDuplicateAndExtraKeysRejectedAtEveryLevel() throws {
        let transcript = Transcript(text: "Hello. ", segments: [], alignment_status: "failed")
        let data = try JSONSerialization.data(withJSONObject: document(), options: [.sortedKeys])
        let json = String(decoding: data, as: UTF8.self)
        for altered in [
            json.replacingOccurrences(of: "\"schema_version\":1", with: "\"schema_version\":1,\"schema_\\u0076ersion\":1"),
            json.replacingOccurrences(of: "\"name\":\"화자 1\"", with: "\"name\":\"화자 1\",\"na\\u006de\":\"화자 1\""),
            json.replacingOccurrences(of: "\"start_s\":0", with: "\"start_s\":0,\"start_s\":0")
        ] {
            XCTAssertNotEqual(altered, json)
            XCTAssertThrowsError(try SpeakerDocument.decode(Data(altered.utf8), audioHash: hash, transcript: transcript))
        }
        for array in ["speakers", "turns", "segments"] {
            var extra = document()
            var items = extra[array] as! [[String: Any]]
            items[0]["extra"] = true
            extra[array] = items
            XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: extra), audioHash: hash, transcript: transcript))
        }
    }

    func testUnicodeOtherCategoriesRejectedInNames() throws {
        let transcript = Transcript(text: "Hello. ", segments: [], alignment_status: "failed")
        for name in ["bad\u{200D}name", "bad\u{E000}name", "bad\u{0378}name"] {
            var invalid = document()
            invalid["speakers"] = [["id": "speaker_1", "name": name]]
            XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: invalid), audioHash: hash, transcript: transcript))
        }
    }

    func testIndependentAssignmentsUnionAndRejectOverlapOrHalfCoverage() throws {
        let transcript = Transcript(text: "Hello. ", segments: [], alignment_status: "failed")
        var value = document()
        value["turns"] = [
            ["start_s": 0, "end_s": 1.2, "speaker_id": "speaker_1"],
            ["start_s": 0.8, "end_s": 2, "speaker_id": "speaker_1"]]
        XCTAssertNoThrow(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript))
        value["turns"] = [["start_s": 0, "end_s": 1, "speaker_id": "speaker_1"]]
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript))
        value = document()
        value["speakers"] = [["id": "speaker_1", "name": "화자 1"], ["id": "speaker_2", "name": "화자 2"]]
        value["turns"] = [
            ["start_s": 0, "end_s": 2, "speaker_id": "speaker_1"],
            ["start_s": 1.9, "end_s": 2, "speaker_id": "speaker_2"]]
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript))
        value["segments"] = [["start_s": 0, "end_s": 2, "text": "Hello. ", "speaker_id": NSNull()]]
        let decoded = try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript)
        let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(decoded)) as! [String: Any]
        let segments = encoded["segments"] as! [[String: Any]]
        XCTAssertTrue(segments[0]["speaker_id"] is NSNull)
    }

    func testSegmentEndsCannotRunBackward() throws {
        let transcript = Transcript(text: "Hello. ", segments: [], alignment_status: "failed")
        var value = document()
        value["segments"] = [
            ["start_s": 0, "end_s": 2, "text": "Hello", "speaker_id": NSNull()],
            ["start_s": 1, "end_s": 1.5, "text": ". ", "speaker_id": NSNull()]]
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript))
    }

    func testAssignmentComparisonBudgetRejectsPathologicalInput() throws {
        let text = String(repeating: "a", count: 1500)
        let transcript = Transcript(text: text, segments: [], alignment_status: "failed")
        var value = document(text)
        value["turns"] = (0..<1500).map { index -> [String: Any] in
            ["start_s": index, "end_s": index + 1, "speaker_id": "speaker_1"]
        }
        value["segments"] = (0..<1500).map { _ -> [String: Any] in
            ["start_s": 0, "end_s": 1500, "text": "a", "speaker_id": "speaker_1"]
        }
        XCTAssertThrowsError(try SpeakerDocument.decode(JSONSerialization.data(withJSONObject: value), audioHash: hash, transcript: transcript))
    }
}
