#!/usr/bin/env python3
"""Exercise the shipping reopen delegate/coordinator with isolated AppKit windows."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
ui = root / 'macos/Sources/VoiceMemoTranscriber'
app = (ui / 'App.swift').read_text()
reopen = next(line for line in app.splitlines() if 'func applicationShouldHandleReopen' in line)
delegate_header = next(line for line in app.splitlines() if 'class AppDelegate:' in line)
close_policy = next(line for line in app.splitlines() if 'func applicationShouldTerminateAfterLastWindowClosed' in line)
# Both menu entry points must share the tested library coordinator; the scene is singleton.
assert app.count('Button("앱 열기") { delegate.libraryWindow.show() }') == 2
assert 'Window("Vemo", id: "library")' in app
assert '.onAppear { delegate.libraryWindow.openScene = { openWindow(id: "library") } }' in app
harness = r'''
import AppKit
import SwiftUI
/* DELEGATE HEADER */
    let libraryWindow = LibraryWindowCoordinator()
    /* REOPEN */
    /* CLOSE POLICY */
}
@MainActor final class FixtureWindow: NSWindow {
    var closed = false
    override func close() { closed = true; super.close() }
}
@main struct Checks {
    @MainActor static func main() {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        func check(_ condition: Bool, _ message: String) {
            guard condition else { print("FAIL: \(message)"); exit(1) }
            print("PASS: \(message)")
        }
        func window(_ title: String) -> FixtureWindow {
            let result = FixtureWindow(contentRect: NSRect(x:-10000,y:-10000,width:300,height:200), styleMask:[.titled,.closable,.miniaturizable], backing:.buffered, defer:false)
            result.title = title
            result.isReleasedWhenClosed = false
            return result
        }
        let delegate = ReopenDelegate()
        let coordinator = delegate.libraryWindow
        check(!delegate.applicationShouldTerminateAfterLastWindowClosed(app), "red-X preserves the running application")
        check(delegate.applicationShouldHandleReopen(app,hasVisibleWindows:false), "startup without scene callback permits default handling")
        let settings = window("Settings fixture")
        settings.orderFront(nil)
        var opens = 0
        var library: FixtureWindow?
        coordinator.openScene = {
            opens += 1
            if let library, !library.closed { library.makeKeyAndOrderFront(nil); return }
            let created = window("Library fixture")
            // Use the actual registration bridge as SwiftUI's background view does.
            created.contentView = LibraryWindowRegistration.Surface(coordinator:coordinator)
            created.makeKeyAndOrderFront(nil)
            library = created
        }
        check(!delegate.applicationShouldHandleReopen(app,hasVisibleWindows:true), "settings-only reopen is handled without AppKit duplicate handling")
        check(opens == 1 && library?.isVisible == true, "missing library opens through scene callback even with Settings visible")
        let first = library!
        coordinator.show()
        check(opens == 1 && library === first, "menu open reuses existing library")
        first.orderOut(nil)
        _ = delegate.applicationShouldHandleReopen(app,hasVisibleWindows:true)
        check(first.isVisible && library === first && opens == 2, "ordered-out library reuses singleton scene without another window")
        first.miniaturize(nil)
        let until = Date().addingTimeInterval(2)
        while !first.isMiniaturized && Date() < until { _ = RunLoop.main.run(mode:.default,before:Date().addingTimeInterval(0.01)) }
        check(first.isMiniaturized, "fixture really is minimized")
        _ = delegate.applicationShouldHandleReopen(app,hasVisibleWindows:true)
        check(!first.isMiniaturized && first.isVisible && opens == 2, "minimized library is restored rather than duplicated")
        first.close()
        check(!first.isVisible, "fixture closes through native window lifecycle")
        _ = delegate.applicationShouldHandleReopen(app,hasVisibleWindows:true)
        check(opens == 3 && library !== first && library?.isVisible == true, "closed library reopens through scene callback while old window is still retained")
        check(settings.isVisible, "Settings window is preserved")
        library!.close()
        settings.close()
        _ = delegate.applicationShouldHandleReopen(app,hasVisibleWindows:false)
        check(opens == 4 && library?.isVisible == true, "no remaining visible windows still recreates library")
        library!.close()

    }
}
'''.replace('/* DELEGATE HEADER */', delegate_header.replace('AppDelegate:', 'ReopenDelegate:')).replace('/* REOPEN */', reopen).replace('/* CLOSE POLICY */', close_policy)
scene_harness = r'''
import SwiftUI
/* SCENE DELEGATE HEADER */
    let libraryWindow = LibraryWindowCoordinator()
    var started = false
    /* CLOSE POLICY */
    /* REOPEN */
    func check(_ condition: Bool, _ message: String) {
        guard condition else { print("FAIL: \(message)"); exit(1) }
        print("PASS: \(message)")
    }
    func verify() async {
        guard !started else { return }; started = true
        try? await Task.sleep(for:.milliseconds(300))
        let first = NSApp.windows.first { $0.title == "Reopen scene fixture" }!
        first.close()
        try? await Task.sleep(for:.milliseconds(200))
        _ = applicationShouldHandleReopen(NSApp,hasVisibleWindows:false)
        try? await Task.sleep(for:.milliseconds(400))
        let restored = NSApp.windows.filter { $0.title == "Reopen scene fixture" && $0.isVisible }
        check(restored.count == 1,"actual SwiftUI scene callback survives closing library content")
        libraryWindow.show(); libraryWindow.show()
        try? await Task.sleep(for:.milliseconds(200))
        check(NSApp.windows.filter { $0.title == "Reopen scene fixture" && $0.isVisible }.count == 1,"actual singleton SwiftUI scene does not duplicate")
        restored[0].close()
        try? await Task.sleep(for:.milliseconds(200))
        libraryWindow.show()
        try? await Task.sleep(for:.milliseconds(400))
        check(NSApp.windows.contains { $0.title == "Reopen scene fixture" && $0.isVisible },"menu route reopens actual scene again")
        exit(0)
    }
}
@main struct SceneChecks: App {
    @NSApplicationDelegateAdaptor(FixtureDelegate.self) var delegate
    @Environment(\.openWindow) private var openWindow
    var body: some Scene {
        Window("Reopen scene fixture",id:"library") {
            Text("Synthetic reopen test — no Vemo data").frame(width:300,height:100)
                .background(LibraryWindowRegistration(coordinator:delegate.libraryWindow))
                .onAppear {
                    delegate.libraryWindow.openScene = { openWindow(id:"library") }
                    Task { await delegate.verify() }
                }
        }
    }
}
'''.replace('/* SCENE DELEGATE HEADER */', delegate_header.replace('AppDelegate:', 'FixtureDelegate:')).replace('/* CLOSE POLICY */', close_policy).replace('/* REOPEN */', reopen)
with tempfile.TemporaryDirectory(prefix='VemoReopenChecks-', dir=root/'macos') as tmp:
    out = Path(tmp)
    path = out / 'Checks.swift'
    path.write_text(harness)
    subprocess.run(['swiftc','-swift-version','6','-parse-as-library',str(path),str(ui/'LibraryWindowCoordinator.swift'),'-o',str(out/'checks')],check=True)
    subprocess.run([str(out/'checks')],check=True,timeout=20)
    scene_path = out / 'SceneChecks.swift'
    scene_path.write_text(scene_harness)
    subprocess.run(['swiftc','-swift-version','6','-parse-as-library',str(scene_path),str(ui/'LibraryWindowCoordinator.swift'),'-o',str(out/'scene-checks')],check=True)
    subprocess.run([str(out/'scene-checks')],check=True,timeout=20)
