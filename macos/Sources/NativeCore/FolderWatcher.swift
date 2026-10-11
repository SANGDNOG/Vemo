import Foundation
public actor FolderWatcher {
    private struct Observation { let stamp: FileStamp; let firstSeen: Date; var accepted: Bool; var failed: Bool = false }
    private var observations: [String:Observation] = [:]
    private var folder: String?
    public init() {}
    public func stableFiles(in directory: URL) throws -> [URL] {
        try scan(in:directory).files
    }
    public struct Scan: Sendable { public let files: [URL]; public let waitingForDownload: Int; public let changingCount: Int; public let unavailableCount: Int }
    public func scan(in directory: URL, now: Date = Date()) throws -> Scan {
        try BrokerFiles.noLinks(directory)
        if folder != directory.path { observations.removeAll(); folder = directory.path }
        let urls = try FileManager.default.contentsOfDirectory(at:directory,includingPropertiesForKeys:nil,options:[.skipsHiddenFiles])
        let extensions = Set(["wav","m4a","mp3","aac","caf","aif","aiff","flac","mp4","qta"])
        var result: [URL] = [], seen: Set<String> = []
        var downloading = 0, changing = 0, unavailable = 0
        for url in urls where extensions.contains(url.pathExtension.lowercased()) {
            seen.insert(url.path)
            do {
                guard try LocalFileAvailability.check(url) == .ready else { observations.removeValue(forKey:url.path); downloading += 1; continue }
            } catch { unavailable += 1; continue }
            guard let stamp = try? BrokerFiles.stamp(url), stamp.size > 0 else { unavailable += 1; continue }
            if let previous = observations[url.path], previous.stamp == stamp {
                if previous.failed { unavailable += 1 }
                if !previous.accepted { if now.timeIntervalSince(previous.firstSeen) >= 2 { result.append(url) } else { changing += 1 } }
            } else { observations[url.path] = .init(stamp:stamp,firstSeen:now,accepted:false); changing += 1 }
        }
        observations = observations.filter { seen.contains($0.key) }
        return Scan(files:result,waitingForDownload:downloading,changingCount:changing,unavailableCount:unavailable)
    }
    public func accepted(_ url: URL) { observations[url.path]?.accepted = true; observations[url.path]?.failed = false }
    public func rejected(_ url: URL) { observations[url.path]?.accepted = true; observations[url.path]?.failed = true }
}
