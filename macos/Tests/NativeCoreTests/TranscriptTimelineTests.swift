import XCTest
@testable import NativeCore

final class TranscriptTimelineTests: XCTestCase {
    func testSentenceRowsUseActualSegmentBoundsAndExactUTF8Hash() throws {
        let text = "한국어를 읽어요. English works!\n"
        let pieces = ["한국", "어를", "읽어", "요", "English", "works"]
        let transcript = Transcript(text: text, segments: pieces.enumerated().map {
            Transcript.Segment(start_s: Double($0.offset + 1), end_s: Double($0.offset + 2), text: $0.element)
        }, alignment_status: "completed")
        let timeline = TranscriptTimeline(recordingID: "recording", audioSHA256: "audio", processorID: "processor", transcript: transcript)
        XCTAssertEqual(timeline.segments.map(\.text), ["한국어를 읽어요. ", "English works!\n"])
        XCTAssertEqual(timeline.segments.map(\.start), [1, 5])
        XCTAssertEqual(timeline.segments.map(\.end), [5, 7])
        XCTAssertEqual(timeline.segments.map(\.text).joined(), text)
        XCTAssertEqual(timeline.transcript_sha256, MobileSyncValidation.digest(Data(text.utf8)))
        // No normalization of authoritative decomposed Unicode or CRLF.
        let raw = "e\u{301}\r\n"
        let exact = TranscriptTimeline(recordingID: "r", audioSHA256: "a", processorID: "p",
            transcript: Transcript(text: raw, segments: [], alignment_status: "failed"))
        XCTAssertEqual(Data(exact.segments.map(\.text).joined().utf8), Data(raw.utf8))
        XCTAssertNotEqual(exact.transcript_sha256, MobileSyncValidation.digest(Data(raw.precomposedStringWithCanonicalMapping.utf8)))
    }

    func testUntrustedAlignmentProducesExplicitNullsWithoutInventedTiming() throws {
        for (status, segments) in [
            ("failed", [Transcript.Segment(start_s: 1, end_s: 3, text: "First. Second.")]),
            ("completed", [Transcript.Segment(start_s: 1, end_s: 3, text: "wrong")]),
            ("completed", [Transcript.Segment(start_s: -1, end_s: 3, text: "First. Second.")]),
            ("completed", [])
        ] {
            let timeline = TranscriptTimeline(recordingID: "r", audioSHA256: "a", processorID: "p",
                transcript: Transcript(text: "First. Second.", segments: segments, alignment_status: status))
            let json = try JSONSerialization.jsonObject(with: BrokerFiles.json(timeline)) as! [String: Any]
            let rows = try XCTUnwrap(json["segments"] as? [[String: Any]])
            XCTAssertEqual(rows.map { $0["text"] as! String }, ["First. ", "Second."])
            XCTAssertTrue(rows.allSatisfy { Set($0.keys) == Set(["start", "end", "text"]) && $0["start"] is NSNull && $0["end"] is NSNull })
        }
    }

    func testLongSharedSegmentUsesSameRealBoundsForEveryDisplayRow() {
        let text = String(repeating: "long text ", count: 100)
        let timeline = TranscriptTimeline(recordingID: "r", audioSHA256: "a", processorID: "p",
            transcript: Transcript(text: text, segments: [.init(start_s: 2, end_s: 10, text: text)], alignment_status: "completed"))
        XCTAssertTrue(timeline.segments.count > 1)
        XCTAssertEqual(timeline.segments.map(\.text).joined(), text)
        XCTAssertTrue(timeline.segments.allSatisfy { $0.start == 2 && $0.end == 10 && $0.text.count <= TranscriptDisplay.maximumCharacters })
    }
}
