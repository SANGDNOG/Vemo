import Foundation
import Darwin
import NativeCore
func lifecycleChecks(root: URL) async throws {
    let ready = root.appendingPathComponent("child.pid")
    let child = Process(); child.executableURL = URL(fileURLWithPath:"/bin/sh")
    // Synthetic process-only fixture. No audio/jobs/models are embedded in the app.
    child.arguments = ["-c", "trap '' TERM; /bin/sh -c 'trap \"\" TERM; echo $$ > \"$1\"; while :; do /bin/sleep 1; done' fixture \"$1\" & wait", "fixture", ready.path]
    child.standardInput = FileHandle.nullDevice; child.standardOutput = FileHandle.nullDevice; child.standardError = FileHandle.nullDevice
    try child.run()
    for _ in 0..<100 { if FileManager.default.fileExists(atPath:ready.path) { break }; try await Task.sleep(for:.milliseconds(10)) }
    let pid = Int32(try String(contentsOf:ready,encoding:.utf8).trimmingCharacters(in:.whitespacesAndNewlines))!
    let captured = ProcessCleanup.descendants(of:child.processIdentifier)
    let start = Date(); let stopped = await ProcessCleanup.stop(child,captured:captured)
    try check(stopped && !child.isRunning, "bounded terminate kill reaps stubborn engine fixture")
    try check(Date().timeIntervalSince(start) < 6, "process cleanup deadline is bounded")
    try check(kill(pid,0) == -1 && errno == ESRCH, "stubborn descendant does not remain alive")
    let watcher = FolderWatcher(), incoming = root.appendingPathComponent("watch")
    try FileManager.default.createDirectory(at:incoming,withIntermediateDirectories:true)
    let file = incoming.appendingPathComponent("incoming.wav"); try wave().write(to:file)
    try check(try await watcher.stableFiles(in:incoming).isEmpty, "watched files wait for a stable interval")
    try await Task.sleep(for:.milliseconds(2100))
    try check(try await watcher.stableFiles(in:incoming).count == 1, "stable watched audio is discoverable")
    await watcher.accepted(file)
    try check(try await watcher.stableFiles(in:incoming).isEmpty, "watched file accepted once per revision")
    var changed = wave(); changed.append(1); try changed.write(to:file)
    try check(try await watcher.stableFiles(in:incoming).isEmpty, "changed watched revision waits again")
}
