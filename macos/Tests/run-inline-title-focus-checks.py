#!/usr/bin/env python3
"""Host the actual inline editor and pointer bridge offscreen; no global input or user data."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
ui = root / 'macos/Sources/VoiceMemoTranscriber'
source = (ui / 'LibraryView.swift').read_text()
editor = source[source.index('struct VemoInlineTitleEditor:'):]
harness = r'''
import AppKit
import SwiftUI
import Foundation
/* EDITOR */
@MainActor final class State: ObservableObject {
 @Published var editing = false
 @Published var text = "합성 녹음 제목"
 var saved = 0, cancelled = 0
 func save() { saved += 1; editing = false }
 func cancel() { cancelled += 1; editing = false }
}
struct Fixture: View {
 @ObservedObject var state:State
 let sidebar:Bool
 var row: some View {
  VStack(alignment:.leading) {
   if state.editing { VemoInlineTitleEditor(text:$state.text,font:.systemFont(ofSize:sidebar ? 13 : 22,weight:.semibold),save:state.save,cancel:state.cancel) }
   else { Text(state.text) }
   Text("Synthetic recording")
  }.frame(maxWidth:.infinity,alignment:.leading)
   .overlay {
    if sidebar && !state.editing {
     VemoLibraryItemInteraction(label:"Synthetic",select:{},editTitle:{ state.editing = true })
    }
   }
 }
 var body: some View {
  Group {
   if sidebar { List { row }.listStyle(.sidebar) }
   else { row.padding(24) }
  }.frame(width:300,height:150)
 }
}
@MainActor func descendants(_ view:NSView) -> [NSView] { [view] + view.subviews.flatMap { descendants($0) } }
@MainActor func pump(_ host:NSView) {
 let until = Date().addingTimeInterval(0.2)
 repeat { host.layoutSubtreeIfNeeded(); _ = RunLoop.main.run(mode:.default,before:Date().addingTimeInterval(0.005)) } while Date() < until
}
@main struct Checks {
 @MainActor static func main() throws {
  NSApplication.shared.setActivationPolicy(.prohibited)
  setbuf(stdout,nil)
  func check(_ value:Bool,_ name:String) throws {
   guard value else { print("FAIL: " + name); throw NSError(domain:name,code:1) }
   print("PASS: " + name)
  }
  for sidebar in [true,false] {
  print("LOCATION: " + (sidebar ? "sidebar" : "detail"))
  let state = State()
  let host = NSHostingView(rootView:Fixture(state:state,sidebar:sidebar))
  let window = NSWindow(contentRect:NSRect(x:0,y:0,width:300,height:150),styleMask:[.titled],backing:.buffered,defer:false)
  window.isReleasedWhenClosed = false; window.contentView = host
  defer { window.close() }
  pump(host)
  if sidebar {
  guard let surface = descendants(host).compactMap({ $0 as? VemoLibraryItemInteraction.Surface }).first else { throw NSError(domain:"missing row overlay",code:1) }
  let point = surface.convert(NSPoint(x:surface.bounds.midX,y:surface.bounds.midY),to:nil)
  func mouse(_ type:NSEvent.EventType) -> NSEvent {
   NSEvent.mouseEvent(with:type,location:point,modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,eventNumber:0,clickCount:2,pressure:1)!
  }
  surface.mouseDown(with:mouse(.leftMouseDown)); surface.mouseUp(with:mouse(.leftMouseUp))
  } else { state.editing = true }
  pump(host)
  try check(state.editing && descendants(host).allSatisfy { !($0 is VemoLibraryItemInteraction.Surface) },"editing surface has no blocking pointer overlay")
  guard let field = descendants(host).compactMap({ $0 as? NSTextField }).first(where:{ $0.isEditable }) else { throw NSError(domain:"missing inline text field",code:1) }
  guard let textView = field.currentEditor() as? NSTextView else { print("FOCUS DEBUG: field=\(field.frame) hidden=\(field.isHiddenOrHasHiddenAncestor) window=\(field.window != nil) responder=\(String(describing:window.firstResponder))"); throw NSError(domain:"inline field has no focused field editor",code:1) }
  try check(window.firstResponder === textView,"inline editor receives actual first responder after row transition")
  try check(textView.selectedRange() == NSRange(location:0,length:(state.text as NSString).length),"begin selects entire title")
  textView.insertText("바뀐 합성 제목",replacementRange:textView.selectedRange())
  pump(host)
  try check(state.text == "바뀐 합성 제목","native input reaches shared draft")
  textView.setSelectedRange(NSRange(location:2,length:0))
  state.objectWillChange.send(); pump(host)
  try check(textView.selectedRange() == NSRange(location:2,length:0),"ordinary updates do not refocus or reselect title")
  textView.doCommand(by:#selector(NSResponder.cancelOperation(_:))); pump(host)
  try check(state.cancelled == 1 && state.saved == 0 && !state.editing,"Escape cancels actual field editor without save")
  state.editing = true; pump(host)
  guard let reopened = descendants(host).compactMap({ $0 as? NSTextField }).first(where:{ $0.isEditable }),
        let reopenedEditor = reopened.currentEditor() as? NSTextView else { throw NSError(domain:"reopened editor unfocused",code:1) }
  try check(window.firstResponder === reopenedEditor,"each new edit session receives focus")
  reopenedEditor.doCommand(by:#selector(NSResponder.insertNewline(_:))); pump(host)
  try check(state.saved == 1 && !state.editing,"Enter submits actual field editor")
  }
 }
}
'''.replace('/* EDITOR */', editor)
with tempfile.TemporaryDirectory(prefix='VemoInlineFocus-') as tmp:
    out = Path(tmp)
    path = out / 'Checks.swift'; path.write_text(harness)
    subprocess.run(['swiftc','-swift-version','6','-parse-as-library',str(path),str(ui/'LibraryItemInteraction.swift'),'-o',str(out/'checks')],check=True)
    subprocess.run([str(out/'checks')],check=True,timeout=15)
