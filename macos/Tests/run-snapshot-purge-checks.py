#!/usr/bin/env python3
"""Deterministic suspended native snapshots; shipping purge dispatch, no source files or engine."""
from pathlib import Path
import argparse,subprocess,tempfile
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument("--single-pass-mutation",action="store_true",help="Mutate only the generated source copy; expected to fail the new-token drain check")
args=parser.parse_args()
root=Path(__file__).resolve().parents[2]
s=(root/'macos/Sources/VoiceMemoTranscriber/AppModel.swift').read_text()
def block(marker):
 start=s.index(marker);opening=s.index('{',start);depth=0
 for end in range(opening,len(s)):
  if s[end]=='{':depth+=1
  elif s[end]=='}':
   depth-=1
   if depth==0:return s[start:end+1].replace('private func','func')
 raise ValueError(marker)
methods='\n'.join(block(marker) for marker in ['    private func startNativeSnapshot(', '    private func sendRecordingCommand(', '    func command(', '    func purgeRecording(', '    func confirmRecordingAction(', '    private func recordingCommandPending('])
# Observe entry to each drain pass without adding a suspension to the shipping method.
# The exact same observation remains in the negative-control copy.
drain='while let tasks = nativeSnapshotTasks[id], !tasks.isEmpty {'
assert methods.count(drain) == 1
methods=methods.replace(drain,drain+'\n                drainPasses += 1',1)
if args.single_pass_mutation:
 methods=methods.replace(drain,drain.replace('while let','if let'),1)
