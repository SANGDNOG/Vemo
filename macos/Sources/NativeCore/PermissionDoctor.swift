import Foundation
import Darwin
public struct PermissionDiagnostic: Codable, Sendable {
    public var schema_version = 1
    public var checked_at: String
    public var status: String
    public var error_code: Int32?
    public var directory_readable: Bool
    public var database_readable: Bool
    public var schema_verified = false
    public var message: String
}
public enum PermissionDoctor {
    public static func check() -> PermissionDiagnostic {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Group Containers/group.com.apple.VoiceMemos.shared/Recordings")
        var result = PermissionDiagnostic(checked_at: ISO8601DateFormatter().string(from: Date()), status: "unknown", error_code: nil, directory_readable: false, database_readable: false, message: "")
        do { _ = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil); result.directory_readable = true }
        catch { let e = error as NSError; result.status = "access_failed"; result.error_code = Int32(e.code); result.message = "음성 메모 저장소를 읽지 못했습니다. 앱의 전체 디스크 접근 권한과 Mac 음성 메모 동기화를 확인하세요."; return result }
        let fd = open(folder.appendingPathComponent("CloudRecordings.db").path, O_RDONLY | O_NOFOLLOW)
        if fd >= 0 { close(fd); result.database_readable = true; result.status = "readable_unverified_schema"; result.message = "DB 파일을 읽기 전용으로 열었습니다. 스키마·녹음일·동기화는 아직 검증되지 않았습니다." }
        else { result.error_code = errno; result.status = errno == EACCES || errno == EPERM ? "permission_denied" : "database_unavailable"; result.message = "음성 메모 DB를 열지 못했습니다. 권한 또는 저장소 경로를 확인하세요." }
        return result
    }
}
