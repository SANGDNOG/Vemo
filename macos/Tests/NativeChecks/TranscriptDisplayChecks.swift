import Foundation
import NativeCore

func transcriptDisplayChecks() throws {
    func fixture(_ text: String, _ pieces: [String], status: String = "completed") throws -> Transcript {
        let segments = pieces.enumerated().map { index, piece in
            ["text": piece, "start_s": Double(index + 1), "end_s": Double(index + 2)] as [String: Any]
        }
        return try JSONDecoder().decode(Transcript.self, from: JSONSerialization.data(withJSONObject: ["text": text, "segments": segments, "alignment_status": status]))
    }
    func untimedChunks(_ transcript: Transcript) -> Bool {
        let rows = TranscriptDisplay.rows(transcript)
        return rows.count > 1 && rows.map(\.text).joined() == transcript.text && rows.allSatisfy { $0.timing == nil }
    }
    let text = "한국어를 읽어요. English works!"
    let mixed = try fixture(text, ["한국", "어를", "읽어", "요", "English", "works"])
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
    let before = try encoder.encode(mixed)
    let srtBefore = try TranscriptExport.render(mixed, title: "fixture", format: "srt")
    let vttBefore = try TranscriptExport.render(mixed, title: "fixture", format: "vtt")
    let rows = TranscriptDisplay.rows(mixed)
    try check(rows.map(\.text).joined() == text, "sentence display preserves authoritative Korean/English spacing and punctuation")
    try check(rows.count == 2 && rows[0].text.trimmingCharacters(in: .whitespacesAndNewlines) == "한국어를 읽어요." && rows[1].text.trimmingCharacters(in: .whitespacesAndNewlines) == "English works!", "Korean subwords become readable sentences")
    try check(rows[0].timing?.start_s == 1 && rows[0].timing?.end_s == 5 && rows[1].timing?.start_s == 5 && rows[1].timing?.end_s == 7, "sentence timings map to first and last consumed raw segments")
    let repeated = try fixture("반복 반복. 반복 반복!", ["반", "복", "반복", "반복", "반", "복"])
    let repeats = TranscriptDisplay.rows(repeated)
    try check(repeats.count == 2 && repeats[0].timing?.end_s == 4 && repeats[1].timing?.start_s == 4 && repeats[1].timing?.end_s == 7, "repeated words map by sequential character consumption")
    for bad in [try fixture(text, []), try fixture(text, ["한국어를"]), try fixture(text, ["한국어를", "달라요", "English works"]), try fixture(text, ["한국어를 읽어요 English works extra"]), try fixture(text, ["English works 한국어를 읽어요"]), try fixture(text, [text], status: "aligning"), try fixture(text, [text], status: "failed")] {
        try check(untimedChunks(bad), "missing/partial/mismatched/unfinished alignment retains exact untimed sentence chunks")
    }
    var invalid = mixed
    invalid.segments[0].start_s = -1
    try check(untimedChunks(invalid), "negative display timing rejected")
    invalid = mixed; invalid.segments[1].end_s = .infinity
    try check(untimedChunks(invalid), "nonfinite display timing rejected")
    invalid = mixed; invalid.segments[2].start_s = 0
    try check(untimedChunks(invalid), "out-of-order display timing rejected")
    invalid = mixed; invalid.segments[0].end_s = 0
    try check(untimedChunks(invalid), "reversed display interval rejected")
    let paragraphText = "첫 문단입니다\n\n다음 paragraph 입니다"
    let paragraphs = TranscriptDisplay.rows(try fixture(paragraphText, ["첫", "문단입니다", "다음", "paragraph", "입니다"]))
    try check(paragraphs.map(\.text).joined() == paragraphText && paragraphs.contains { $0.text.contains("다음 paragraph") && $0.timing?.start_s == 3 }, "paragraph breaks preserve authoritative text and mapping")
    try check(untimedChunks(try fixture(paragraphText, [], status: "failed")), "failed alignment retains exact untimed paragraph chunks")
    let longText = String(repeating: "긴문장 English ", count: 90) + String(repeating: "가", count: 700)
    let bounded = TranscriptDisplay.rows(try fixture(longText, [longText]))
    try check(bounded.count > 3 && bounded.allSatisfy { $0.text.count <= TranscriptDisplay.maximumCharacters } && bounded.map(\.text).joined() == longText, "long unpunctuated paragraphs and words have bounded lossless display")
    try check(bounded.allSatisfy { $0.timing?.start_s == 1 && $0.timing?.end_s == 2 }, "shared raw segment uses actual bounds without invented interpolation")
    let untimedBounded = TranscriptDisplay.rows(try fixture(longText, ["partial"], status: "aligning"))
    try check(untimedBounded.map(\.text) == bounded.map(\.text) && untimedBounded.allSatisfy { $0.timing == nil }, "unfinished long text retains identical bounded chunks without timings")
    try check(TranscriptDisplay.rows(try fixture("", [])).isEmpty, "empty transcript has no display rows")
    try check(try encoder.encode(mixed) == before, "display leaves raw alignment segments unchanged")
    try check(try TranscriptExport.render(mixed, title: "fixture", format: "srt") == srtBefore && TranscriptExport.render(mixed, title: "fixture", format: "vtt") == vttBefore && srtBefore.contains("\n한국\n") && vttBefore.contains("\n어를\n"), "SRT/VTT retain raw subword cues after sentence display")
}
