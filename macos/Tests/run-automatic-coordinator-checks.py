#!/usr/bin/env python3
"""Compile shipping coordinator methods with synthetic engine/store; no app or user state."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
source=(root/'macos/Sources/VoiceMemoTranscriber/AppModel.swift').read_text()
def block(marker):
 start=source.index(marker);opening=source.index('{',start);depth=0
 for end in range(opening,len(source)):
  if source[end]=='{':depth+=1
  elif source[end]=='}':
   depth-=1
   if depth==0:return source[start:end+1].replace('private func','func')
 raise ValueError(marker)
methods='\n'.join(block(marker) for marker in ['    private func sendRecordingCommand(', '    func command(', '    private func resetPendingConnectionRequests(', '    private func consumeObsoleteBrokerReply(', '    private func completeAutomaticNoteRequest(', '    private func clearUnacknowledgedNotes(', '    private func cancelObsoleteExport(', '    private func recordingCommandPending(', '    private func scheduleAutomaticOutputs(', '    private func requestMissingNotes(', '    func exportCompletedZIP(', '    private func checkRecordingZIPs(', '    func flushZIPExport(', '    private func updateZIPProgress('])
policy=source[source.index('enum AutomaticRecordingPolicy {'):]
harness=r'''
import Foundation
import NativeCore
struct DemoContent { static let enabled = false }
actor FakeEngine {
 var sent:[WireCommand] = []
 var fail = false
 func setFailure(_ value:Bool) { fail = value }
 func send(_ command:WireCommand) throws { sent.append(command); if fail { throw NSError(domain:"send failure",code:1) } }
 func commands() -> [WireCommand] { sent }
}
actor FakeZIPStore {
 var calls:[String] = []
 var published:[String] = []
 var relocations:[String] = []
 var organizations:[LibraryOrganization] = []
 func movedIDs() -> [String] { relocations }
 func capturedOrganizations() -> [LibraryOrganization] { organizations }
 func relocateIfNeeded(jobID:String,destination:URL,organization:LibraryOrganization,progress:@escaping @Sendable(String)async->Void) async throws -> URL? {
  try Task.checkCancellation();relocations.append(jobID);organizations.append(organization);return nil
 }
 func publishedIDs() -> [String] { published }
 var blocked = false
 var entered = false
 var waiter:CheckedContinuation<Void,Never>?
 func blockNext() { blocked = true; entered = false }
 func waitForEntry() async { while !entered { await Task.yield() } }
 func release() { waiter?.resume(); waiter = nil }
 func exportIfNeeded(job:Job,destination:URL,organization:LibraryOrganization,progress:@escaping @Sendable(String)async->Void) async throws -> URL? {
  calls.append(job.id)
  if blocked { blocked = false; entered = true; await withCheckedContinuation { waiter = $0 } }
  try Task.checkCancellation()
  if job.id == "bad" { throw NSError(domain:"fixture error",code:1) }
  published.append(job.id)
  return nil
 }
 func ids() -> [String] { calls }
}
@MainActor final class Fixture {
 var jobs:[Job] = []
 var library = LibraryOrganization(), libraryReady = true, libraryBusy = false
 var internalBrokerReplies:Set<String> = []
 var nativeSnapshotTasks:[String:[UUID:Task<Void,Never>]] = [:]
 var connected = true, paused = false, notesModelReady = true
 var autoZIPEnabled = true, zipExportClosing = false, zipExportBusy = false, zipRescheduleRequested = false
 var refreshPendingJobs:Set<String> = [], refreshRequests:[String:String] = [:], pendingRecordingCommands:[String:String] = [:]
 var automaticNotesRequested:Set<String> = [], zipChecked:Set<String> = [], zipRetryAfter:[String:Date] = [:]
 var automaticNoteRequests:[String:String] = [:], automaticNotesRetryAfter:[String:Date] = [:]
 var activeExportTask:Task<URL?,Error>?, activeExportJobID:String?, activeExportKey:String?
 var automaticNotesTask:Task<Void,Never>?, zipExportTask:Task<Void,Never>?
 var zipExportFolder = "/synthetic-destination", zipExportProgress = ""
 var zipExportError:String?, zipExportResult:URL?, problem:String?
 let engine = FakeEngine(), perRecordingZIPStore = FakeZIPStore()
 /* METHODS */
}
/* POLICY */
@main struct Checks {
 @MainActor static func main() async throws {
  func check(_ ok:Bool,_ name:String) throws { if !ok { throw NSError(domain:name,code:1) }; print("PASS: "+name) }
  func job(_ id:String,notes:Bool) throws -> Job {
   var value:[String:Any] = ["id":id,"uuid":id,"original_title":id,"status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed","artifact_path":"/fixture/"+id+".json","audio_path":"/fixture/"+id+".m4a"]
   if notes { value["notes_path"] = "/fixture/"+id+"-notes.json"; value["notes_status"] = "completed" }
   return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:value))
  }
  let keyJob = try job("key",notes:true)
  try check(AutomaticRecordingPolicy.zipKey(keyJob,destination:"/fixture",libraryRevision:1) != AutomaticRecordingPolicy.zipKey(keyJob,destination:"/fixture",libraryRevision:2),"folder revision changes ZIP coordinator key")
  let notes = Fixture(); notes.jobs = [try job("legacy",notes:false)]; notes.paused = true
  notes.scheduleAutomaticOutputs(); await notes.automaticNotesTask?.value
  try check(await notes.engine.commands().isEmpty,"paused user sends no automatic note or resume command")
  notes.paused = false; notes.scheduleAutomaticOutputs(); await notes.automaticNotesTask?.value
  notes.pendingRecordingCommands.removeAll()
  notes.scheduleAutomaticOutputs(); await notes.automaticNotesTask?.value
  let sent = await notes.engine.commands()
  try check(sent.count == 1 && sent[0].kind == "generate_notes","legacy notes requested once across repeated events")
  let retry = Fixture(); retry.jobs = [try job("retry",notes:false)]
  await retry.engine.setFailure(true)
  retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  try check(retry.automaticNotesRequested.isEmpty && retry.automaticNoteRequests.isEmpty,"send failure clears automatic note request key")
  retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  try check(await retry.engine.commands().count == 1,"send failure waits bounded retry delay")
  await retry.engine.setFailure(false); retry.automaticNotesRetryAfter = retry.automaticNotesRetryAfter.mapValues { _ in .distantPast }
  retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  try check(await retry.engine.commands().count == 2,"failed note send retries after delay")
  let rejectedID = retry.automaticNoteRequests.keys.first!
  retry.completeAutomaticNoteRequest(rejectedID,success:false); retry.pendingRecordingCommands.removeValue(forKey:rejectedID)
  try check(retry.automaticNotesRequested.isEmpty && !retry.automaticNotesRetryAfter.isEmpty,"negative acknowledgment clears key with retry delay")
  retry.automaticNotesRetryAfter.removeAll(); retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  retry.resetPendingConnectionRequests()
  retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  try check(await retry.engine.commands().count == 4,"disconnect clears unacknowledged key for reconnect")
  retry.jobs[0].notes_status = "failed"; retry.resetPendingConnectionRequests()
  retry.scheduleAutomaticOutputs(); await retry.automaticNotesTask?.value
  try check(await retry.engine.commands().count == 4,"legitimate failed note status does not auto retry")
  let retained = Fixture()
  retained.automaticNotesRequested = ["acked","unacked"]
  retained.automaticNoteRequests["inflight"] = "unacked"
  retained.pendingRecordingCommands["inflight"] = "job"
  retained.refreshRequests["refresh"] = "job"
  retained.resetPendingConnectionRequests()
  try check(retained.automaticNotesRequested == ["acked"] && retained.automaticNoteRequests.isEmpty && retained.pendingRecordingCommands.isEmpty && retained.refreshRequests.isEmpty,"explicit reconnect clears only unacknowledged work and stale command guards")
  for message in ["stale_snapshot","snapshot_not_requested","stale_publication","recording_trashed"] {
   let payload:[String:JSONValue] = ["request_id":.string("internal"),"ok":.bool(false),"error":.object(["code":.string("invalid_command"),"message":.string(message)])]
   retained.internalBrokerReplies.insert("internal")
   try check(retained.consumeObsoleteBrokerReply(payload),"obsolete internal reply suppressed: " + message)
   try check(!retained.consumeObsoleteBrokerReply(payload),"same error on untracked user command remains visible: " + message)
  }
  retained.internalBrokerReplies.insert("source-error")
  try check(!retained.consumeObsoleteBrokerReply(["request_id":.string("source-error"),"ok":.bool(false),"error":.object(["code":.string("source_incomplete"),"message":.string("source unavailable")])]),"real internal source error remains visible")
  let zip = Fixture(); zip.jobs = [try job("bad",notes:true),try job("good",notes:true)]
  zip.autoZIPEnabled = false; zip.scheduleAutomaticOutputs()
  try check(await zip.perRecordingZIPStore.ids().isEmpty,"automatic ZIP optout honored")
  zip.exportCompletedZIP(); await zip.zipExportTask?.value
  try check(await zip.perRecordingZIPStore.ids() == ["bad","good"],"manual check processes good recording despite earlier failure")
  try check(zip.zipExportError?.contains("bad") == true,"individual failures aggregate in UI")
  zip.autoZIPEnabled = true; zip.scheduleAutomaticOutputs(); await zip.zipExportTask?.value
  try check(await zip.perRecordingZIPStore.ids().count == 2,"session fingerprint and retry delay suppress repeated event work")
  zip.scheduleAutomaticOutputs(reconcile:true); await zip.zipExportTask?.value
  try check(await zip.perRecordingZIPStore.ids() == ["bad","good","good"],"reconciliation checks successful receipt while failed job waits")
  zip.jobs.removeAll { $0.id == "bad" }; zip.exportCompletedZIP(); await zip.zipExportTask?.value
  try check(zip.zipExportProgress == "모든 ZIP이 최신입니다","explicit unchanged check reports latest")
  let fresh = Fixture(); fresh.jobs = [try job("first",notes:true),try job("second",notes:true)]
  await fresh.perRecordingZIPStore.blockNext(); fresh.exportCompletedZIP(); await fresh.perRecordingZIPStore.waitForEntry()
  fresh.jobs[1].deleted_at = "2026-10-13"
  fresh.scheduleAutomaticOutputs()
  await fresh.perRecordingZIPStore.release(); await fresh.zipExportTask?.value
  try check(await fresh.perRecordingZIPStore.ids() == ["first"],"latest job recheck excludes trash while previous archive is in flight")
  for mutation in ["trash","refresh","retitle","busy","manual notes","recording date","folder revision"] {
   let changed = Fixture(); changed.jobs = [try job("first",notes:true),try job("second",notes:true)]
   await changed.perRecordingZIPStore.blockNext(); changed.exportCompletedZIP(); await changed.perRecordingZIPStore.waitForEntry()
   switch mutation {
   case "trash": changed.jobs[0].deleted_at = "2026-10-13"
   case "refresh": changed.refreshRequests["request"] = "first"
   case "retitle": changed.jobs[0].topic = "changed"
   case "recording date": changed.command("set_recording_date",["job_id":.string("first")])
   case "manual notes": changed.pendingRecordingCommands["request"] = "first"
   case "folder revision": changed.library.revision += 1
   default: changed.jobs[0].status = "noting"
   }
   changed.cancelObsoleteExport()
   try check(changed.activeExportTask?.isCancelled == true,"same-job \(mutation) cancels only current child")
   await changed.perRecordingZIPStore.release(); await changed.zipExportTask?.value
   try check(await changed.perRecordingZIPStore.publishedIDs() == ["second"],"\(mutation) prevents stale publication and continues other job")
   try check(changed.zipExportError == nil && changed.zipRetryAfter.isEmpty,"\(mutation) cancellation is not persisted as export failure")
  }
  let folderOnly = Fixture(); folderOnly.jobs = [try job("no-notes",notes:false)]
  folderOnly.jobs[0].notes_status = "failed"
  folderOnly.library.revision = 7
  folderOnly.exportCompletedZIP(); await folderOnly.zipExportTask?.value
  let noNotesExports = await folderOnly.perRecordingZIPStore.ids()
  let noNotesMoves = await folderOnly.perRecordingZIPStore.movedIDs()
  try check(noNotesExports.isEmpty && noNotesMoves == ["no-notes"],"existing ZIP relocation works without completed notes and never creates incomplete ZIP")
  try check(await folderOnly.perRecordingZIPStore.capturedOrganizations().first?.revision == 7,"relocation captures current committed library organization")
  folderOnly.libraryReady = false; folderOnly.exportCompletedZIP(); await folderOnly.zipExportTask?.value
  try check(await folderOnly.perRecordingZIPStore.movedIDs().count == 1,"unloaded library cannot move ZIP to incorrect root")
  folderOnly.libraryReady = true; folderOnly.libraryBusy = true; folderOnly.exportCompletedZIP(); await folderOnly.zipExportTask?.value
  try check(await folderOnly.perRecordingZIPStore.movedIDs().count == 1,"pending folder transaction cannot start ZIP relocation")
  let shutdown = Fixture(); shutdown.jobs = [try job("first",notes:true),try job("second",notes:true)]
  await shutdown.perRecordingZIPStore.blockNext(); shutdown.exportCompletedZIP(); await shutdown.perRecordingZIPStore.waitForEntry()
  let closing = Task { await shutdown.flushZIPExport() }; await Task.yield()
  try check(shutdown.zipExportBusy,"shutdown waits for in-flight export")
  await shutdown.perRecordingZIPStore.release(); await closing.value
  let shutdownIDs = await shutdown.perRecordingZIPStore.ids()
  try check(!shutdown.zipExportBusy && shutdownIDs == ["first"],"shutdown finishes current export and starts no further archives")
 }
}
'''.replace('/* METHODS */',methods).replace('/* POLICY */',policy)
with tempfile.TemporaryDirectory(prefix='VemoCoordinator-') as tmp:
 path=Path(tmp); swift=path/'Checks.swift';swift.write_text(harness)
 build=root/'macos/.build/out/Products/Debug'
 subprocess.run(['swiftc','-swift-version','6','-parse-as-library','-I',str(build),'-Xcc','-fmodule-map-file='+str(root/'macos/Sources/CSQLite/module.modulemap'),str(swift),str(build/'NativeCore.o'),'-lsqlite3','-o',str(path/'checks')],check=True)
 subprocess.run([str(path/'checks')],check=True,timeout=30)