print('MODE: '+('single-pass mutation (generated copy only)' if args.single_pass_mutation else 'shipping loop with pass observation'),flush=True)
harness=r'''
import Foundation
import NativeCore
struct DemoContent { static let enabled=false }
/* CONFIRMATION */
actor Engine {
 var sent:[WireCommand]=[]
 var failing=false
 func fail() { failing=true }
 func send(_ request:WireCommand) throws {
  if failing { throw NSError(domain:"synthetic send failure",code:1) }
  sent.append(request)
 }
 func commands() -> [WireCommand] { sent }
}
@MainActor final class Gate {
 var entered=false
 var continuation:CheckedContinuation<Void,Never>?
 func suspend() async { entered=true; await withCheckedContinuation { continuation=$0 } }
 func release() { continuation?.resume();continuation=nil }
}
@MainActor final class Fixture {
 let engine=Engine()
 var jobs:[Job]=[]
 var drainPasses=0
 var brokerRequests:Set<String>=[], nativeSnapshotTasks:[String:[UUID:Task<Void,Never>]]=[:]
 var internalBrokerReplies:Set<String>=[], pendingRecordingCommands:[String:String]=[:]
 var refreshPendingJobs:Set<String>=[],refreshRequests:[String:String]=[:]
 var recordingConfirmation:RecordingBulkConfirmation?, recordingBatchBusy=false,connected=true,problem:String?
 func cancelObsoleteExport() {}
 /* METHODS */
}
@main struct Checks {
 @MainActor static func main() async throws {
  func check(_ ok:Bool,_ name:String) throws { guard ok else { print("FAIL: "+name);exit(1) };print("PASS: "+name) }
  func job() throws -> Job {
   let value:[String:Any]=["id":"job","uuid":"job","original_title":"Synthetic","status":"completed","stage":"completed","elapsed_seconds":0,"title_status":"completed","alignment_status":"completed","deleted_at":"now"]
   return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:value))
  }
  let f=Fixture(),old=Gate(),new=Gate()
  var duplicateRan=false
  f.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:old") { await old.suspend() }
  f.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:old") { duplicateRan=true }
  while !old.entered { await Task.yield() }
  let oldTask=f.nativeSnapshotTasks["job"]!.values.first!
  try check(f.nativeSnapshotTasks["job"]?.count == 1 && !duplicateRan,"duplicate token registers exactly one native operation")
  var purgeStarted=false
  let purge=Task { purgeStarted=true;try await f.sendRecordingCommand(WireCommand("purge_recording",["job_id":.string("job")])) }
  while !purgeStarted { await Task.yield() }
  try check(await f.engine.commands().isEmpty,"purge cannot send while native snapshot is suspended")
  f.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:new") { await new.suspend() }
  while !new.entered { await Task.yield() }
  old.release();await oldTask.value
  try check(f.nativeSnapshotTasks["job"]?.count == 1 && f.brokerRequests.contains("snapshot:job:new"),"old completion preserves newer token task and dedup state")
  // Keep the second gate closed until purge has actually resumed. A single-pass
  // mutant reaches engine.send; the shipping loop enters pass two and suspends.
  let deadline=ContinuousClock.now.advanced(by:.seconds(2))
  var sentEarly=false
  while f.drainPasses < 2 && ContinuousClock.now < deadline {
   if !(await f.engine.commands()).isEmpty { sentEarly=true;break }
   try await Task.sleep(for:.milliseconds(1))
  }
  try check(f.drainPasses >= 2 && !sentEarly && new.continuation != nil,"purge resumes into a second drain pass while new snapshot stays suspended")
  try check(await f.engine.commands().isEmpty,"purge rechecks snapshots added during wait")
  new.release();try await purge.value
  try check(await f.engine.commands().count == 1,"purge sends exactly once after all native tasks finish")
  try check(f.nativeSnapshotTasks["job"] == nil && f.brokerRequests.isEmpty,"snapshot handles and dedup keys removed after completion")
  let unrelated=Gate()
  f.startNativeSnapshot(jobID:"other",requestKey:"snapshot:other:x") { await unrelated.suspend() }
  while !unrelated.entered { await Task.yield() }
  try await f.sendRecordingCommand(WireCommand("purge_recording",["job_id":.string("job")]))
  try check(await f.engine.commands().count == 2,"other job snapshot never blocks purge")
  unrelated.release()
  for entry in ["generic","direct","bulk"] {
   let fixture=Fixture(),gate=Gate();fixture.jobs=[try job()]
   fixture.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:token") { await gate.suspend() }
   while !gate.entered { await Task.yield() }
   if entry == "generic" { fixture.command("purge_recording",["job_id":.string("job")]) }
   else if entry == "direct" { fixture.purgeRecording(fixture.jobs[0]) }
   else { fixture.confirmRecordingAction(RecordingBulkConfirmation(kind:"purge_recording",ids:["job"])) }
   while fixture.pendingRecordingCommands.isEmpty { await Task.yield() }
   try check(await fixture.engine.commands().isEmpty,"\(entry) purge entry waits for native snapshot")
   gate.release()
   while await fixture.engine.commands().isEmpty { await Task.yield() }
   try check(await fixture.engine.commands().count == 1,"\(entry) purge entry dispatches after snapshot release")
  }
  let failing=Fixture(),gate=Gate();var caught=false
  failing.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:failure") {
   do { await gate.suspend();throw NSError(domain:"snapshot failure",code:1) }
   catch { caught=true }
  }
  while !gate.entered { await Task.yield() }
  let afterFailure=Task { try await failing.sendRecordingCommand(WireCommand("purge_recording",["job_id":.string("job")])) }
  gate.release();try await afterFailure.value
  try check(caught && failing.nativeSnapshotTasks.isEmpty && failing.brokerRequests.isEmpty,"caught source error still releases native snapshot tracking")
  let transport=Fixture(),blocked=Gate();transport.jobs=[try job()];await transport.engine.fail()
  transport.startNativeSnapshot(jobID:"job",requestKey:"snapshot:job:transport") { await blocked.suspend() }
  while !blocked.entered { await Task.yield() }
  transport.command("purge_recording",["job_id":.string("job")]);blocked.release()
  while transport.problem == nil { await Task.yield() }
  try check(transport.pendingRecordingCommands.isEmpty && transport.nativeSnapshotTasks.isEmpty,"send failure after snapshot wait clears pending guard and reports error")
 }
}
'''.replace('/* METHODS */',methods).replace('/* CONFIRMATION */',block('struct RecordingBulkConfirmation'))
with tempfile.TemporaryDirectory(prefix='VemoSnapshotPurge-') as tmp:
 out=Path(tmp);path=out/'Checks.swift';path.write_text(harness)
 build=root/'macos/.build/out/Products/Debug'
 subprocess.run(['swiftc','-swift-version','6','-parse-as-library','-I',str(build),'-Xcc','-fmodule-map-file='+str(root/'macos/Sources/CSQLite/module.modulemap'),str(path),str(build/'NativeCore.o'),'-lsqlite3','-o',str(out/'checks')],check=True)
 subprocess.run([str(out/'checks')],check=True,timeout=30)
