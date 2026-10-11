import Foundation
import CryptoKit

public struct RecordingNotes: Codable, Sendable {
    public struct Summary: Codable, Sendable { public var text: String; public var quotes: [String] }
    public struct Highlight: Codable, Sendable { public var quote: String }
    public struct Topic: Codable, Sendable { public var keyword: String; public var summary: String; public var quotes: [String] }
    public var schema_version: Int
    public var source_sha256: String
    public var summary: [Summary]
    public var highlights: [Highlight]
    public var topics: [Topic]
}

public enum NotesDisplay {
    public struct QuoteTarget: Sendable { public let row: Int; public let timing: TranscriptDisplay.Timing? }

    public static func validated(_ notes: RecordingNotes, text: String) throws -> RecordingNotes {
        let hash = SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
        guard notes.schema_version == 1, notes.source_sha256 == hash else { throw NativeFailure.invalid("현재 전사문과 일치하지 않는 노트입니다. 노트를 다시 생성하세요.") }
        func real(_ quote: String) -> Bool { !quote.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && quote.count <= 600 && text.range(of:quote,options:.literal) != nil }
        var result = notes
        result.summary = notes.summary.compactMap { item in
            let quotes = item.quotes.filter(real)
            guard !quotes.isEmpty, !item.text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty, item.text.count <= 1000 else { return nil }
            return RecordingNotes.Summary(text:item.text, quotes:Array(quotes.prefix(4)))
        }
        result.highlights = notes.highlights.filter { real($0.quote) }
        result.topics = notes.topics.compactMap { item in
            let quotes = item.quotes.filter(real)
            guard !quotes.isEmpty, !item.keyword.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty, item.keyword.count <= 80, !item.summary.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty, item.summary.count <= 1000 else { return nil }
            return RecordingNotes.Topic(keyword:item.keyword, summary:item.summary, quotes:Array(quotes.prefix(4)))
        }
        return result
    }

    public static func quoteTarget(_ quote: String, in transcript: Transcript) -> QuoteTarget? {
        guard !quote.isEmpty, let range = transcript.text.range(of:quote,options:.literal) else { return nil }
        let ambiguous = transcript.text.range(of:quote,options:.literal,range:transcript.text.index(after:range.lowerBound)..<transcript.text.endIndex) != nil
        let offset = transcript.text.distance(from:transcript.text.startIndex,to:range.lowerBound)
        var consumed = 0
        for (index,row) in TranscriptDisplay.rows(transcript).enumerated() {
            if offset < consumed + row.text.count { return QuoteTarget(row:index,timing:ambiguous ? nil : row.timing) }
            consumed += row.text.count
        }
        return nil
    }

    public static func markdown(_ notes: RecordingNotes, title: String) -> String {
        func quote(_ text: String) -> String { text.components(separatedBy:.newlines).map { "> " + $0 }.joined(separator:"\n") }
        var lines = ["# " + title, "", "AI가 정리한 노트입니다. 중요한 내용은 인용한 원문에서 확인하세요.", "", "## 핵심 요약", ""]
        for item in notes.summary { lines += ["- " + item.text, ""]; lines += item.quotes.map(quote); lines.append("") }
        lines += ["## 강조 문장", ""]
        for item in notes.highlights { lines += [quote(item.quote), ""] }
        lines += ["## 키워드별 정리", ""]
        for item in notes.topics { lines += ["### " + item.keyword, "", item.summary, ""]; lines += item.quotes.map(quote); lines.append("") }
        return lines.joined(separator:"\n") + "\n"
    }
}
