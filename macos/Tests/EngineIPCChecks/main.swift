import Foundation
import NativeCore
actor Seen {
    var kinds: [String] = []
    func record(_ e: WireEvent) { kinds.append(e.kind); print("EVENT",e.kind) }
    func snapshotSeen() -> Bool { kinds.contains("snapshot") }
}
@main struct Probe {
    static func main() async throws {
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/NativeIPC-" + UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        let realRuntime = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/VoiceMemoTranscriber/runtime")
        try FileManager.default.createSymbolicLink(at:root.appendingPathComponent("runtime"),withDestinationURL:realRuntime)
        let engine = EngineClient(), seen = Seen()
        try await engine.start(root:root,acOnly:true,event:{ await seen.record($0) },ended:{ print("ENDED",$0) })
        for _ in 0..<30 { if await seen.snapshotSeen() { break }; try await Task.sleep(for:.milliseconds(100)) }
        let ok = await seen.snapshotSeen(); print("SNAPSHOT_WITHIN_3SEC",ok)
        _ = await engine.shutdown(); try FileManager.default.removeItem(at:root)
        exit(ok ? 0 : 1)
    }
}
