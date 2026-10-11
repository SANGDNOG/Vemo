from pathlib import Path
base=Path(__file__).resolve().parents[3]
output_dir=base/"local-validation/selection-checks"
output_dir.mkdir(parents=True,exist_ok=True)
s=(base/'macos/Sources/VoiceMemoTranscriber/AppModel.swift').read_text()
start=s.index('    func loadSelection() async {')
end=s.index('    func togglePlayback()',start)
method=s[start:end]
output='''import Foundation
import AVFoundation

actor Gate {
 var continuation: CheckedContinuation<Transcript,Error>?
 func blocked() -> Bool { continuation != nil }
 func read() async throws -> Transcript { try await withCheckedThrowingContinuation { continuation = $0 } }
 func finish(success:Bool, value:Transcript) { if success {continuation?.resume(returning:value)} else {continuation?.resume(throwing:NativeFailure.invalid("synthetic stale read failure"))};continuation=nil }
}
enum DemoContent { static let enabled=false }
@MainActor final class SelectionHarness {
 var selected:Job?
 var player:AVAudioPlayer?
 var loadedAudioKey:String?
 var loadedSelection:String?
 var transcript:Transcript?
 var notes:RecordingNotes?
 var notesReadError:String?
 var playing=false
 var currentTime=0.0
 var audioDuration=0.0
 var quoteRow:Int?
 var focusedQuote:String?
 var topic=""
 var dateInput=""
 var problem:String?
 var loadID=UUID()
 let stateRoot=URL(fileURLWithPath:FileManager.default.currentDirectoryPath).appendingPathComponent("local-validation/emergency-review")
 let gate=Gate()
 let bTranscript:Transcript
 let bNotes:RecordingNotes
 init(_ t:Transcript,_ n:RecordingNotes){bTranscript=t;bNotes=n}
 func loadDemoSelection(){fatalError("not used")}
 func readTranscript(_ path:String) async throws -> Transcript { if path=="blocked-A" {return try await gate.read()};return bTranscript }
 func readNotes(_ path:String,text:String) async throws -> RecordingNotes{return bNotes}
'''+method+'''}
func job(_ id:String,_ artifact:String,_ notes:String?) throws -> Job {
 var obj:[String:Any]=["id":id,"uuid":id,"status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed","artifact_path":artifact]
 if let notes{obj["notes_path"]=notes}
 return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:obj))
}
@main struct Race {
 @MainActor static func main() async throws {
  var failures:[String]=[]
  let t=try JSONDecoder().decode(Transcript.self,from:Data(#"{"text":"B source","segments":[],"alignment_status":"failed"}"#.utf8))
  let n=try JSONDecoder().decode(RecordingNotes.self,from:Data(#"{"schema_version":1,"source_sha256":"synthetic","summary":[],"highlights":[{"quote":"B source"}],"topics":[]}"#.utf8))
  for success in [true,false]{
   let model=SelectionHarness(t,n);model.selected=try job("A","blocked-A",nil)
   let old=Task{@MainActor in await model.loadSelection()}
   while !(await model.gate.blocked()){await Task.yield()}
   model.selected=try job("B","B","B-notes");await model.loadSelection()
   print("RACE before stale finish: selection=\\(model.selected!.id) notesPresent=\\(model.notes != nil)")
   await model.gate.finish(success:success,value:t);await old.value
   if model.notes == nil || model.problem != nil { failures.append("stale result changed current notes/error") }
   print("RACE staleReadSuccess=\\(success) selection=\\(model.selected!.id) transcript=\\(model.transcript!.text) notesPresent=\\(model.notes != nil) error=\\(model.problem ?? "none")")
  }
  let sameJob=SelectionHarness(t,n);sameJob.selected=try job("B","B","B-notes");await sameJob.loadSelection()
  var refreshed=sameJob.selected!;refreshed.artifact_path=nil;refreshed.notes_path=nil;refreshed.status="failed";sameJob.selected=refreshed;await sameJob.loadSelection()
  let eligible=sameJob.transcript != nil && sameJob.selected.map{["completed","needs_recording_date","failed"].contains($0.status)} == true
  if sameJob.transcript != nil || eligible { failures.append("nil artifact retained old transcript") }
  print("REFRESH same job artifactPath=nil retainedTranscript=\\(sameJob.transcript != nil) notesGenerateEnabled=\\(eligible)")
  guard failures.isEmpty else { throw NativeFailure.invalid(failures.joined(separator:"; ")) };print("PASS: selection success/failure race and same-job transcript invalidation")
 }
}
'''
(output_dir/'selection.swift').write_text(output)
print('Copied loadSelection unchanged; replaced only surrounding dependencies and synthetic read boundary.')

import subprocess
sources=[base/'macos/Sources/NativeCore'/name for name in ('WireTypes.swift','Validation.swift','RecordingNotes.swift','TranscriptDisplay.swift')]
subprocess.run(['swiftc','-parse-as-library',*[str(p) for p in sources],str(output_dir/'selection.swift'),'-o',str(output_dir/'selection')],check=True)
subprocess.run([str(output_dir/'selection')],check=True,cwd=base)
