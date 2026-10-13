import Foundation
import NativeCore

@main struct TrashRecoveryChecks {
    static func check(_ value: Bool, _ message: String) throws {
        if !value { throw NSError(domain:message,code:1) }
        print("PASS: " + message)
    }
    static func job(_ id: String, deleted: String? = nil) throws -> Job {
        var fields: [String:Any] = ["id":id,"uuid":id,"original_title":"Recording " + id,"status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed"]
        if let deleted { fields["deleted_at"] = deleted }
        return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:fields))
    }
    static func main() async throws {
        let active = try job("a"), deleted = try job("b",deleted:"2026-10-13T00:00:00Z")
        try check(active.deleted_at == nil,"legacy jobs decode without deletion field")
        let jobs = [active,deleted]
        for filter in ["all","folder","unfiled"] {
            let assignments = filter == "folder" ? ["a":"folder","b":"folder"] : [:]
            let visible = LibraryQuery.visible(jobs,assignments:assignments,filter:filter,search:"",index:[:])
            try check(visible.map(\.id) == ["a"],"normal \(filter) excludes deleted")
        }
        try check(LibraryQuery.visible(jobs,assignments:["b":"folder"],filter:"trash",search:"",index:[:]).map(\.id) == ["b"],"trash includes deleted regardless of old folder")
        try check(LibraryQuery.visible(jobs,assignments:[:],filter:"all",search:"private match",index:["b":"private match"]).isEmpty,"deleted content excluded from normal search")
        var restored = deleted; restored.deleted_at = nil
        try check(LibraryQuery.visible([restored],assignments:[:],filter:"trash",search:"",index:[:]).isEmpty,"restored item leaves trash")
        let memoData = Data(#"{"uuid":"a","recorded_at_source":"unknown","completion_evidence":"finished","source_kind":"import","source_reference":"import:a"}"#.utf8)
        var memo = try JSONDecoder().decode(Memo.self,from:memoData)
        try check(memo.metadata_revision == nil,"legacy memo metadata revision optional")
        memo.metadata_revision = "42"
        let roundtrip = try JSONDecoder().decode(Memo.self,from:JSONEncoder().encode(memo))
        try check(roundtrip.metadata_revision == "42","memo metadata revision wire roundtrip")
        let root = URL(fileURLWithPath:FileManager.default.currentDirectoryPath).appendingPathComponent("macos/.build/TrashRecovery-" + UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at:root) }
        let audio = root.appendingPathComponent("synthetic.wav")
        try Data("fixture".utf8).write(to:audio)
        let watcher = FolderWatcher(), now = Date()
        _ = try await watcher.scan(in:root,now:now)
        let ready = try await watcher.scan(in:root,now:now.addingTimeInterval(3))
        try check(ready.files == [audio],"stable file offered")
        await watcher.accepted(audio)
        let accepted = try await watcher.scan(in:root,now:now.addingTimeInterval(4))
        try check(accepted.files.isEmpty,"accepted file suppressed")
        await watcher.retryFiles()
        let retry = try await watcher.scan(in:root,now:now.addingTimeInterval(5))
        try check(retry.files == [audio],"force retry reoffers accepted file without losing stability")
        await watcher.rejected(audio); await watcher.retryFiles()
        let rejectedRetry = try await watcher.scan(in:root,now:now.addingTimeInterval(6))
        try check(rejectedRetry.files == [audio] && rejectedRetry.unavailableCount == 0,"force retry clears rejected state")
        try Data("fixture changed".utf8).write(to:audio)
        await watcher.retryFiles()
        let changing = try await watcher.scan(in:root,now:now.addingTimeInterval(7))
        try check(changing.files.isEmpty && changing.changingCount == 1,"force retry still waits for changed files to stabilize")
    }
}
