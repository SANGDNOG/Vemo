import Foundation
public actor FolderWatcher {
    private struct Observation { let stamp: FileStamp; let firstSeen: Date; var accepted: Bool }
    private var observations: [String:Observation] = [:]
    private var folder: String?
    public init() {}
    public func stableFiles(in directory: URL) throws -> [URL] {
        try BrokerFiles.noLinks(directory)
        if folder != directory.path { observations.removeAll(); folder = directory.path }
        let urls = try FileManager.default.contentsOfDirectory(at:directory,includingPropertiesForKeys:nil,options:[.skipsHiddenFiles])
        let extensions = Set(["wav","m4a","mp3","aac","caf","aif","aiff","flac","mp4","qta"])
        var result: [URL] = [], seen: Set<String> = []
        for url in urls where extensions.contains(url.pathExtension.lowercased()) {
            seen.insert(url.path)
            guard let stamp = try? BrokerFiles.stamp(url), stamp.size > 0 else { continue }
            if let previous = observations[url.path], previous.stamp == stamp {
                if !previous.accepted && Date().timeIntervalSince(previous.firstSeen) >= 2 { result.append(url) }
            } else { observations[url.path] = .init(stamp:stamp,firstSeen:Date(),accepted:false) }
        }
        observations = observations.filter { seen.contains($0.key) }; return result
    }
    public func accepted(_ url: URL) { observations[url.path]?.accepted = true }
}
