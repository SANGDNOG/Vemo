import Foundation
import NativeCore
import CryptoKit

/// Documentation-only fixtures. No source broker, engine or user's queue is opened.
enum DemoContent {
    static var enabled: Bool { ProcessInfo.processInfo.arguments.contains("--demo") || Bundle.main.object(forInfoDictionaryKey:"VemoDemoMode") as? Bool == true }
    static let jobs: [Job] = {
        let base: [String:JSONValue] = ["uuid":.string("demo"),"stage":.string("completed"),"elapsed_seconds":.number(36),"title_status":.string("completed"),"alignment_status":.string("completed")]
        let rows: [[String:JSONValue]] = [
            ["id":.string("demo-completed"),"notes_status":.string("completed"),"topic":.string("프로젝트 일정과 API 연동 계획"),"recording_date":.string("2026-10-10"),"recorded_at":.string("2026-10-10T09:00:00+09:00"),"duration_s":.number(180),"status":.string("completed")],
            ["id":.string("demo-empty"),"notes_status":.string("pending"),"topic":.string("노트 작성 전 회의"),"recording_date":.string("2026-10-10"),"recorded_at":.string("2026-10-10T14:00:00+09:00"),"duration_s":.number(180),"status":.string("completed"),"artifact_path":.string("demo-transcript")],
            ["id":.string("demo-progress"),"original_title":.string("제품 스프린트 회의"),"recording_date":.string("2026-10-09"),"recorded_at":.string("2026-10-09T15:00:00+09:00"),"duration_s":.number(2541),"status":.string("transcribing"),"stage":.string("transcribing"),"completed_units":.integer(4),"total_units":.integer(9),"elapsed_seconds":.number(82)],
            ["id":.string("demo-date"),"original_title":.string("공유한 오디오 파일"),"status":.string("needs_recording_date"),"stage":.string("publish")]
        ]
        return rows.map { try! JSONValue.object(base.merging($0,uniquingKeysWith: { _,new in new })).decoded(Job.self) }
    }()
    static func transcript(for id: String) -> Transcript? {
        guard id != "demo-date" else { return nil }
        let sentences=["다음 주 월요일에 프로젝트를 시작하고, 첫 주에는 요구사항과 담당자를 정리하기로 했습니다. ","The API integration starts on Monday. ","금요일에는 프로토타입을 함께 검토하고 다음 스프린트의 범위를 결정하겠습니다."]
        let segments=sentences.enumerated().map { index,text in JSONValue.object(["start_s":.number(Double(index)*7),"end_s":.number(Double(index+1)*7),"text":.string(text)]) }
        return try! JSONValue.object(["text":.string(sentences.joined()),"segments":.array(segments),"alignment_status":.string("completed")]).decoded(Transcript.self)
    }
    static func notes(for id:String) -> RecordingNotes? {
        guard id == "demo-completed", let transcript = transcript(for:id) else { return nil }
        let start = "다음 주 월요일에 프로젝트를 시작하고, 첫 주에는 요구사항과 담당자를 정리하기로 했습니다."
        let api = "The API integration starts on Monday."
        let review = "금요일에는 프로토타입을 함께 검토하고 다음 스프린트의 범위를 결정하겠습니다."
        let hash = SHA256.hash(data:Data(transcript.text.utf8)).map { String(format:"%02x",$0) }.joined()
        let fixture:[String:Any] = ["schema_version":1,"source_sha256":hash,
            "summary":[["text":"월요일에 프로젝트와 API 연동을 시작하고 금요일에 프로토타입을 검토합니다.","quotes":[start,api,review]]],
            "highlights":[["quote":api],["quote":review]],
            "topics":[["keyword":"프로젝트 일정","summary":"첫 주에 요구사항과 담당자를 정리합니다.","quotes":[start]],
                      ["keyword":"API","summary":"API 연동은 월요일에 시작합니다.","quotes":[api]],
                      ["keyword":"프로토타입","summary":"금요일에 프로토타입 검토 후 다음 스프린트 범위를 정합니다.","quotes":[review]]]]
        return try! JSONDecoder().decode(RecordingNotes.self,from:JSONSerialization.data(withJSONObject:fixture))
    }

}
