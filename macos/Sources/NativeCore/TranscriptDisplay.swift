import Foundation
import NaturalLanguage

/// A presentation-only projection. Exports continue to use the untouched raw segments.
public enum TranscriptDisplay {
    public struct Timing: Sendable {
        public let start_s: Double
        public let end_s: Double
    }
    public struct Row: Sendable {
        public let text: String
        public let timing: Timing?
    }
    public static let maximumCharacters = 320

    public static func rows(_ transcript: Transcript) -> [Row] {
        let texts = chunks(transcript.text)
        // Readability does not depend on successful alignment; only seek buttons do.
        let plain = texts.map { Row(text: $0, timing: nil) }
        guard ["completed", "success"].contains(transcript.alignment_status), !transcript.segments.isEmpty else { return plain }
        let source = normalized(transcript.text)
        guard !source.isEmpty else { return plain }
        var aligned: [Character] = []
        var owners: [Int] = []
        var previousStart = 0.0
        var previousEnd = 0.0
        for (index, segment) in transcript.segments.enumerated() {
            guard segment.start_s.isFinite, segment.end_s.isFinite,
                  segment.start_s >= previousStart, segment.end_s >= previousEnd,
                  segment.end_s >= segment.start_s else { return plain }
            previousStart = segment.start_s
            previousEnd = segment.end_s
            let characters = normalized(segment.text)
            aligned.append(contentsOf: characters)
            owners.append(contentsOf: repeatElement(index, count: characters.count))
        }
        // Consume characters in order, never search for repeated substrings or interpolate times.
        guard source == aligned else { return plain }
        var consumed = 0
        return texts.map { text in
            let count = normalized(text).count
            defer { consumed += count }
            guard count > 0 else { return Row(text: text, timing: nil) }
            let first = transcript.segments[owners[consumed]]
            let last = transcript.segments[owners[consumed + count - 1]]
            return Row(text: text, timing: Timing(start_s: first.start_s, end_s: last.end_s))
        }
    }

    private static func normalized(_ text: String) -> [Character] {
        Array(text.precomposedStringWithCanonicalMapping.filter { character in
            !character.unicodeScalars.allSatisfy {
                CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.punctuationCharacters.contains($0)
            }
        })
    }

    private static func chunks(_ text: String) -> [String] {
        let tokenizer = NLTokenizer(unit: .sentence)
        tokenizer.string = text
        var boundaries: Set<String.Index> = [text.endIndex]
        func afterWhitespace(_ index: String.Index) -> String.Index {
            var end = index
            while end < text.endIndex && text[end].isWhitespace { end = text.index(after: end) }
            return end
        }
        tokenizer.enumerateTokens(in: text.startIndex..<text.endIndex) { range, _ in
            boundaries.insert(afterWhitespace(range.upperBound))
            return true
        }
        for index in text.indices where text[index].isNewline {
            boundaries.insert(afterWhitespace(text.index(after: index)))
        }
        var result: [String] = []
        var start = text.startIndex
        for boundary in boundaries.sorted() where boundary > start {
            // Retain every original character, including spacing between tokenizer ranges.
            while start < boundary {
                var end = text.index(start, offsetBy: maximumCharacters, limitedBy: boundary) ?? boundary
                if end < boundary, let space = text[start..<end].lastIndex(where: { $0.isWhitespace }) {
                    end = text.index(after: space)
                }
                result.append(String(text[start..<end]))
                start = end
            }
        }
        return result
    }
}
