import SwiftUI
import AppKit

/// One native pointer surface owns click, drag and drop; the SwiftUI row draws its appearance.
struct VemoLibraryItemInteraction: NSViewRepresentable {
    let label: String
    var selected = false
    var recordingCount: Int?
    var select: (() -> Void)?
    var dragData: (() -> Data?)?
    var dragEnded: ((Data) -> Void)?
    var canDrop: ((Data) -> Bool)?
    var receiveDrop: ((Data) -> Bool)?
    var targeted: ((Bool) -> Void)?

    func makeNSView(context: Context) -> Surface { Surface() }
    func updateNSView(_ view: Surface, context: Context) {
        view.input = self
        view.setAccessibilityLabel(label + (recordingCount.map { ", \($0)개 녹음" } ?? ""))
        view.setAccessibilityRole(select == nil ? .group : .button)
        view.setAccessibilityValue(selected ? "선택됨" : "")
        if canDrop != nil { view.registerForDraggedTypes([Surface.itemType]) }
        else { view.unregisterDraggedTypes() }
    }

    // NSTableView routes embedded NSButtons to their native pointer surface.
    // Keep the control layout-neutral and invisible; SwiftUI draws the row.
    final class Surface: NSButton, NSDraggingSource {
        override var intrinsicContentSize: NSSize {
            NSSize(width: NSView.noIntrinsicMetric, height: NSView.noIntrinsicMetric)
        }
        override func draw(_ dirtyRect: NSRect) {}

        static let itemType = NSPasteboard.PasteboardType("io.github.sangdnog.vemo.library-item")
        var input: VemoLibraryItemInteraction?
        private var pressedAt: NSPoint?
        private var dragged = false
        private var sourceData: Data?
        override var acceptsFirstResponder: Bool { input?.select != nil }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
        override func isAccessibilityElement() -> Bool { true }
        override func accessibilityPerformPress() -> Bool {
            guard let select = input?.select else { return false }
            select(); return true
        }
        override func menu(for event: NSEvent) -> NSMenu? {
            if let menu = super.menu(for: event) { return menu }
            var ancestor = superview
            while let view = ancestor {
                if let menu = view.menu(for: event) { return menu }
                ancestor = view.superview
            }
            return nil
        }
        override func mouseDown(with event: NSEvent) {
            pressedAt = convert(event.locationInWindow, from: nil)
            dragged = false
            if acceptsFirstResponder { window?.makeFirstResponder(self) }
        }
        override func mouseUp(with event: NSEvent) {
            defer { pressedAt = nil; dragged = false }
            guard pressedAt != nil, !dragged, bounds.contains(convert(event.locationInWindow, from: nil)) else { return }
            input?.select?()
        }
        override func mouseDragged(with event: NSEvent) {
            guard let start = pressedAt, !dragged else { return }
            let point = convert(event.locationInWindow, from: nil)
            guard hypot(point.x - start.x, point.y - start.y) >= 4,
                  let data = input?.dragData?(), data.count <= 4096 else { return }
            dragged = true
            sourceData = data
            let item = NSPasteboardItem()
            item.setData(data, forType: Self.itemType)
            let draggingItem = NSDraggingItem(pasteboardWriter: item)
            let size = NSSize(width: min(max(bounds.width, 120), 260), height: 34)
            let title = input?.label ?? "폴더"
            let image = NSImage(size: size, flipped: false) { rect in
                NSColor.controlBackgroundColor.withAlphaComponent(0.94).setFill()
                NSBezierPath(roundedRect: rect, xRadius: 8, yRadius: 8).fill()
                (title as NSString).draw(in: rect.insetBy(dx: 10, dy: 8), withAttributes: [
                    .font: NSFont.systemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor.labelColor])
                return true
            }
            draggingItem.setDraggingFrame(NSRect(origin: NSPoint(x: point.x - 12, y: point.y - 17), size: size), contents: image)
            beginDraggingSession(with: [draggingItem], event: event, source: self)
        }
        override func keyDown(with event: NSEvent) {
            if event.keyCode == 36 || event.keyCode == 49 || event.keyCode == 76 { input?.select?() }
            else { super.keyDown(with: event) }
        }
        func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
            context == .withinApplication ? .move : []
        }
        func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
            if let sourceData { input?.dragEnded?(sourceData) }
            sourceData = nil; pressedAt = nil; dragged = false
        }
        private func incoming(_ sender: any NSDraggingInfo) -> Data? {
            guard let items = sender.draggingPasteboard.pasteboardItems, items.count == 1,
                  let data = items[0].data(forType: Self.itemType), data.count <= 4096 else { return nil }
            return data
        }
        override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation { draggingUpdated(sender) }
        override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
            let valid = incoming(sender).map { input?.canDrop?($0) == true } == true
            input?.targeted?(valid)
            return valid ? .move : []
        }
        override func draggingExited(_ sender: (any NSDraggingInfo)?) { input?.targeted?(false) }
        override func prepareForDragOperation(_ sender: any NSDraggingInfo) -> Bool {
            incoming(sender).map { input?.canDrop?($0) == true } == true
        }
        override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
            input?.targeted?(false)
            guard let data = incoming(sender), input?.canDrop?(data) == true else { return false }
            return input?.receiveDrop?(data) == true
        }
    }
}
