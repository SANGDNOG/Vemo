import SwiftUI

/// Owns only the library window; Settings and panels must never satisfy a reopen.
@MainActor final class LibraryWindowCoordinator: NSObject {
    private weak var window: NSWindow?
    var openScene: (() -> Void)?

    func register(_ window: NSWindow) { self.window = window }

    @discardableResult func show() -> Bool {
        if let window, window.isVisible || window.isMiniaturized {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
        } else {
            guard let openScene else { return false }
            // A singleton SwiftUI Window recreates closed content and reuses
            // ordered-out windows, including ones it retains after close.
            openScene()
        }
        NSApp.unhide(nil)
        NSApp.activate(ignoringOtherApps: true)
        return true
    }
}

/// Capture the NSWindow belonging to the library content, rather than guessing
/// from NSApplication.windows (which also includes settings and auxiliary UI).
struct LibraryWindowRegistration: NSViewRepresentable {
    let coordinator: LibraryWindowCoordinator
    func makeNSView(context: Context) -> Surface { Surface(coordinator: coordinator) }
    func updateNSView(_ nsView: Surface, context: Context) {
        if let window = nsView.window { coordinator.register(window) }
    }
    final class Surface: NSView {
        let coordinator: LibraryWindowCoordinator
        init(coordinator: LibraryWindowCoordinator) {
            self.coordinator = coordinator
            super.init(frame: .zero)
        }
        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let window { coordinator.register(window) }
        }
    }
}
