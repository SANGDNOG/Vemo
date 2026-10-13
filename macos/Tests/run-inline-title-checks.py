#!/usr/bin/env python3
"""Exercise shipping title-edit state and native input without live app/user state."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
source_dir = root / 'macos/Sources/VoiceMemoTranscriber'
source = (source_dir / 'AppModel.swift').read_text()

def block(marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth = 0
    for end in range(opening, len(source)):
        if source[end] == '{':
            depth += 1
        elif source[end] == '}':
            depth -= 1
            if depth == 0:
                return source[start:end + 1]
    raise ValueError(marker)

methods = '\n'.join(block(marker) for marker in [
    '    func isEditingTitle(', '    func beginTitleEdit(',
    '    func cancelTitleEdit(', '    func saveTitleEdit(', '    func setRecordingSelection('])
selection = block('    @Published var selection:').replace('@Published ', '')
harness = r'''
import Foundation
import NativeCore
/* LOCATION */
@MainActor final class Fixture {
 var jobs:[Job] = []
 var activeJobs:[Job] { jobs.filter { $0.deleted_at == nil } }
 var editingTitleJobID:String?, editingTitleLocation:TitleEditingLocation?
 var titleDraft = ""
 var selectedIDs:Set<String> = []
 var updatingSelection = false
 var selectionAnchor:String?
 var visibleJobs:[Job] { jobs }
 var sent:[WireCommand] = []
 func command(_ kind:String,_ payload:[String:JSONValue]) { sent.append(WireCommand(kind,payload)) }
 /* SELECTION */
 /* METHODS */
}
@main struct Checks {
 @MainActor static func main() throws {
  func check(_ ok:Bool,_ name:String) throws {
   guard ok else { throw NSError(domain:name,code:1) }
   print("PASS: " + name)
  }
  func job(_ id:String) throws -> Job {
   let value:[String:Any] = ["id":id,"uuid":id,"original_title":"Original " + id,"status":"unprocessed","stage":"unprocessed","elapsed_seconds":0,"title_status":"pending","alignment_status":"pending"]
   return try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:value))
  }
  let first = try job("first"), second = try job("second")
  let model = Fixture(); model.jobs = [first,second]
  model.beginTitleEdit(first,at:.sidebar)
  try check(model.selection == first.id && model.isEditingTitle(first,at:.sidebar),"sidebar edit selects its job and stays at row location")
  try check(!model.isEditingTitle(first,at:.detail) && model.titleDraft == "Original first","shared draft has only one editing location")
  model.titleDraft = "   \n "; model.saveTitleEdit()
  try check(model.sent.isEmpty && model.editingTitleJobID == first.id,"blank title stays editable and sends nothing")
  model.titleDraft = "  New title \n"; model.saveTitleEdit()
  try check(model.sent.count == 1 && model.sent[0].payload["job_id"]?.string == first.id && model.sent[0].payload["topic"]?.string == "New title","Enter save trims title and targets editing unprocessed job")
  try check(model.editingTitleJobID == nil && model.editingTitleLocation == nil,"save closes inline editor")
  model.beginTitleEdit(first,at:.detail); model.titleDraft = "Discard"; model.cancelTitleEdit()
  try check(model.sent.count == 1 && model.titleDraft.isEmpty && model.editingTitleJobID == nil,"Escape cancels without retitle command")
  model.beginTitleEdit(first,at:.detail)
  try check(model.isEditingTitle(first,at:.detail) && !model.isEditingTitle(first,at:.sidebar),"detail edit belongs only to header")
  model.titleDraft = "Old draft"; model.selection = second.id; model.saveTitleEdit()
  try check(model.sent.count == 1 && model.editingTitleJobID == nil,"selection change cancels and never saves old draft to new job")
  model.beginTitleEdit(second,at:.sidebar); model.titleDraft = "Keep draft"; model.selection = second.id
  try check(model.titleDraft == "Keep draft" && model.editingTitleJobID == second.id,"reselecting same row preserves edit")
  model.beginTitleEdit(first,at:.detail)
  try check(model.titleDraft == "Original first" && model.selection == first.id,"new editor replaces old draft with intended job")
  model.jobs[0].deleted_at = "2026-10-13"; model.saveTitleEdit()
  try check(model.sent.count == 1 && model.editingTitleJobID == nil,"trashed job cannot receive stale title save")
  model.beginTitleEdit(model.jobs[0],at:.sidebar)
  try check(model.editingTitleJobID == nil,"trash cannot begin editing")
 }
}
'''.replace('/* LOCATION */', block('enum TitleEditingLocation')) \
   .replace('/* SELECTION */', selection).replace('/* METHODS */', methods)
with tempfile.TemporaryDirectory(prefix='VemoInlineTitle-') as tmp:
    out = Path(tmp)
    checks = out / 'Checks.swift'
    checks.write_text(harness)
    build = root / 'macos/.build/out/Products/Debug'
    subprocess.run(['swiftc','-swift-version','6','-parse-as-library','-I',str(build),
        '-Xcc','-fmodule-map-file='+str(root/'macos/Sources/CSQLite/module.modulemap'),
        str(checks),str(build/'NativeCore.o'),'-lsqlite3','-o',str(out/'model-checks')],check=True)
    subprocess.run([str(out/'model-checks')],check=True,timeout=20)
    subprocess.run(['swiftc','-swift-version','6','-parse-as-library',
        str(source_dir/'LibraryItemInteraction.swift'),str(root/'macos/Tests/RecordingTitleInputChecks.swift'),
        '-o',str(out/'input-checks')],check=True)
    subprocess.run([str(out/'input-checks')],check=True,timeout=20)
