#!/usr/bin/env python3
"""Shipping selection/drag/purge coordinator with synthetic jobs and temporary folder store."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
ui=root/'macos/Sources/VoiceMemoTranscriber'
s=(ui/'AppModel.swift').read_text()
def block(marker):
 start=s.index(marker); opening=s.index('{',start);depth=0
 for end in range(opening,len(s)):
  if s[end]=='{':depth+=1
  elif s[end]=='}':
   depth-=1
   if not depth:return s[start:end+1].replace('private func','func').replace('@Published ','')
 raise ValueError(marker)
markers=['sendRecordingCommand','setRecordingSelection','selectRecording','selectAllRecordings','recordingActionIDs','reconcileLibrarySelection','changeLibrary','moveRecordings','beginRecordingDrag','finishRecordingDrag','recordingDrop','libraryDragPayload','canDropLibraryItem','dropLibraryItem','requestRecordingAction','confirmRecordingAction','removeRecordingState','cancelTitleEdit','recordingCommandPending','cancelObsoleteExport']
methods='\n'.join(block(('    private func ' if '    private func '+name+'(' in s else '    func ')+name+'(') for name in markers)
harness=r'''
import AppKit
import NativeCore
import Foundation
/* CONFIRMATION */
enum DemoContent { static let enabled = false }
actor FakeEngine {
 var sent:[WireCommand] = []
 func send(_ command:WireCommand) throws { sent.append(command) }
 func commands() -> [WireCommand] { sent }
}
@MainActor final class Fixture {
 var jobs:[Job] = []
 var nativeSnapshotTasks:[String:[UUID:Task<Void,Never>]] = [:]
 var visibleJobs:[Job] { jobs.filter { $0.deleted_at == nil } }
 var activeJobs:[Job] { visibleJobs }
 var selectedIDs:Set<String> = [], selectionAnchor:String?, updatingSelection = false
 var editingTitleJobID:String?, editingTitleLocation:String?, titleDraft = ""
 var recordingDragGroups:[String:(origin:String,ids:[String])] = [:]
 let libraryDragSessionID = "test-session"
 var library = LibraryOrganization(), libraryReady = true, libraryBusy = false, libraryFilter = "all"
 let libraryStore:LibraryFolderStore
 var libraryWriteTask:Task<Void,Never>?, problem:String?
 var recordingBatchBusy = false, connected = true
 var recordingConfirmation:RecordingBulkConfirmation?
 let engine = FakeEngine()
 var transcriptIndex:[String:String] = [:]
 var refreshPendingJobs:Set<String> = [], refreshRequests:[String:String] = [:], pendingRecordingCommands:[String:String] = [:]
 var automaticNotesRequested:Set<String> = [], automaticNotesRetryAfter:[String:Date] = [:], automaticNoteRequests:[String:String] = [:]
 var zipChecked:Set<String> = [], zipRetryAfter:[String:Date] = [:]
 var activeExportTask:Task<URL?,Error>?, activeExportJobID:String?, activeExportKey:String?, zipExportFolder = "/fixture"
 init(root:URL) { libraryStore = LibraryFolderStore(root:root) }
 func scheduleAutomaticOutputs() {}
 /* SELECTION */
 /* METHODS */
}
/* POLICY */
@main struct Checks {
 @MainActor static func main() async throws {
  NSApplication.shared.setActivationPolicy(.prohibited)
  func check(_ ok:Bool,_ name:String) throws { guard ok else { throw NSError(domain:name,code:1) };print("PASS: "+name) }
  func job(_ id:String) throws -> Job {
   let data:[String:Any] = ["id":id,"uuid":id,"original_title":id,"status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed"]
   return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:data))
  }
  let tmp=FileManager.default.temporaryDirectory.appendingPathComponent("VemoBulk-"+UUID().uuidString)
  defer { try? FileManager.default.removeItem(at:tmp) }
  let m=Fixture(root:tmp); m.jobs=try ["a","b","c","d"].map(job)
  m.selectRecording("b")
  try check(m.selectedIDs == ["b"] && m.selection == "b","ordinary click selects one primary")
  m.selectRecording("d",modifiers:.command)
  try check(m.selectedIDs == ["b","d"] && m.selection == "d","Cmd click adds and updates primary")
  m.selectRecording("d",modifiers:.command)
  try check(m.selectedIDs == ["b"] && m.selection == "b","Cmd click removes and retains valid primary")
  m.selectRecording("b"); m.selectRecording("d",modifiers:.shift)
  try check(m.selectedIDs == ["b","c","d"],"Shift click selects visible range")
  m.selectRecording("a",modifiers:.shift)
  try check(m.selectedIDs == ["a","b"],"Shift range uses original anchor")
  m.selectAllRecordings()
  try check(m.selectedIDs == ["a","b","c","d"],"select all covers visible recordings")
  m.selectRecording("a"); m.selectRecording("c",modifiers:.command)
  try check(m.recordingActionIDs("a") == ["a","c"],"context on selected row targets exact selection")
  try check(m.recordingActionIDs("b") == ["b"],"context on unselected row never targets other selection")
  m.editingTitleJobID="a";m.titleDraft="draft";m.setRecordingSelection(["a","b"])
  try check(m.editingTitleJobID == nil,"multiselection cancels inline editing")
  m.setRecordingSelection(["a","c"]);m.jobs[2].deleted_at="now";m.reconcileLibrarySelection()
  try check(m.selectedIDs == ["a"],"selection reconciliation excludes hidden/trash rows")
  m.jobs[2].deleted_at=nil
  m.library=try await m.libraryStore.create(name:"Target")
  let folder=m.library.folders[0].id
  try check(m.jobs[0].purge_pending == nil,"legacy job decodes without purge_pending")
  m.setRecordingSelection(["a","b"])
  let drag=m.beginRecordingDrag("a")
  m.setRecordingSelection(["c"])
  try check(try m.recordingDrop(drag,on:.folder(folder))?.ids == ["a","b"],"drag freezes selected IDs at drag start")
  m.jobs[1].deleted_at="now"
  try check(!m.canDropLibraryItem(drag,on:.folder(folder)),"one trashed member rejects entire group drop")
  m.jobs[1].deleted_at=nil
  let foreign=LibraryDragPayload(kind:.recording,id:"a",sessionID:"foreign",dragID:drag.dragID)
  try check(!m.canDropLibraryItem(foreign,on:.folder(folder)),"foreign session cannot reuse captured group")
  try check(m.dropLibraryItem(drag,on:.folder(folder)),"group drop accepted")
  await m.libraryWriteTask?.value
  try check(m.library.assignments["a"] == folder && m.library.assignments["b"] == folder && m.library.assignments["c"] == nil,"group drop assigns only captured recordings")
  try check(!m.canDropLibraryItem(drag,on:.unfiled),"completed drag token cannot replay")
  m.setRecordingSelection(["a","c"]);let mixed=m.beginRecordingDrag("a")
  try check(m.dropLibraryItem(mixed,on:.folder(folder)),"group drop works when anchor already belongs to destination")
  await m.libraryWriteTask?.value
  try check(m.library.assignments["c"] == folder,"group drop moves remaining member in one transaction")
  m.moveRecordings(["a","c"],to:nil);await m.libraryWriteTask?.value
  try check(m.library.assignments["a"] == nil && m.library.assignments["c"] == nil && m.library.assignments["b"] == folder,"bulk menu move preserves unrelated assignment")
  m.jobs[0].deleted_at="now";m.jobs[1].deleted_at="now"
  m.jobs[0].purge_pending=true
  let encoded=try JSONEncoder().encode(m.jobs[0])
  try check(try JSONDecoder().decode(Job.self,from:encoded).purge_pending == true,"purge_pending survives wire roundtrip")
  m.requestRecordingAction("restore_recording",ids:["a","b"])
  try check(m.recordingConfirmation?.ids == ["b"],"purge_pending excluded from restore confirmation")
  m.requestRecordingAction("purge_recording",ids:["a","b","c"])
  try check(m.recordingConfirmation?.ids == ["a","b"],"purge confirmation includes only trash and count")
  try check(await m.engine.commands().isEmpty,"opening confirmation never sends destructive command")
  let confirmation=m.recordingConfirmation!
  m.jobs[1].deleted_at=nil
  m.confirmRecordingAction(confirmation)
  while m.recordingBatchBusy { await Task.yield() }
  let sent=await m.engine.commands()
  try check(sent.count == 1 && sent[0].kind == "purge_recording" && sent[0].payload["job_id"]?.string == "a","confirmation revalidates captured IDs and skips restored item")
  let aKey=AutomaticRecordingPolicy.notesKey(m.jobs[0]), bKey=AutomaticRecordingPolicy.notesKey(m.jobs[1])
  let aZip=AutomaticRecordingPolicy.zipKey(m.jobs[0],destination:"/fixture"), bZip=AutomaticRecordingPolicy.zipKey(m.jobs[1],destination:"/fixture")
  m.automaticNotesRequested=[aKey,bKey];m.automaticNoteRequests=["aReq":aKey,"bReq":bKey]
  m.automaticNotesRetryAfter=[aKey:Date(),bKey:Date()];m.zipChecked=[aZip,bZip];m.zipRetryAfter=[aZip:Date(),bZip:Date()]
  m.transcriptIndex=["a":"remove","b":"keep"];m.refreshRequests=["ar":"a","br":"b"]
  m.activeExportJobID="a";m.activeExportTask=Task { try await Task.sleep(for:.seconds(30));return nil }
  m.removeRecordingState("a")
  try check(!m.jobs.contains(where:{$0.id == "a"}) && m.transcriptIndex == ["b":"keep"],"job_removed removes exact job and transcript index")
  try check(m.automaticNotesRequested == [bKey] && m.automaticNoteRequests == ["bReq":bKey] && m.automaticNotesRetryAfter.count == 1,"job_removed clears only its notes request state")
  try check(m.zipChecked == [bZip] && m.zipRetryAfter.count == 1 && m.activeExportTask?.isCancelled == true,"job_removed cancels current stale ZIP and preserves unrelated ZIP state")
  try check(m.refreshRequests == ["br":"b"] && !m.pendingRecordingCommands.values.contains("a"),"job_removed clears only removed job command guards")
  let window=NSWindow(contentRect:NSRect(x:0,y:0,width:200,height:60),styleMask:[.titled],backing:.buffered,defer:false)
  window.isReleasedWhenClosed=false;defer{window.close()}
  let surface=VemoLibraryItemInteraction.Surface(frame:NSRect(x:0,y:0,width:200,height:60));window.contentView=surface
  var flags:NSEvent.ModifierFlags = [], all=0, single=0
  surface.input=VemoLibraryItemInteraction(label:"Synthetic",select:{single+=1},selectWithModifiers:{flags=$0},selectAll:{all+=1})
  for type in [NSEvent.EventType.leftMouseDown,.leftMouseUp] {
   let event=NSEvent.mouseEvent(with:type,location:NSPoint(x:10,y:10),modifierFlags:[.command,.shift],timestamp:0,windowNumber:window.windowNumber,context:nil,eventNumber:0,clickCount:1,pressure:1)!
   if type == .leftMouseDown { surface.mouseDown(with:event) } else { surface.mouseUp(with:event) }
  }
  try check(flags.contains([.command,.shift]) && single == 0,"native pointer forwards modifiers without collapsing selection")
  let key=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:.command,timestamp:0,windowNumber:window.windowNumber,context:nil,characters:"a",charactersIgnoringModifiers:"a",isARepeat:false,keyCode:0)!
  surface.keyDown(with:key)
  try check(all == 1,"native focused row routes Cmd A to select all")
  var edits=0
  surface.input=VemoLibraryItemInteraction(label:"Multiple",select:{single+=1},editTitle:{edits+=1},canEditTitle:{false})
  let enter=NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,characters:"\r",charactersIgnoringModifiers:"\r",isARepeat:false,keyCode:36)!
  surface.keyDown(with:enter)
  try check(edits == 0 && single == 0,"multi-selection Enter neither collapses selection nor opens rename")
  for type in [NSEvent.EventType.leftMouseDown,.leftMouseUp] {
   let event=NSEvent.mouseEvent(with:type,location:NSPoint(x:10,y:10),modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,eventNumber:0,clickCount:2,pressure:1)!
   if type == .leftMouseDown { surface.mouseDown(with:event) } else { surface.mouseUp(with:event) }
  }
  try check(edits == 1 && single == 1,"double click still explicitly selects and edits one row")
 }
}
'''.replace('/* CONFIRMATION */',block('struct RecordingBulkConfirmation')).replace('/* SELECTION */',block('    @Published var selection:')).replace('/* METHODS */',methods).replace('/* POLICY */',s[s.index('enum AutomaticRecordingPolicy {'):])
with tempfile.TemporaryDirectory(prefix='VemoBulkChecks-') as tmp:
 out=Path(tmp);path=out/'Checks.swift';path.write_text(harness)
 build=root/'macos/.build/out/Products/Debug'
 subprocess.run(['swiftc','-swift-version','6','-parse-as-library','-I',str(build),'-Xcc','-fmodule-map-file='+str(root/'macos/Sources/CSQLite/module.modulemap'),str(path),str(ui/'LibraryItemInteraction.swift'),str(build/'NativeCore.o'),'-lsqlite3','-o',str(out/'checks')],check=True)
 subprocess.run([str(out/'checks')],check=True,timeout=30)
