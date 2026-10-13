#!/usr/bin/env python3
"""Offscreen native split layout using shipping split composition; no live app state."""
from pathlib import Path
import subprocess,tempfile
root=Path(__file__).resolve().parents[2]
s=(root/'macos/Sources/VoiceMemoTranscriber/LibraryView.swift').read_text()
def block(marker):
 start=s.index(marker);opening=s.index('{',start);depth=0
 for end in range(opening,len(s)):
  if s[end]=='{':depth+=1
  elif s[end]=='}':
   depth-=1
   if depth==0:return s[start:end+1]
 raise ValueError(marker)
# Assert semantic column placement as well as actual native split geometry below.
assert 'folderNavigation' in block('    private var foldersColumn:')
recordings=block('    private var recordingsColumn:')
assert 'recordingSearch' in recordings and 'recordingsList' in recordings and 'model.chooseFiles()' in recordings
assert 'folderNavigation' not in recordings
assert 'private var sidebarToggle:' not in s
assert block('    private var splitContent:').count('.toolbar(removing: .sidebarToggle)') == 2
assert '.toolbar(removing: .sidebarToggle)' not in block('    private var foldersColumn:')
assert 'ToolbarItem(placement: .primaryAction) { sidebarToggle }' not in s
harness=r'''
import AppKit
import SwiftUI
import Foundation
struct Marker:NSViewRepresentable {
 let name:String
 func makeNSView(context:Context)->NSView { let v=NSView();v.identifier=NSUserInterfaceItemIdentifier(name);return v }
 func updateNSView(_ view:NSView,context:Context) {}
}
@MainActor final class Visibility:ObservableObject { @Published var value:NavigationSplitViewVisibility = .all }
struct Fixture:View {
 @ObservedObject var visibility:Visibility
 var columnVisibility:NavigationSplitViewVisibility { get { visibility.value } nonmutating set { visibility.value=newValue } }
 var foldersColumn:some View { Marker(name:"folders").frame(maxWidth:.infinity,maxHeight:.infinity) }
 var recordingsColumn:some View { Marker(name:"recordings").frame(maxWidth:.infinity,maxHeight:.infinity) }
 var recordingDetail:some View { Marker(name:"detail").frame(maxWidth:.infinity,maxHeight:.infinity) }
 var body:some View { splitContent }
 /* SPLIT */
}
@MainActor func descendants(_ view:NSView)->[NSView] { [view]+view.subviews.flatMap{descendants($0)} }
@MainActor func pump(_ host:NSView) {
 let deadline=Date().addingTimeInterval(0.35)
 repeat { host.layoutSubtreeIfNeeded();_ = RunLoop.main.run(mode:.default,before:Date().addingTimeInterval(0.01)) } while Date()<deadline
}
@main struct Checks {
 @MainActor static func main() throws {
  NSApplication.shared.setActivationPolicy(.prohibited)
  func check(_ ok:Bool,_ name:String) throws { guard ok else { print("FAIL: "+name);exit(1) };print("PASS: "+name) }
  for width in [1200.0,980.0] {
  print("WINDOW WIDTH: \(width)")
  let visibility=Visibility(),host=NSHostingView(rootView:Fixture(visibility:Visibility()))
  host.rootView=Fixture(visibility:visibility)
  let window=NSWindow(contentRect:NSRect(x:0,y:0,width:width,height:720),styleMask:[.titled,.resizable],backing:.buffered,defer:false)
  window.isReleasedWhenClosed=false;window.contentView=host;defer{window.close()}
  pump(host)
  guard let split=descendants(host).compactMap({$0 as? NSSplitView}).first(where:{$0.arrangedSubviews.count==3}) else { throw NSError(domain:"native three-column split absent",code:1) }
  try check(split.isVertical,"native split uses three side-by-side columns")
  let frames=["folders","recordings","detail"].map { name in
   let view=descendants(host).first(where:{$0.identifier?.rawValue == name})!
   return view.convert(view.bounds,to:host)
  }
  print("FRAMES: \(frames)")
  try check(frames[0].width >= 199 && frames[0].width <= 251,"folder column respects 200–250 width")
  try check(frames[1].width >= 279 && frames[1].width <= 361,"recording column respects 280–360 width")
  try check(frames[2].width > 400,"detail receives remaining width")
  try check(abs(frames[0].minY-frames[1].minY)<1 && abs(frames[1].height-frames[2].height)<1,"column top and bottom boundaries align")
  visibility.value = .doubleColumn;pump(host)
  try check(split.isSubviewCollapsed(split.arrangedSubviews[0]),"hiding sidebar collapses folders")
  try check(!split.isSubviewCollapsed(split.arrangedSubviews[1]) && !split.isSubviewCollapsed(split.arrangedSubviews[2]),"recordings and detail remain available")
  visibility.value = .all;pump(host)
  try check(!split.isSubviewCollapsed(split.arrangedSubviews[0]),"show sidebar restores folders")
  }
 }
}
'''.replace('/* SPLIT */',block('    private var splitContent:').replace('$columnVisibility','Binding(get:{ columnVisibility },set:{ columnVisibility=$0 })'))
with tempfile.TemporaryDirectory(prefix='VemoThreeColumn-') as tmp:
 out=Path(tmp);path=out/'Checks.swift';path.write_text(harness)
 subprocess.run(['swiftc','-swift-version','6','-parse-as-library',str(path),'-o',str(out/'checks')],check=True)
 subprocess.run([str(out/'checks')],check=True,timeout=15)
print('PASS: shipping columns place folders/search/list/import in intended panes with single toggle')
