import Foundation
import NativeCore

/// Documentation-only fixtures. No source broker, engine or user's queue is opened.
enum DemoContent {
    static var enabled: Bool { ProcessInfo.processInfo.arguments.contains("--demo") || Bundle.main.object(forInfoDictionaryKey:"VemoDemoMode") as? Bool == true }
    static let jobs: [Job] = {
        let base: [String:JSONValue] = ["uuid":.string("demo"),"stage":.string("completed"),"elapsed_seconds":.number(36),"title_status":.string("completed"),"alignment_status":.string("completed")]
        let rows: [[String:JSONValue]] = [
            ["id":.string("demo-completed"),"topic":.string("프로젝트 일정과 API 연동 계획"),"recording_date":.string("2026-10-10"),"recorded_at":.string("2026-10-10T09:00:00+09:00"),"duration_s":.number(180),"status":.string("completed")],
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
}
