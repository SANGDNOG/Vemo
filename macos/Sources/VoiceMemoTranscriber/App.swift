import SwiftUI
import NativeCore
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var terminating = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        if ProcessInfo.processInfo.arguments.contains("--diagnostic-only") {
            Task {
                let diagnostic = try? await SourceBroker(root:AppModel.shared.stateRoot).writeDiagnostic()
                let root = AppModel.shared.stateRoot
                do {
                    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                    try JSONEncoder().encode(diagnostic).write(to: root.appendingPathComponent("permission-diagnostic.json"), options: .atomic)
                } catch { /* No source contents or paths are logged. */ }
                NSApp.terminate(nil)
            }
        }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !terminating else { return .terminateNow }; terminating = true
        Task { await AppModel.shared.engine.shutdown(); NSApp.reply(toApplicationShouldTerminate: true) }; return .terminateLater
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { if !flag { sender.windows.first(where: { $0.canBecomeMain })?.makeKeyAndOrderFront(nil) }; return true }
}
@main struct VoiceMemoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel.shared
    @Environment(\.openWindow) private var openWindow
    var body: some Scene {
        WindowGroup("Vemo", id: "library") { LibraryView(model: model).frame(minWidth: 760, minHeight: 540).task { if !ProcessInfo.processInfo.arguments.contains("--diagnostic-only") { model.start() } } }.defaultSize(width: 1040, height: 720)
            .commands { CommandGroup(replacing: .newItem) { Button("파일 가져오기…") { model.chooseFiles() }.keyboardShortcut("o"); Button("앱 열기") { openWindow(id: "library"); NSApp.activate(ignoringOtherApps: true) } } }
        Settings { SettingsView(model: model) }
        MenuBarExtra("Vemo", systemImage: "waveform") {
            Text(model.overallStatus)
            Button("앱 열기") { openWindow(id: "library"); NSApp.activate(ignoringOtherApps: true) }
            Button(model.paused ? "처리 재개" : "새 작업 일시 중지") { model.command(model.paused ? "resume" : "pause") }
            Divider(); Button("종료") { model.quit() }.keyboardShortcut("q")
        }
    }
}
