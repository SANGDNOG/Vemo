import Foundation
import NativeCore
import CryptoKit

func notesChecks() throws {
    let text = "다음 주 프로젝트를 시작합니다. The API starts Monday. 금요일에 검토합니다."
    let hash = SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    let data = try JSONSerialization.data(withJSONObject: ["schema_version":1,"source_sha256":hash,
        "summary":[["text":"다음 주 시작", "quotes":["다음 주 프로젝트를 시작합니다."]]],
        "highlights":[["quote":"The API starts Monday."]],
        "topics":[["keyword":"API", "summary":"월요일 시작", "quotes":["The API starts Monday."]]]])
    let notes = try JSONDecoder().decode(RecordingNotes.self, from:data)
    let checked = try NotesDisplay.validated(notes, text:text)
    try check(checked.summary.count == 1 && checked.topics.first?.keyword == "API", "notes validate actual Korean/English citations")
    try rejects("notes from another transcript rejected") { _ = try NotesDisplay.validated(notes, text:"changed") }
    var fake = notes; fake.highlights = [try JSONDecoder().decode(RecordingNotes.Highlight.self, from:Data(#"{"quote":"꾸며낸 발언입니다."}"#.utf8))]
    try check(try NotesDisplay.validated(fake, text:text).highlights.isEmpty, "fabricated highlight rejected")
    let transcript = try JSONDecoder().decode(Transcript.self, from:JSONSerialization.data(withJSONObject:["text":text,"segments":[["text":text,"start_s":3.0,"end_s":9.0]],"alignment_status":"completed"]))
    let target = NotesDisplay.quoteTarget("The API starts Monday.", in:transcript)
    try check(target != nil && target?.timing?.start_s == 3, "exact quote maps to existing row and raw bounds")
    let repeated = try JSONDecoder().decode(Transcript.self, from:Data(#"{"text":"반복입니다. 반복입니다.","segments":[{"text":"반복입니다.","start_s":1,"end_s":2},{"text":"반복입니다.","start_s":2,"end_s":3}],"alignment_status":"completed"}"#.utf8))
    try check(NotesDisplay.quoteTarget("반복입니다.", in:repeated)?.timing == nil, "ambiguous repeated quote does not invent timestamp")
    let overlapping = try JSONDecoder().decode(Transcript.self,from:Data(#"{"text":"가가가.","segments":[{"text":"가가가.","start_s":1,"end_s":3}],"alignment_status":"completed"}"#.utf8))
    try check(NotesDisplay.quoteTarget("가가", in:overlapping)?.timing == nil, "overlapping repeated quote has no invented timestamp")
    try check(NotesDisplay.quoteTarget("없는 인용", in:transcript) == nil, "unmatched quote has no transcript destination")
    let markdown = NotesDisplay.markdown(checked, title:"프로젝트 — 2026-10-10")
    try check(markdown.contains("# 프로젝트 — 2026-10-10") && markdown.contains("핵심 요약") && markdown.contains("강조 문장") && markdown.contains("### API") && markdown.contains("> The API starts Monday."), "notes export includes date, sections and exact quotes")
    let legacy = try JSONDecoder().decode(Job.self,from:Data(#"{"id":"old","uuid":"old","status":"completed","stage":"completed","elapsed_seconds":2,"title_status":"completed","alignment_status":"completed"}"#.utf8))
    try check(legacy.notes_path == nil && legacy.notes_status == nil, "legacy jobs decode without note fields")
}

func notesPublicationChecks(root:URL) async throws {
    let state = root.appendingPathComponent("note-publication-state")
    let local = state.appendingPathComponent("artifacts/notes-job/versions/notes-six-files")
    let output = root.appendingPathComponent("Drive/Notes")
    try FileManager.default.createDirectory(at:local,withIntermediateDirectories:true)
    let title = "노트 예시 — 2026-10-10"
    let names = [title+".txt",title+".md",title+".srt",title+".vtt",title+" 노트.md",title+" 노트.json"]
    var files:[[String:Any]] = []
    for name in names {
        let data = Data((name+"\nsynthetic content\n").utf8), url = local.appendingPathComponent(name)
        try data.write(to:url); files.append(["name":name,"path":url.path,"sha256":digest(data)])
    }
    let manifest:[String:Any] = ["schema_version":1,"publication_id":"notes-six-files","job_id":"notes-job","uuid":"notes-memo","recording_date":"2026-10-10","title":title,"revision":["number":1],"files":files]
    let path = local.appendingPathComponent("manifest.json"); try JSONSerialization.data(withJSONObject:manifest).write(to:path)
    let publisher = PublishBroker(stateRoot:state)
    let receipt = try await publisher.publish(manifestPath:path.path,jobID:"notes-job",publicationID:"notes-six-files",outputRoot:output)
    let version = try await publisher.activeRevision(uuid:"notes-memo",recordingDate:"2026-10-10",outputRoot:output)
    try check(receipt.artifact_hashes.count == 6 && Set(receipt.artifact_hashes.keys) == Set(names), "notes and transcripts publish in the same six-file local Drive revision")
    try check(try Data(contentsOf:version.appendingPathComponent(title+" 노트.md")) == Data((title+" 노트.md\nsynthetic content\n").utf8), "published note content is preserved exactly")
    let replay = try await publisher.publish(manifestPath:path.path,jobID:"notes-job",publicationID:"notes-six-files",outputRoot:output)
    try check(replay.artifact_hashes == receipt.artifact_hashes && replay.output_manifest_path == receipt.output_manifest_path, "note-inclusive publication replays exact durable receipt")
}
