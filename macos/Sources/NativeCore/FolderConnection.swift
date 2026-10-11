import Foundation
import Darwin

public enum FolderConnection {
    public static func validate(input: URL, output: URL) throws {
        func caseSensitiveVolume(_ url:URL) -> Bool {
            var ancestor = url.standardizedFileURL
            while !FileManager.default.fileExists(atPath:ancestor.path), ancestor.path != "/" { ancestor.deleteLastPathComponent() }
            return (try? ancestor.resourceValues(forKeys:[.volumeSupportsCaseSensitiveNamesKey]).volumeSupportsCaseSensitiveNames) == true
        }
        func comparable(_ url:URL) -> String {
            let path = url.standardizedFileURL.resolvingSymlinksInPath().path.precomposedStringWithCanonicalMapping
            return caseSensitiveVolume(input) && caseSensitiveVolume(output) ? path : path.folding(options:.caseInsensitive,locale:Locale(identifier:"en_US_POSIX"))
        }
        // Resolve existing ancestors and honor volume case rules even when output is not created yet.
        let a = comparable(input)
        let b = comparable(output)
        guard a != b, !a.hasPrefix(b == "/" ? b : b + "/"), !b.hasPrefix(a == "/" ? a : a + "/") else {
            throw BrokerFailure("overlapping_folders", "입력과 결과는 서로 포함되지 않는 별도 폴더를 선택하세요. 예: Vemo/Recordings와 Vemo/Notes")
        }
    }
}

public enum LocalFileAvailability {
    public enum State: Equatable, Sendable { case ready, waitingForDownload }
    public static func isDownloaded(flags: UInt32, ubiquitous: Bool, status: URLUbiquitousItemDownloadingStatus?) -> Bool {
        flags & UInt32(SF_DATALESS) == 0 && (!ubiquitous || status == .current || status == .downloaded)
    }
    // lstat/resource metadata do not open audio contents or trigger a streamed provider read.
    public static func check(_ url: URL) throws -> State {
        try BrokerFiles.noLinks(url)
        var info = stat()
        guard lstat(url.path,&info) == 0, info.st_mode & S_IFMT == S_IFREG else { throw BrokerFailure("unavailable_file", "일반 오디오 파일에 접근할 수 없습니다.") }
        let metadata = try url.resourceValues(forKeys:[.isUbiquitousItemKey,.ubiquitousItemDownloadingStatusKey])
        if isDownloaded(flags:info.st_flags,ubiquitous:metadata.isUbiquitousItem == true,status:metadata.ubiquitousItemDownloadingStatus) { return .ready }
        if metadata.isUbiquitousItem == true { try? FileManager.default.startDownloadingUbiquitousItem(at:url) }
        return .waitingForDownload
    }
}
