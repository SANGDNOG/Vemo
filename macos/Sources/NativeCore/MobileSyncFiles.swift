import Foundation
import Darwin
import CryptoKit

// Cloud paths are traversed with openat/O_NOFOLLOW. Keeping directory descriptors
// pins each operation to the checked directory even if a provider renames a parent.
enum MobileSyncFiles {
    static let maximumAudioBytes: Int64 = 16 * 1024 * 1024 * 1024
    final class Directory {
        let fd: Int32
        let url: URL
        init(_ url: URL) throws {
            try BrokerFiles.noLinks(url)
            fd = open(url.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard fd >= 0 else { throw BrokerFailure("sync_root_unavailable", "연결한 동기화 폴더에 접근할 수 없습니다.") }
            self.url = url
        }
        private init(fd: Int32, url: URL) { self.fd = fd; self.url = url }
        deinit { close(fd) }
        func child(_ name: String, create: Bool = false) throws -> Directory {
            _ = try Validation.component(name)
            if create, mkdirat(fd, name, 0o700) != 0, errno != EEXIST { throw BrokerFiles.fail() }
            let child = openat(fd, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard child >= 0 else { throw BrokerFailure("unsafe_sync_directory", "동기화 하위 폴더에 접근할 수 없습니다.") }
            if create, fsync(fd) != 0 { close(child); throw BrokerFiles.fail() }
            return Directory(fd: child, url: url.appendingPathComponent(name, isDirectory: true))
        }
        func names() throws -> [String] {
            // fdopendir owns its descriptor; open '.' instead of dup so offsets are independent.
            let copy = openat(fd, ".", O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            guard copy >= 0 else { throw BrokerFiles.fail() }
            guard let stream = fdopendir(copy) else { close(copy); throw BrokerFiles.fail() }
            defer { closedir(stream) }
            var names: [String] = []
            errno = 0
            while let entry = readdir(stream) {
                let name = withUnsafePointer(to: &entry.pointee.d_name) { pointer in
                    pointer.withMemoryRebound(to: CChar.self, capacity: Int(MAXNAMLEN) + 1) { String(cString: $0) }
                }
                if name != ".", name != ".." { names.append(name) }
                guard names.count <= 50_000 else { throw BrokerFailure("sync_item_limit", "동기화 항목 수가 제한을 초과했습니다.") }
                errno = 0
            }
            guard errno == 0 else { throw BrokerFiles.fail() }
            return names.sorted()
        }
        func exists(_ name: String) throws -> Bool {
            _ = try Validation.component(name)
            var info = stat()
            if fstatat(fd, name, &info, AT_SYMLINK_NOFOLLOW) == 0 { return true }
            guard errno == ENOENT else { throw BrokerFiles.fail() }
            return false
        }
        private func available(_ info: stat) throws {
            guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { throw BrokerFailure("unsafe_sync_file", "일반 모바일 파일만 지원합니다.") }
            guard info.st_flags & UInt32(SF_DATALESS) == 0 else { throw BrokerFailure("waiting_for_download", "동기화 파일의 로컬 다운로드를 기다리고 있습니다.") }
        }
        func readFD(_ name: String) throws -> Int32 {
            _ = try Validation.component(name)
            var before = stat()
            guard fstatat(fd, name, &before, AT_SYMLINK_NOFOLLOW) == 0 else { throw BrokerFailure("source_incomplete", "모바일 파일이 아직 도착하지 않았습니다.") }
            try available(before)
            // File-provider download metadata is consulted before opening content.
            let path = url.appendingPathComponent(name)
            try BrokerFiles.noLinks(path)
            let metadata = try path.resourceValues(forKeys: [.isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey])
            guard LocalFileAvailability.isDownloaded(flags: before.st_flags, ubiquitous: metadata.isUbiquitousItem == true, status: metadata.ubiquitousItemDownloadingStatus) else { throw BrokerFailure("waiting_for_download", "동기화 파일의 로컬 다운로드를 기다리고 있습니다.") }
            let input = openat(fd, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard input >= 0 else { throw BrokerFiles.fail() }
            var after = stat()
            do {
                guard fstat(input, &after) == 0 else { throw BrokerFiles.fail() }
                try available(after)
                guard FileStamp(before) == FileStamp(after) else { throw BrokerFailure("source_changing", "모바일 파일이 변경 중입니다.") }
            } catch { close(input); throw error }
            return input
        }
        func unchanged(_ name: String, stamp: FileStamp) throws {
            var current = stat()
            guard fstatat(fd, name, &current, AT_SYMLINK_NOFOLLOW) == 0, FileStamp(current) == stamp else { throw BrokerFailure("source_changing", "동기화 중 파일이 변경되었습니다. 다음 검사에서 다시 시도합니다.") }
        }
        func read(_ name: String) throws -> Data {
            let input = try readFD(name), before = try BrokerFiles.stamp(input)
            let handle = FileHandle(fileDescriptor: input, closeOnDealloc: true)
            guard before.size <= WireCodec.limit else { throw BrokerFailure("oversize_file", "모바일 JSON이 8 MiB 제한을 초과했습니다.") }
            var data = Data()
            while let block = try handle.read(upToCount: min(65_536, WireCodec.limit + 1 - data.count)), !block.isEmpty {
                data.append(block)
                guard data.count <= WireCodec.limit else { throw BrokerFailure("oversize_file", "모바일 JSON이 8 MiB 제한을 초과했습니다.") }
            }
            guard before == (try BrokerFiles.stamp(input)), Int64(data.count) == before.size else { throw BrokerFailure("source_changing", "모바일 JSON이 변경 중입니다.") }
            try unchanged(name, stamp: before)
            return data
        }
        func copyAudio(_ name: String, to outputURL: URL, bytes: Int64, hash: String) throws {
            let input = try readFD(name), before = try BrokerFiles.stamp(input)
            let source = FileHandle(fileDescriptor: input, closeOnDealloc: true)
            guard before.size == bytes, bytes > 0, bytes <= maximumAudioBytes else { throw BrokerFailure("audio_size_mismatch", "모바일 오디오 크기가 완료 정보와 다릅니다.") }
            let output = open(outputURL.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard output >= 0 else { throw BrokerFiles.fail() }
            let target = FileHandle(fileDescriptor: output, closeOnDealloc: true)
            var digest = SHA256(), count: Int64 = 0
            while let block = try source.read(upToCount: 1_048_576), !block.isEmpty {
                count += Int64(block.count)
                guard count <= bytes else { throw BrokerFailure("source_changing", "모바일 오디오가 변경 중입니다.") }
                digest.update(data: block); try target.write(contentsOf: block)
            }
            guard fsync(output) == 0, count == bytes, before == (try BrokerFiles.stamp(input)) else { throw BrokerFailure("source_changing", "모바일 오디오가 변경 중입니다.") }
            try unchanged(name, stamp: before)
            guard digest.finalize().map({ String(format: "%02x", $0) }).joined() == hash else { throw BrokerFailure("audio_hash_mismatch", "모바일 오디오 해시가 완료 정보와 다릅니다.") }
        }
        func atomic(_ data: Data, name: String, replace: Bool) throws {
            _ = try Validation.component(name)
            guard data.count <= WireCodec.limit else { throw BrokerFailure("oversize_file", "모바일 JSON이 8 MiB 제한을 초과했습니다.") }
            let temporary = ".vemo-" + UUID().uuidString.lowercased() + ".tmp"
            let output = openat(fd, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard output >= 0 else { throw BrokerFiles.fail() }
            let target = FileHandle(fileDescriptor: output, closeOnDealloc: true)
            defer { unlinkat(fd, temporary, 0) }
            try target.write(contentsOf: data)
            guard fsync(output) == 0 else { throw BrokerFiles.fail() }
            if replace {
                // Never follow an existing result symlink, including on retry.
                if try exists(name) { let check = try readFD(name); close(check) }
                guard renameat(fd, temporary, fd, name) == 0 else { throw BrokerFiles.fail() }
            } else {
                guard linkat(fd, temporary, fd, name, 0) == 0 else { throw BrokerFailure("processor_conflict", "동기화 폴더에 처리기 등록이 이미 있습니다.") }
                guard unlinkat(fd, temporary, 0) == 0 else { throw BrokerFiles.fail() }
            }
            guard fsync(fd) == 0 else { throw BrokerFiles.fail() }
        }
    }
}
