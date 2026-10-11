import Foundation
import NativeCore
import Darwin

func cloudFolderChecks() async throws {
    let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches").appendingPathComponent("Vemo-folder-check-" + UUID().uuidString)
    try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
    defer { try? FileManager.default.removeItem(at:root) }
    let input = root.appendingPathComponent("Recordings"), output = root.appendingPathComponent("Notes")
    try FolderConnection.validate(input:input, output:output)
    try rejects("identical input/output rejected") { try FolderConnection.validate(input:input,output:input) }
    try rejects("output nested in input rejected") { try FolderConnection.validate(input:input,output:input.appendingPathComponent("Notes")) }
    try rejects("input nested in output rejected") { try FolderConnection.validate(input:output.appendingPathComponent("Recordings"),output:output) }
    try FolderConnection.validate(input:root.appendingPathComponent("Input"),output:root.appendingPathComponent("Input2"))
    try FileManager.default.createDirectory(at:input,withIntermediateDirectories:true)
    let lowerAlias = root.appendingPathComponent("recordings")
    if FileManager.default.fileExists(atPath:lowerAlias.path) {
        try rejects("not-yet-created case alias cannot put output inside input") { try FolderConnection.validate(input:input,output:lowerAlias.appendingPathComponent("Notes")) }
    }
    try check(LocalFileAvailability.isDownloaded(flags:0,ubiquitous:false,status:nil), "ordinary local provider file ready")
    try check(!LocalFileAvailability.isDownloaded(flags:UInt32(SF_DATALESS),ubiquitous:false,status:nil), "dataless provider file waits without opening audio")
    try check(!LocalFileAvailability.isDownloaded(flags:0,ubiquitous:true,status:.notDownloaded), "iCloud placeholder waits for download")
    try check(LocalFileAvailability.isDownloaded(flags:0,ubiquitous:true,status:.downloaded), "downloaded iCloud file ready without demanding newest cloud revision")
    let local = root.appendingPathComponent("sample.wav"); try wave().write(to:local)
    try check(try LocalFileAvailability.check(local) == .ready, "local availability inspection accepts real WAV")
    let watcher = FolderWatcher()
    let first = try await watcher.scan(in:root,now:Date(timeIntervalSince1970:100))
    try check(first.files.isEmpty && first.changingCount == 1, "first folder observation waits for stability")
    let ready = try await watcher.scan(in:root,now:Date(timeIntervalSince1970:103))
    try check(ready.files == [local] && ready.waitingForDownload == 0, "complete unchanged file is offered on next scan")
    await watcher.accepted(local)
    try check(try await watcher.scan(in:root,now:Date(timeIntervalSince1970:106)).files.isEmpty, "accepted provider file is not repeatedly imported")
    try Data([0]).append(to:local)
    try check(try await watcher.scan(in:root,now:Date(timeIntervalSince1970:109)).changingCount == 1, "changed file is reobserved instead of silently skipped")
}
private extension Data {
    func append(to url:URL) throws { let handle = try FileHandle(forWritingTo:url); defer { try? handle.close() }; try handle.seekToEnd(); try handle.write(contentsOf:self) }
}
