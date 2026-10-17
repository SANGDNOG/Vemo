import SwiftUI
import NativeCore
@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    private var terminating = false
    let libraryWindow = LibraryWindowCoordinator()
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
        guard !terminating else { return .terminateLater }; terminating = true
        Task { await AppModel.shared.flushZIPExport(); await AppModel.shared.flushLibraryChanges(); await AppModel.shared.engine.shutdown(); NSApp.reply(toApplicationShouldTerminate: true) }; return .terminateLater
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { !libraryWindow.show() }
}
@main struct VoiceMemoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var model = AppModel.shared
    @Environment(\.openWindow) private var openWindow
    var body: some Scene {
        Window("Vemo", id: "library") { LibraryView(model: model).tint(VemoTheme.accent).buttonStyle(VemoButtonStyle()).frame(minWidth: 980, minHeight: 540)
                .background(LibraryWindowRegistration(coordinator: delegate.libraryWindow))
                .onAppear { delegate.libraryWindow.openScene = { openWindow(id: "library") } }
                .task { if !ProcessInfo.processInfo.arguments.contains("--diagnostic-only") { model.start() } } }.defaultSize(width: 1200, height: 720)
            .windowToolbarStyle(.unifiedCompact)
            .commands { CommandGroup(replacing: .newItem) { Button("파일 가져오기…") { model.chooseFiles() }.keyboardShortcut("o"); Button("앱 열기") { delegate.libraryWindow.show() } } }
        Settings { SettingsView(model: model).tint(VemoTheme.accent).buttonStyle(VemoButtonStyle()) }
        MenuBarExtra("Vemo", systemImage: "waveform") {
            Text(model.overallStatus)
            Button("앱 열기") { delegate.libraryWindow.show() }
            Button(model.paused ? "처리 재개" : "새 작업 일시 중지") { model.command(model.paused ? "resume" : "pause") }
            Divider(); Button("종료") { model.quit() }.keyboardShortcut("q")
        }
    }
}
