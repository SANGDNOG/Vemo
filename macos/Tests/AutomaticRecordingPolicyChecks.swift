import Foundation
import NativeCore

@main struct AutomaticRecordingPolicyChecks {
    static func check(_ value: Bool, _ message: String) throws {
        if !value { throw NSError(domain:message,code:1) }
        print("PASS: " + message)
    }
    static func main() throws {
        let data = Data(#"{"id":"a","uuid":"a","status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed","artifact_path":"/owned/transcript.json","audio_path":"/owned/audio.m4a"}"#.utf8)
        var job = try JSONDecoder().decode(Job.self,from:data)
        try check(AutomaticRecordingPolicy.needsNotes(job),"legacy completed transcript requests missing notes")
        job.status = "needs_recording_date"; job.notes_status = "pending"
        try check(AutomaticRecordingPolicy.needsNotes(job),"unknown recording date does not block notes")
        job.notes_status = "failed"
        try check(!AutomaticRecordingPolicy.needsNotes(job),"failed notes never automatically retry")
        job.notes_status = "running"
        try check(!AutomaticRecordingPolicy.needsNotes(job),"running notes are not resubmitted")
        job.notes_status = "completed"; job.notes_path = "/owned/notes.json"
        try check(AutomaticRecordingPolicy.zipEligible(job),"completed notes with all three inputs export without invented date")
        for status in ["waiting","paused","noting","transcribing","failed","unprocessed"] {
            job.status = status
            try check(!AutomaticRecordingPolicy.zipEligible(job) && !AutomaticRecordingPolicy.needsNotes(job),"in-progress/failed \(status) not automatically scheduled")
        }
        job.status = "completed"; job.deleted_at = "2026-10-13"
        try check(!AutomaticRecordingPolicy.zipEligible(job),"trashed recording excluded")
        job.deleted_at = nil
        let original = job
        job.audio_path = nil
        try check(!AutomaticRecordingPolicy.zipEligible(job),"audio path required")
        job = original; job.notes_path = nil
        try check(!AutomaticRecordingPolicy.zipEligible(job),"notes path required")
        job = original; job.artifact_path = nil
        try check(!AutomaticRecordingPolicy.zipEligible(job),"transcript path required")
        job = original
        let key = AutomaticRecordingPolicy.zipKey(job,destination:"/one")
        try check(key != AutomaticRecordingPolicy.zipKey(job,destination:"/two"),"destination change triggers check")
        job.topic = "renamed"
        try check(key != AutomaticRecordingPolicy.zipKey(job,destination:"/one"),"title change triggers check")
        job = original; job.notes_path = "/owned/new-notes.json"
        try check(key != AutomaticRecordingPolicy.zipKey(job,destination:"/one"),"new notes path triggers check")
    }
}
