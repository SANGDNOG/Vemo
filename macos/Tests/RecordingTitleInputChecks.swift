import SwiftUI
import AppKit

@main struct RecordingTitleInputChecks {
    @MainActor static func main() throws {
        NSApplication.shared.setActivationPolicy(.prohibited)
        let window = NSWindow(contentRect:NSRect(x:0,y:0,width:300,height:100),styleMask:[.titled],backing:.buffered,defer:false)
        window.isReleasedWhenClosed = false
        let surface = VemoLibraryItemInteraction.Surface(frame:NSRect(x:0,y:0,width:300,height:100))
        window.contentView = surface
        var selected = 0, edited = 0
        surface.input = VemoLibraryItemInteraction(label:"Synthetic",select:{ selected += 1 },editTitle:{ edited += 1 })
        func check(_ ok: Bool,_ message:String) throws {
            guard ok else { throw NSError(domain:message,code:1) }
            print("PASS: " + message)
        }
        func mouse(_ type:NSEvent.EventType,clicks:Int) -> NSEvent {
            NSEvent.mouseEvent(with:type,location:NSPoint(x:20,y:20),modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,eventNumber:0,clickCount:clicks,pressure:1)!
        }
        surface.mouseDown(with:mouse(.leftMouseDown,clicks:1)); surface.mouseUp(with:mouse(.leftMouseUp,clicks:1))
        try check(selected == 1 && edited == 0,"single click selects without edit")
        surface.mouseDown(with:mouse(.leftMouseDown,clicks:2)); surface.mouseUp(with:mouse(.leftMouseUp,clicks:2))
        try check(selected == 2 && edited == 1,"double click selects then edits")
        func key(_ code:UInt16) -> NSEvent {
            NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:[],timestamp:0,windowNumber:window.windowNumber,context:nil,characters:"",charactersIgnoringModifiers:"",isARepeat:false,keyCode:code)!
        }
        surface.keyDown(with:key(36))
        try check(edited == 2,"Enter edits selected recording")
        surface.keyDown(with:key(49))
        try check(selected == 4 && edited == 2,"Space retains selection behavior")
        surface.input = VemoLibraryItemInteraction(label:"Trash",select:{ selected += 1 })
        surface.keyDown(with:key(36))
        try check(selected == 5 && edited == 2,"noneditable row Enter still selects")
        try check(surface.acceptsFirstResponder,"keyboard focus remains available")
        window.close()
    }
}
