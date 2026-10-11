import Foundation
public enum TranscriptExport {
    public static func render(_ transcript: Transcript, title: String, format: String) throws -> String {
        switch format {
        case "txt": return title + "\n\n" + transcript.text + "\n"
        case "md": return "# " + title + "\n\n" + transcript.text + "\n"
        case "srt", "vtt":
            guard ["completed", "success"].contains(transcript.alignment_status) else { throw NativeFailure.invalid("시간 정렬이 완료되지 않아 자막을 내보낼 수 없습니다.") }
            return (format == "vtt" ? "WEBVTT\n\n" : "") + (try transcript.segments.enumerated().map { i, s in
                guard s.start_s.isFinite, s.end_s.isFinite, s.start_s >= 0, s.end_s >= s.start_s else { throw NativeFailure.invalid("유효하지 않은 자막 시각입니다.") }
                return "\(format == "srt" ? "\(i+1)\n" : "")\(timestamp(s.start_s, comma: format == "srt")) --> \(timestamp(s.end_s, comma: format == "srt"))\n\(s.text)\n"
            }).joined(separator: "\n")
        default: throw NativeFailure.invalid("지원하지 않는 내보내기 형식입니다.")
        }
    }
    private static func timestamp(_ seconds: Double, comma: Bool) -> String {
        let ms = Int(max(0, min(seconds, Double(Int.max / 1000))) * 1000)
        return String(format: "%02d:%02d:%02d%@%03d", ms/3600000, (ms/60000)%60, (ms/1000)%60, comma ? "," : ".", ms%1000)
    }
}
