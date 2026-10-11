import Foundation
import Darwin
import CryptoKit
import AVFoundation

public struct BrokerFailure: Error, LocalizedError, Sendable {
    public let code: String
    public let message: String
    public var errorDescription: String? { message }
    public init(_ code: String, _ message: String) { self.code = code; self.message = message }
}
public struct OwnedSnapshot: Codable, Sendable {
    public let owned_path: String
    public let revision_before: MediaRevision
    public let revision_after: MediaRevision
}
struct FileStamp: Equatable {
    let device: Int32; let inode: UInt64; let size: Int64; let modified: Int64; let changed: Int64
    init(_ s: stat) {
        device = s.st_dev; inode = s.st_ino; size = s.st_size
        modified = Int64(s.st_mtimespec.tv_sec) * 1_000_000_000 + Int64(s.st_mtimespec.tv_nsec)
        changed = Int64(s.st_ctimespec.tv_sec) * 1_000_000_000 + Int64(s.st_ctimespec.tv_nsec)
    }
    var revision: MediaRevision { MediaRevision(size: size, mtime_ns: modified) }
}
// All native broker IO is on broker actors, never on MainActor. No source is opened for writing.
enum BrokerFiles {
    static func fail(_ code: String = "file_io", _ message: String = "파일에 접근하지 못했습니다. 권한과 저장 공간을 확인하세요.") -> BrokerFailure { BrokerFailure(code, message) }
    static func noLinks(_ url: URL) throws {
        guard url.isFileURL, url.path.hasPrefix("/"), !url.path.utf8.contains(0) else { throw fail("unsafe_path", "유효하지 않은 파일 경로입니다.") }
        var path = url.standardizedFileURL
        while path.path != "/" {
            var s = stat()
            if lstat(path.path, &s) == 0 {
                guard s.st_mode & S_IFMT != S_IFLNK else { throw fail("unsafe_path", "심볼릭 링크 경로는 지원하지 않습니다.") }
            } else if errno != ENOENT { throw fail() }
            path.deleteLastPathComponent()
        }
    }
    static func directory(_ url: URL) throws {
        try noLinks(url)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try noLinks(url)
        var s = stat(); guard lstat(url.path, &s) == 0, s.st_mode & S_IFMT == S_IFDIR else { throw fail("unsafe_path", "폴더 경로가 필요합니다.") }
    }
    static func readFD(_ url: URL) throws -> Int32 {
        try noLinks(url)
        let fd = open(url.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        guard fd >= 0 else { throw fail() }
        var s = stat()
        guard fstat(fd, &s) == 0, s.st_mode & S_IFMT == S_IFREG else { close(fd); throw fail("unsafe_file", "일반 파일만 지원합니다.") }
        return fd
    }
    static func stamp(_ fd: Int32) throws -> FileStamp { var s = stat(); guard fstat(fd, &s) == 0 else { throw fail() }; return FileStamp(s) }
    static func stamp(_ url: URL) throws -> FileStamp { let fd = try readFD(url); defer { close(fd) }; return try stamp(fd) }
    static func read(_ url: URL, limit: Int = WireCodec.limit) throws -> Data {
        let fd = try readFD(url); let h = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        let s = try stamp(fd); guard s.size <= limit else { throw fail("oversize_file", "메타데이터 파일이 너무 큽니다.") }
        let d = try h.readToEnd() ?? Data(); guard s == (try stamp(fd)), d.count <= limit else { throw fail("changing_file", "읽는 동안 파일이 변경되었습니다.") }; return d
    }
    static func hash(_ url: URL) throws -> String {
        let fd = try readFD(url); let h = FileHandle(fileDescriptor: fd, closeOnDealloc: true); let before = try stamp(fd)
        var digest = SHA256()
        while let block = try h.read(upToCount: 1_048_576), !block.isEmpty { digest.update(data: block) }
        guard before == (try stamp(fd)), before == (try stamp(url)) else { throw fail("changing_file", "읽는 동안 파일이 변경되었습니다.") }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }
    static func syncDirectory(_ url: URL) throws {
        let fd = open(url.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC); guard fd >= 0 else { throw fail() }; defer { close(fd) }
        guard fsync(fd) == 0 else { throw fail() }
    }
    static func writeNew(_ data: Data, to url: URL) throws {
        try noLinks(url)
        let fd = open(url.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600); guard fd >= 0 else { throw fail("output_conflict", "기존 파일을 덮어쓰지 않았습니다.") }
        let h = FileHandle(fileDescriptor: fd, closeOnDealloc: true); try h.write(contentsOf: data)
        guard fsync(fd) == 0 else { throw fail() }
    }
    static func atomic(_ data: Data, to url: URL) throws {
        try noLinks(url)
        let temp = url.deletingLastPathComponent().appendingPathComponent(".native-\(UUID().uuidString).tmp")
        defer { try? FileManager.default.removeItem(at: temp) }
        try writeNew(data, to: temp)
        guard rename(temp.path, url.path) == 0 else { throw fail() }; try syncDirectory(url.deletingLastPathComponent())
    }
    static func json<T: Encodable>(_ value: T) throws -> Data { let e = JSONEncoder(); e.outputFormatting = [.sortedKeys]; return try e.encode(value) }
    static func lock(_ url: URL) throws -> Int32 {
        try noLinks(url)
        let fd = open(url.path, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard fd >= 0 else { throw fail() }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else { close(fd); throw fail("broker_busy", "다른 앱 작업이 진행 중입니다. 잠시 후 다시 시도하세요.") }; return fd
    }
    static func stableCopy(_ source: URL, to target: URL) throws -> OwnedSnapshot {
        let fd = try readFD(source); let input = FileHandle(fileDescriptor: fd, closeOnDealloc: true); let before = try stamp(fd)
        guard before.size > 0 else { throw fail("source_incomplete", "녹음 파일이 아직 비어 있습니다.") }
        let out = open(target.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard out >= 0 else { throw fail() }
        let output = FileHandle(fileDescriptor: out, closeOnDealloc: true); var success = false
        defer { if !success { try? FileManager.default.removeItem(at: target) } }
        while let block = try input.read(upToCount: 1_048_576), !block.isEmpty { try output.write(contentsOf: block) }
        guard fsync(out) == 0 else { throw fail() }
        let after = try stamp(fd)
        guard before == after, after == (try stamp(source)), (try stamp(out)).size == before.size else { throw fail("source_changing", "녹음이 변경 중입니다. 동기화가 완료되면 다시 시도합니다.") }
        let owned = try stamp(out)
        guard owned == (try stamp(target)) else { throw fail("snapshot_changing", "소유 스냅샷이 변경되었습니다.") }
        try syncDirectory(target.deletingLastPathComponent()); success = true
        return OwnedSnapshot(owned_path: target.path, revision_before: owned.revision, revision_after: owned.revision)
    }
    static func decodeAudio(_ url: URL) throws -> Double {
        do {
            let audio = try AVAudioFile(forReading: url)
            guard audio.length > 0, audio.processingFormat.sampleRate.isFinite, audio.processingFormat.sampleRate > 0,
                  let buffer = AVAudioPCMBuffer(pcmFormat: audio.processingFormat, frameCapacity: 65_536) else { throw fail("invalid_media", "디코딩할 수 있는 음성이 없습니다.") }
            var frames: Int64 = 0
            while audio.framePosition < audio.length { try audio.read(into: buffer); guard buffer.frameLength > 0 else { throw fail("invalid_media", "오디오 파일이 불완전합니다.") }; frames += Int64(buffer.frameLength) }
            guard frames == audio.length else { throw fail("invalid_media", "오디오 파일이 불완전합니다.") }
            return Double(frames) / audio.processingFormat.sampleRate
        } catch { throw fail("invalid_media", "오디오 파일을 끝까지 디코딩하지 못했습니다. 지원되는 완전한 오디오 파일을 선택하세요.") }
    }
}
