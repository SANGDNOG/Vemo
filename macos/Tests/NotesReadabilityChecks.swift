import Foundation
import CryptoKit
import NativeCore

@main struct NotesReadabilityChecks {
    static func main() throws {
        let checks = NotesReadabilityChecks()
        try checks.validationPreservesUpTo64Citations()
        try checks.exportKeepsLegacyItemsAndExactEvidenceInFinalDisclosure()
        print("PASS: notes readability export and up to 64 citation validation")
    }
    private func expect(_ condition: Bool, line: Int = #line) throws {
        if !condition { throw NSError(domain:"NotesReadabilityChecks",code:line) }
    }
    private func notes(quotes: [String], count: Int = 1) throws -> RecordingNotes {
        let text = quotes.joined(separator:"\n")
        let hash = SHA256.hash(data:Data(text.utf8)).map { String(format:"%02x",$0) }.joined()
        return try JSONDecoder().decode(RecordingNotes.self,from:JSONSerialization.data(withJSONObject:[
            "schema_version":1,"source_sha256":hash,
            "summary":(1...count).map { ["text":"요약 \($0)","quotes":quotes] },
            "topics":(1...count).map { ["keyword":"주제 \($0)","summary":"내용 \($0)","quotes":quotes] },
            "highlights":quotes.map { ["quote":$0] }
        ]))
    }

    func validationPreservesUpTo64Citations() throws {
        for count in [5,8,9,64] {
            let quotes = (1...count).map { "정확한 원문 \($0)." }
            let checked = try NotesDisplay.validated(notes(quotes:quotes),text:quotes.joined(separator:"\n"))
            try expect(checked.summary.first?.quotes == quotes)
            try expect(checked.topics.first?.quotes == quotes)
        }
        let nine = (1...9).map { "인용 \($0)." }
        let checkedNine = try NotesDisplay.validated(notes(quotes:nine),text:nine.joined(separator:"\n"))
        let markdown = NotesDisplay.markdown(checkedNine,title:"9개 근거")
        for (index,quote) in nine.enumerated() {
            try expect(markdown.contains("[근거 \(index + 1)](#evidence-\(index + 1))"))
            try expect(markdown.contains("> " + quote))
        }
        let quotes = (1...65).map { "정확한 원문 \($0)." }
        let checked = try NotesDisplay.validated(notes(quotes:quotes),text:quotes.joined(separator:"\n"))
        try expect(checked.summary.first?.quotes == Array(quotes.prefix(64)))
        try expect(checked.topics.first?.quotes == Array(quotes.prefix(64)))
    }

    func exportKeepsLegacyItemsAndExactEvidenceInFinalDisclosure() throws {
        let quotes = ["  공백을 보존한 원문.  ","첫 줄\r\n\r\n둘째 줄 <tag> & 원문",String(repeating:"긴 원문 ",count:150)]
        let markdown = NotesDisplay.markdown(try notes(quotes:quotes,count:9),title:"테스트 녹음")
        let parts = markdown.components(separatedBy:"## 원문 근거\n")
        try expect(parts.count == 2)
        let overview = parts[0], appendix = parts[1]
        try expect(!overview.contains("> "))
        try expect(overview.contains("- 요약 9") && overview.contains("### 주제 9") && overview.contains("- 내용 9"))
        try expect(appendix.contains("<details>\n<summary>원문 근거 보기</summary>\n\n"))
        try expect(overview.contains("<summary>중요한 원문 보기</summary>"))
        try expect(overview.contains("[근거 1](#evidence-1)"))
        for index in 1...quotes.count {
            try expect(appendix.components(separatedBy:"<a id=\"evidence-\(index)\"></a>").count == 2)
        }
        try expect(!appendix.contains("evidence-4"))
        for quote in quotes {
            let quoted = "> " + quote.replacingOccurrences(of:"\n",with:"\n> ")
            try expect(appendix.components(separatedBy:quoted).count == 2)
        }
        try expect(markdown.hasSuffix("</details>\n"))
    }
}
