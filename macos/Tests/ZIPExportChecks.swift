import Foundation
import Darwin
import NativeCore

private actor ExportGate {
    private var continuation: CheckedContinuation<Void,Never>?
    private var entered = false
    func pause() async {
        entered = true
        await withCheckedContinuation { continuation = $0 }
    }
    func waitUntilEntered() async { while !entered { await Task.yield() } }
    func resume() { continuation?.resume(); continuation = nil }
}

@MainActor private final class Heartbeat { var ticks = 0; var compressionStart = 0 }

@main struct ZIPExportChecks {
    static func expect(_ value: Bool, _ message: String) throws {
        guard value else { throw NSError(domain:message,code:1) }
    }
    static func job(_ id: String, root: URL, status: String = "completed", title: String = "../같은/제목: 녹음") throws -> Job {
        try JSONDecoder().decode(Job.self,from:JSONSerialization.data(withJSONObject:[
            "id":id,"uuid":id,"topic":title,"status":status,"stage":"completed","elapsed_seconds":0,
            "title_status":"completed","alignment_status":"completed",
            "audio_path":root.appendingPathComponent("audio.m4a").path,
            "artifact_path":root.appendingPathComponent("transcript.json").path
        ]))
    }
    static func main() async throws {
        let fm = FileManager.default
        let root = URL(fileURLWithPath:fm.currentDirectoryPath,isDirectory:true).appendingPathComponent("macos/.build/ZIPChecks-" + UUID().uuidString)
        defer { try? fm.removeItem(at:root) }
        try fm.createDirectory(at:root,withIntermediateDirectories:true)
        let destination = root.appendingPathComponent("destination")
        try fm.createDirectory(at:destination,withIntermediateDirectories:true)
        let audio = Data("synthetic-original-audio".utf8)
        let transcript = Data(#"{"text":"정확한 원문\nEnglish.","segments":[{"start_s":1,"end_s":2,"text":"정확한 원문"}],"alignment_status":"completed","extra":"retained"}"#.utf8)
        try audio.write(to:root.appendingPathComponent("audio.m4a"))
        try transcript.write(to:root.appendingPathComponent("transcript.json"))
        try Data("excluded".utf8).write(to:root.appendingPathComponent("queue.sqlite"))
        let jobs = try [job("b",root:root),job("a",root:root),job("running",root:root,status:"running")]
        let date = Date(timeIntervalSince1970:1_800_000_000)
        let first = try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date)
        let firstBytes = try Data(contentsOf:first)
        let second = try await RecordingZIPExport.export(jobs:jobs.reversed(),stateRoot:root,destination:destination,date:date)
        try expect(first != second,"collision does not overwrite")
        try expect(try Data(contentsOf:first) == firstBytes,"first archive unchanged")
        let extracted = root.appendingPathComponent("extracted")
        let unzip = Process(); unzip.executableURL = URL(fileURLWithPath:"/usr/bin/ditto")
        unzip.arguments = ["-x","-k",first.path,extracted.path]; try unzip.run(); unzip.waitUntilExit()
        try expect(unzip.terminationStatus == 0,"archive integrity")
        let folders = try fm.contentsOfDirectory(at:extracted,includingPropertiesForKeys:nil).sorted { $0.lastPathComponent < $1.lastPathComponent }
        try expect(folders.count == 2,"completed jobs only")
        for folder in folders {
            try expect(!folder.lastPathComponent.contains("/") && !folder.lastPathComponent.contains(":"),"safe folder names")
            let files = try fm.contentsOfDirectory(atPath:folder.path).sorted()
            try expect(files == ["original.m4a","transcript.json","transcript.md","transcript.txt"],"only requested artifacts")
            try expect(try Data(contentsOf:folder.appendingPathComponent("original.m4a")) == audio,"original bytes preserved")
            try expect(try Data(contentsOf:folder.appendingPathComponent("transcript.json")) == transcript,"full segment JSON preserved")
            try expect(try String(contentsOf:folder.appendingPathComponent("transcript.txt"),encoding:.utf8).contains("정확한 원문\nEnglish."),"transcript text included")
        }
        let extracted2 = root.appendingPathComponent("extracted2")
        let unzip2 = Process(); unzip2.executableURL = URL(fileURLWithPath:"/usr/bin/ditto")
        unzip2.arguments = ["-x","-k",second.path,extracted2.path]; try unzip2.run(); unzip2.waitUntilExit()
        try expect(try fm.contentsOfDirectory(atPath:extracted2.path).sorted() == folders.map(\.lastPathComponent),"deterministic folders")
        // A MainActor ticker must keep running while the exporter stages/compresses off-thread.
        try Data(repeating:65,count:16 * 1024 * 1024).write(to:root.appendingPathComponent("audio.m4a"))
        let heartbeat = await MainActor.run { Heartbeat() }
        let ticker = Task { @MainActor in
            while !Task.isCancelled { heartbeat.ticks += 1; await Task.yield() }
        }
        _ = try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date) { message in
            if message.hasPrefix("ZIP 압축 중") {
                await MainActor.run { heartbeat.compressionStart = heartbeat.ticks }
            }
        }
        ticker.cancel(); await ticker.value
        let responsive = await MainActor.run { heartbeat.ticks > heartbeat.compressionStart }
        try expect(responsive,"MainActor heartbeat continues during ZIP export")
        try audio.write(to:root.appendingPathComponent("audio.m4a"))
        let gate = ExportGate()
        let cancellation = Task {
            try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date) { message in
                if message == "ZIP 무결성 확인 중" { await gate.pause() }
            }
        }
        await gate.waitUntilEntered()
        let beforeCancel = try fm.contentsOfDirectory(atPath:destination.path).filter { $0.hasSuffix(".zip") }.sorted()
        cancellation.cancel()
        await gate.resume()
        do { _ = try await cancellation.value; throw NSError(domain:"cancel accepted",code:99) }
        catch { try expect(error is CancellationError,"cancelled export is awaited and throws") }
        try expect(try fm.contentsOfDirectory(atPath:destination.path).sorted() == beforeCancel,"awaited cancellation removes partial ZIP")
        try fm.removeItem(at:root.appendingPathComponent("audio.m4a"))
        let before = try fm.contentsOfDirectory(atPath:destination.path).sorted()
        do { _ = try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date); throw NSError(domain:"missing audio accepted",code:99) }
        catch { try expect((error as NSError).code != 99,"missing audio fails whole export") }
        try expect(try fm.contentsOfDirectory(atPath:destination.path).sorted() == before,"failed export publishes no ZIP/temp")
        try audio.write(to:root.appendingPathComponent("audio.m4a"))
        try fm.removeItem(at:root.appendingPathComponent("transcript.json"))
        do { _ = try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date); throw NSError(domain:"missing transcript accepted",code:99) }
        catch { try expect((error as NSError).code != 99,"missing transcript fails whole export") }
        try expect(try fm.contentsOfDirectory(atPath:destination.path).sorted() == before,"missing transcript publishes nothing")
        let artifact = root.appendingPathComponent("transcript.json")
        for data in [Data(repeating:32,count:WireCodec.limit + 1),Data(#"{"text":" \n ","segments":[],"alignment_status":"completed"}"#.utf8)] {
            try data.write(to:artifact)
            do { _ = try await RecordingZIPExport.export(jobs:jobs,stateRoot:root,destination:destination,date:date); throw NSError(domain:"invalid metadata accepted",code:99) }
            catch { try expect((error as NSError).code != 99,"oversized/empty transcript rejected") }
            try expect(try fm.contentsOfDirectory(atPath:destination.path).sorted() == before,"invalid transcript publishes nothing")
        }
        print("PASS: ZIP naming, inclusion, deterministic folders, exact bytes, missing-input failure, no overwrite integrity, bounded metadata, empty rejection, cancellation cleanup and MainActor heartbeat")
    }
}
