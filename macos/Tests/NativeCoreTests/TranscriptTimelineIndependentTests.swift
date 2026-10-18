import XCTest
@testable import NativeCore

final class TranscriptTimelineIndependentTests: XCTestCase {
    private let recording = "01234567-89ab-4cde-8fab-0123456789ab"
    private let processor = "11234567-89ab-4cde-8fab-0123456789ab"
    private func encoded(_ text: String, segments: [Transcript.Segment], status: String = "completed") throws -> [String: Any] {
        let transcript = Transcript(text: text, segments: segments, alignment_status: status)
        let bytes = try BrokerFiles.json(TranscriptTimeline(recordingID: recording,
            audioSHA256: String(repeating: "a", count: 64), processorID: processor, transcript: transcript))
        return try JSONSerialization.jsonObject(with: bytes) as! [String: Any]
    }
    private func rows(_ value: [String: Any]) -> [[String: Any]] { value["segments"] as! [[String: Any]] }

    func testExactUnicodeHashAndTextSurviveSidecarEncoding() throws {
        let text = "cafe\u{301} 🎙️.\r\n한글!  "
        let value = try encoded(text, segments: [.init(start_s: 0, end_s: 2.75, text: text)])
        XCTAssertEqual(Set(value.keys), Set(["schema_version", "recording_id", "audio_sha256", "processor_id", "transcript_sha256", "segments"]))
        XCTAssertEqual(value["transcript_sha256"] as? String, MobileSyncValidation.digest(Data(text.utf8)))
        XCTAssertNotEqual(value["transcript_sha256"] as? String, MobileSyncValidation.digest(Data(text.precomposedStringWithCanonicalMapping.utf8)))
        XCTAssertEqual(Data(rows(value).map { $0["text"] as! String }.joined().utf8), Data(text.utf8))
        XCTAssertEqual(rows(value).first?["start"] as? Double, 0)
        XCTAssertEqual(rows(value).last?["end"] as? Double, 2.75)
    }
    func testInvalidAndUnalignedTimesFallBackToExplicitNullWithoutLosingText() throws {
        let text = "First sentence. Next sentence."
        for (start, end, status) in [
            (Double.nan, 2.0, "completed"), (0.0, Double.infinity, "completed"),
            (-0.1, 2.0, "completed"), (3.0, 2.0, "completed"), (0.0, 2.0, "failed")
        ] {
            let value = try encoded(text, segments: [.init(start_s: start, end_s: end, text: text)], status: status)
            XCTAssertEqual(rows(value).map { $0["text"] as! String }.joined(), text)
            XCTAssertTrue(rows(value).allSatisfy { $0["start"] is NSNull && $0["end"] is NSNull })
        }
    }
    func testBackwardSegmentBoundsAndUnmatchedCoverageFallBack() throws {
        let text = "First sentence. Next sentence."
        for segments: [Transcript.Segment] in [
            [.init(start_s: 1, end_s: 3, text: "First sentence. "), .init(start_s: 0.5, end_s: 4, text: "Next sentence.")],
            [.init(start_s: 1, end_s: 3, text: "First sentence. "), .init(start_s: 2, end_s: 2.5, text: "Next sentence.")],
            [.init(start_s: 0, end_s: 2, text: "Unrelated text.")], []
        ] {
            let value = try encoded(text, segments: segments)
            XCTAssertEqual(rows(value).map { $0["text"] as! String }.joined(), text)
            XCTAssertTrue(rows(value).allSatisfy { $0["start"] is NSNull && $0["end"] is NSNull })
        }
    }
}
