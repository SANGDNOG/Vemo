import Foundation
import Darwin

/// One immutable job snapshot per export; file IO and archiving run off MainActor.
public actor RecordingZIPExport {
    public static func export(jobs: [Job], stateRoot: URL, destination: URL, date: Date = Date(),
                              progress: @escaping @Sendable (String) async -> Void = { _ in }) async throws -> URL {
        try await RecordingZIPExport().run(jobs:jobs,stateRoot:stateRoot,destination:destination,date:date,progress:progress)
    }

    private struct Input {
        let job: Job
        let audio: URL
        let stamp: FileStamp
        let artifact: URL
        let artifactStamp: FileStamp
    }

    private func run(jobs: [Job], stateRoot: URL, destination: URL, date: Date,
                     progress: @Sendable (String) async -> Void) async throws -> URL {
        let fm = FileManager.default
        let completed = jobs.filter { $0.status == "completed" && $0.artifact_path != nil }.sorted { $0.id < $1.id }
        guard !completed.isEmpty else { throw NativeFailure.invalid("내보낼 완료된 녹음이 없습니다.") }
        guard Set(completed.map(\.id)).count == completed.count else { throw NativeFailure.invalid("중복된 녹음 ID가 있어 ZIP을 만들지 못했습니다.") }
        try BrokerFiles.noLinks(destination)
        guard try destination.resourceValues(forKeys:[.isDirectoryKey]).isDirectory == true else {
            throw NativeFailure.invalid("ZIP 저장 폴더를 선택하세요.")
        }
        // Preflight every input before staging anything. Missing originals never fall back to PCM.
        var inputs: [Input] = []
        for (index,job) in completed.enumerated() {
            try Task.checkCancellation()
            await progress("입력 확인 \(index + 1)/\(completed.count)")
            do {
                guard let audioPath = job.audio_path, let artifactPath = job.artifact_path else {
                    throw NativeFailure.invalid("원본 오디오 또는 전사 경로가 없습니다.")
                }
                let audio = try Validation.owned(audioPath,root:stateRoot)
                let stamp = try BrokerFiles.stamp(audio)
                guard stamp.size > 0 else { throw NativeFailure.invalid("원본 오디오가 비어 있습니다.") }
                let artifact = try Validation.owned(artifactPath,root:stateRoot)
                let artifactStamp = try BrokerFiles.stamp(artifact)
                _ = try readTranscript(artifact,expected:artifactStamp)
                inputs.append(Input(job:job,audio:audio,stamp:stamp,artifact:artifact,artifactStamp:artifactStamp))
            } catch {
                throw NativeFailure.invalid("\(job.title): ZIP 입력 확인 실패 · \(error.localizedDescription)")
            }
        }
        let scratch = fm.urls(for:.cachesDirectory,in:.userDomainMask)[0].appendingPathComponent("Vemo-ZIP-" + UUID().uuidString)
        let payload = scratch.appendingPathComponent("recordings")
        let temporaryZIP = destination.appendingPathComponent(".vemo-zip-" + UUID().uuidString + ".part")
        defer { try? fm.removeItem(at:scratch); try? fm.removeItem(at:temporaryZIP) }
        try BrokerFiles.directory(payload)
        for (index,input) in inputs.enumerated() {
            try Task.checkCancellation()
            await progress("녹음 복사 \(index + 1)/\(inputs.count)")
            let folder = payload.appendingPathComponent(String(format:"%04d-",index + 1) + safeName(input.job.title))
            try BrokerFiles.directory(folder)
            guard input.stamp == (try BrokerFiles.stamp(input.audio)) else {
                throw NativeFailure.invalid("원본 오디오가 변경되었습니다. 다시 내보내세요.")
            }
            let ext = safeName(input.audio.pathExtension)
            let audioName = input.audio.pathExtension.isEmpty ? "original" : "original." + ext
            _ = try BrokerFiles.stableCopy(input.audio,to:folder.appendingPathComponent(audioName))
            guard input.stamp == (try BrokerFiles.stamp(input.audio)) else {
                throw NativeFailure.invalid("복사 중 원본 오디오가 변경되었습니다. 다시 내보내세요.")
            }
            let (json,transcript) = try readTranscript(input.artifact,expected:input.artifactStamp)
            for format in ["txt","md"] {
                let text = try TranscriptExport.render(transcript,title:input.job.title,format:format)
                try BrokerFiles.writeNew(Data(text.utf8),to:folder.appendingPathComponent("transcript." + format))
            }
            try BrokerFiles.writeNew(json,to:folder.appendingPathComponent("transcript.json"))
        }
        await progress("ZIP 압축 중 · \(inputs.count)개 녹음")
        try execute("/usr/bin/ditto",arguments:["-c","-k","--norsrc","--noextattr","--noqtn",payload.path,temporaryZIP.path],log:scratch.appendingPathComponent("archive.log"))
        try Task.checkCancellation()
        await progress("ZIP 무결성 확인 중")
        try execute("/usr/bin/unzip",arguments:["-tqq",temporaryZIP.path],log:scratch.appendingPathComponent("verify.log"))
        let fd = try BrokerFiles.readFD(temporaryZIP)
        defer { close(fd) }
        guard fsync(fd) == 0 else { throw BrokerFiles.fail() }
        try Task.checkCancellation()
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier:"en_US_POSIX")
        formatter.calendar = Calendar(identifier:.gregorian)
        formatter.dateFormat = "yyyy-MM-dd-HHmmss"
        let base = "Vemo-녹음-" + formatter.string(from:date)
        // Atomic, exclusive rename on the destination filesystem: even concurrent exports cannot overwrite.
        var suffix = 0
        while true {
            let target = destination.appendingPathComponent(base + (suffix == 0 ? "" : "-\(suffix)") + ".zip")
            if renamex_np(temporaryZIP.path,target.path,UInt32(RENAME_EXCL)) == 0 {
                try BrokerFiles.syncDirectory(destination)
                return target
            }
            guard errno == EEXIST else { throw BrokerFiles.fail() }
            suffix += 1
        }
    }

    private func readTranscript(_ url: URL, expected: FileStamp) throws -> (Data,Transcript) {
        guard expected == (try BrokerFiles.stamp(url)) else {
            throw NativeFailure.invalid("전사 파일이 변경되었습니다. 다시 내보내세요.")
        }
        let json = try BrokerFiles.read(url,limit:WireCodec.limit)
        guard expected == (try BrokerFiles.stamp(url)) else {
            throw NativeFailure.invalid("읽는 동안 전사 파일이 변경되었습니다. 다시 내보내세요.")
        }
        let transcript = try JSONDecoder().decode(Transcript.self,from:json)
        guard !transcript.text.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty else {
            throw NativeFailure.invalid("전사문이 비어 있어 ZIP을 만들지 못했습니다.")
        }
        return (json,transcript)
    }

    private func safeName(_ value: String) -> String {
        let forbidden = CharacterSet.controlCharacters.union(CharacterSet(charactersIn:"/\\:*?\"<>|"))
        let clean = value.unicodeScalars.map { forbidden.contains($0) ? "_" : String($0) }.joined()
            .trimmingCharacters(in:CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn:".")))
        // A byte bound leaves room for the ordinal and avoids filesystem component limits.
        var result = ""
        for character in clean {
            guard result.utf8.count + String(character).utf8.count <= 160 else { break }
            result.append(character)
        }
        return result.isEmpty ? "녹음" : result
    }

    private func execute(_ executable: String, arguments: [String], log: URL) throws {
        try BrokerFiles.writeNew(Data(),to:log)
        let output = try FileHandle(forWritingTo:log)
        defer { try? output.close() }
        let process = Process()
        process.executableURL = URL(fileURLWithPath:executable)
        process.arguments = arguments
        process.standardOutput = output; process.standardError = output
        try process.run(); process.waitUntilExit()
        guard process.terminationReason == .exit, process.terminationStatus == 0 else {
            throw NativeFailure.invalid("ZIP 압축 또는 무결성 확인에 실패했습니다. 저장 공간과 폴더 권한을 확인하세요.")
        }
    }
}
