import Foundation
import NativeCore
struct ModelProgress {
    static func label(_ state: String) -> String {
        switch state {
        case "resolving": return "다운로드 파일 확인 중"
        case "verifying": return "모델 파일 검증 중"
        case "downloading", "download", "running": return "모델 다운로드 중"
        case "stalled", "stall", "waiting": return "다운로드 응답을 기다리는 중"
        case "ready", "completed", "success": return "모델 준비됨"
        case "failed", "error", "invalid", "missing_or_invalid": return "모델 다운로드 또는 검증 실패 · 설정에서 재개하세요"
        case "cancelled", "interrupted": return "모델 다운로드 중단 · 설정에서 재개하세요"
        default: return "모델 준비 중"
        }
    }
    static func description(_ payload: [String:JSONValue]) -> String {
        let state = label(payload["state"]?.string ?? "")
        guard let bytes = payload["downloaded_bytes"]?.numeric, bytes.isFinite, bytes >= 0, bytes < Double(Int64.max) else { return state }
        let downloaded = ByteCountFormatter.string(fromByteCount:Int64(bytes),countStyle:.file)
        if let total = payload["total_bytes"]?.numeric, total.isFinite, total > 0, total < Double(Int64.max) { return state + " · " + downloaded + " / " + ByteCountFormatter.string(fromByteCount:Int64(total),countStyle:.file) }
        return state + " · " + downloaded
    }
}
