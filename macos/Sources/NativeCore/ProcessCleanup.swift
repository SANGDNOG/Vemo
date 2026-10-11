import Foundation
import Darwin
public enum ProcessCleanup {
    public struct Identity: Sendable {
        let pid: Int32; let parent: Int32; let group: Int32; let seconds: UInt64; let micros: UInt64
    }
    private static func identity(_ pid: Int32) -> Identity? {
        var info = proc_bsdinfo()
        let count = proc_pidinfo(pid,PROC_PIDTBSDINFO,0,&info,Int32(MemoryLayout<proc_bsdinfo>.size))
        guard count == MemoryLayout<proc_bsdinfo>.size else { return nil }
        return Identity(pid:pid,parent:Int32(info.pbi_ppid),group:Int32(info.pbi_pgid),seconds:info.pbi_start_tvsec,micros:info.pbi_start_tvusec)
    }
    public static func descendants(of pid: Int32) -> [Identity] {
        let count = proc_listallpids(nil,0)
        guard count > 0 else { return [] }
        var pids = [Int32](repeating:0,count:Int(count)+256)
        let actual = pids.withUnsafeMutableBytes { proc_listallpids($0.baseAddress,Int32($0.count)) }
        let all = pids.prefix(max(0,Int(actual))).compactMap { identity($0) }
        var parents: Set<Int32> = [pid], result: [Identity] = []
        for _ in 0..<32 { let next = all.filter { parents.contains($0.parent) && !parents.contains($0.pid) }; if next.isEmpty { break }; result += next; parents.formUnion(next.map(\.pid)) }
        if let root = identity(pid) { result.append(root) }; return result
    }
    private static func matches(_ old: Identity) -> Bool {
        guard let current = identity(old.pid) else { return false }
        return current.seconds == old.seconds && current.micros == old.micros && current.group == old.group
    }
    private static func signal(_ identities: [Identity], _ value: Int32) {
        var groups: Set<Int32> = []
        for item in identities where matches(item) {
            // Only sessions led by an owned descendant are group targets; never signal the app group.
            if item.group > 1, item.group != getpgrp(), identities.contains(where: { $0.pid == item.group }) { groups.insert(item.group) }
            _ = kill(item.pid,value)
        }
        for group in groups { _ = kill(-group,value) }
    }
    public static func stop(_ process: Process, captured: [Identity] = []) async -> Bool {
        var identities = captured
        identities += descendants(of:process.processIdentifier)
        // EOF/shutdown was already sent; allow the engine supervisor to stop/checkpoint children.
        for _ in 0..<20 { if !process.isRunning { break }; try? await Task.sleep(for:.milliseconds(100)) }
        signal(identities,SIGTERM)
        for _ in 0..<10 { if !process.isRunning && !identities.contains(where:matches) { break }; try? await Task.sleep(for:.milliseconds(100)) }
        signal(identities,SIGKILL)
        for _ in 0..<20 { if !process.isRunning { break }; try? await Task.sleep(for:.milliseconds(100)) }
        return !process.isRunning && !identities.contains(where:matches)
    }
}
