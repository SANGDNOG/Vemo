import Foundation
import Darwin
import NativeCore

// Foundation FileHandle.AsyncBytes uses a shared serial IO actor; a silent stderr can block stdout.
// Each pipe gets its own blocking-reader queue, with bounded queued chunks and explicit overflow failure.
private enum EnginePipeReader {
    static func chunks(_ handle: FileHandle, label: String) -> AsyncThrowingStream<Data,Error> {
        AsyncThrowingStream(bufferingPolicy:.bufferingOldest(16)) { continuation in
            DispatchQueue(label:label,qos:.utility).async {
                do {
                    var buffer = [UInt8](repeating:0,count:65_536)
                    while true {
                        let count = buffer.withUnsafeMutableBytes { Darwin.read(handle.fileDescriptor,$0.baseAddress,$0.count) }
                        if count == 0 { break }
                        if count < 0 { if errno == EINTR { continue }; throw NativeFailure.invalid("엔진 pipe를 읽지 못했습니다.") }
                        switch continuation.yield(Data(buffer.prefix(count))) {
                        case .enqueued: break
                        case .dropped: continuation.finish(throwing:NativeFailure.invalid("엔진 응답 처리가 지연되어 연결을 다시 시작해야 합니다.")); return
                        case .terminated: return
                        @unknown default: return
                        }
                    }
                    continuation.finish()
                } catch { continuation.finish(throwing:error) }
            }
        }
    }
    static func drain(_ handle: FileHandle) {
        DispatchQueue(label:"local.voice-memo.engine.stderr",qos:.utility).async {
            var buffer = [UInt8](repeating:0,count:65_536)
            while true {
                let count = buffer.withUnsafeMutableBytes { Darwin.read(handle.fileDescriptor,$0.baseAddress,$0.count) }
                if count <= 0 { if count < 0 && errno == EINTR { continue }; break }
            }
        }
    }
}
actor EngineClient {
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var errors: FileHandle?
    private var readTask: Task<Void,Never>?
    private var monitorTask: Task<Void,Never>?
    private var captured: [ProcessCleanup.Identity] = []
    private var generation = UUID()
    private var cleanup: Task<Bool,Never>?
    private var nativeLock: Int32?
    private var starting = false
    func start(root: URL, acOnly: Bool, event: @escaping @Sendable (WireEvent) async -> Void, ended: @escaping @Sendable (String) async -> Void) async throws {
        guard !starting else { throw NativeFailure.invalid("엔진 시작이 진행 중입니다.") }; starting = true; defer { starting = false }
        if process != nil || cleanup != nil { guard await shutdown() else { throw NativeFailure.invalid("이전 엔진 프로세스 정리를 확인하지 못했습니다. 새 엔진은 시작하지 않았습니다.") } }
        let executable = RuntimePaths.python(root: root)
        guard FileManager.default.isExecutableFile(atPath:executable.path) else { throw NativeFailure.invalid("전용 실행 환경이 없습니다. runtime 설치를 완료한 뒤 엔진을 다시 연결하세요.") }
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
        let lease = open(root.appendingPathComponent("native-session.lock").path,O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC,0o600)
        guard lease >= 0, flock(lease,LOCK_EX | LOCK_NB) == 0 else { if lease >= 0 { close(lease) }; throw NativeFailure.invalid("다른 앱 세션이 실행 중입니다. 실행 중인 앱을 종료한 뒤 다시 연결하세요.") }
        nativeLock = lease
        do {
            // A crashed old app loses its lease; its independently supervised engine may need up to five seconds.
            let writer = open(root.appendingPathComponent("engine.lock").path,O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC,0o600)
            guard writer >= 0 else { throw NativeFailure.invalid("엔진 상태 잠금을 확인하지 못했습니다.") }
            defer { close(writer) }
            var available = false
            for _ in 0..<60 {
                if flock(writer,LOCK_EX | LOCK_NB) == 0 { flock(writer,LOCK_UN); available = true; break }
                try await Task.sleep(for:.milliseconds(100))
            }
            guard available else { throw NativeFailure.invalid("이전 엔진이 아직 정리 중입니다. 두 번째 엔진을 시작하지 않았습니다. 잠시 후 다시 연결하세요.") }
            let p = Process(); p.executableURL = executable
            p.arguments = ["-m","voice_memo_transcriber.service","--state-root",root.path,"--model-root",root.appendingPathComponent("models").path,"--ffmpeg-path",RuntimePaths.decoder().path]
            var environment = ProcessInfo.processInfo.environment; environment["PYTHONUNBUFFERED"] = "1"; environment["PYTHONDONTWRITEBYTECODE"] = "1"; for key in ["PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV"] { environment.removeValue(forKey: key) }; p.environment = environment
            let stdin = Pipe(), stdout = Pipe(), stderr = Pipe(); p.standardInput = stdin; p.standardOutput = stdout; p.standardError = stderr
            try p.run(); process = p; input = stdin.fileHandleForWriting; output = stdout.fileHandleForReading; errors = stderr.fileHandleForReading
            let token = UUID(); generation = token; captured = ProcessCleanup.descendants(of:p.processIdentifier)
            // First bytes in every fresh session configure policy. The engine gates scheduling until handshake.
            try send(WireCommand("set_policy",["ac_only":.bool(acOnly)])); try send(WireCommand("hello"))
            readTask = Task.detached { [weak self] in
                do {
                    var buffer = Data(); var sequence = -1
                    for try await chunk in EnginePipeReader.chunks(stdout.fileHandleForReading,label:"local.voice-memo.engine.stdout") {
                        for byte in chunk {
                            if byte == 10 {
                                if !buffer.isEmpty { let message = try WireCodec.decode(buffer,after:sequence); sequence = message.sequence; await event(message); buffer.removeAll(keepingCapacity:true) }
                            } else { buffer.append(byte); guard buffer.count <= WireCodec.limit else { throw NativeFailure.invalid("엔진 메시지 제한을 초과했습니다.") } }
                        }
                    }
                    await self?.connectionEnded(token:token,message:"엔진 연결이 종료되었습니다. 마지막 결과는 유지됩니다.",callback:ended)
                } catch { await self?.connectionEnded(token:token,message:"엔진 응답을 읽지 못했습니다. 다시 연결하세요.",callback:ended) }
            }
            EnginePipeReader.drain(stderr.fileHandleForReading)
            monitorTask = Task { [weak self] in
                while !Task.isCancelled { try? await Task.sleep(for:.milliseconds(250)); guard !Task.isCancelled else { break }; await self?.capture(token:token) }
            }
        } catch { await shutdown(); throw error }
    }
    private func capture(token: UUID) { guard token == generation, let p = process else { return }; captured += ProcessCleanup.descendants(of:p.processIdentifier); if captured.count > 8192 { captured = Array(captured.suffix(8192)) } }
    private func connectionEnded(token: UUID, message: String, callback: @Sendable (String) async -> Void) async {
        guard token == generation else { return }; await shutdown(); await callback(message)
    }
    func send(_ command: WireCommand) throws {
        guard let input, process?.isRunning == true else { throw NativeFailure.invalid("엔진이 연결되지 않았습니다.") }
        var data = try JSONEncoder().encode(command); guard data.count <= WireCodec.limit else { throw NativeFailure.invalid("명령 크기 제한을 초과했습니다.") }; data.append(10); try input.write(contentsOf:data)
    }
    @discardableResult func shutdown() async -> Bool {
        if let cleanup { return await cleanup.value }
        generation = UUID(); monitorTask?.cancel(); monitorTask = nil
        try? send(WireCommand("shutdown")); try? input?.close(); input = nil
        let p = process, prior = captured; captured = []
        let task = Task { if let p { return await ProcessCleanup.stop(p,captured:prior) }; return true }; cleanup = task
        let stopped = await task.value
        // Keep process/lease ownership until the bounded cleanup has actually completed.
        try? output?.close(); try? errors?.close(); output = nil; errors = nil
        readTask?.cancel(); readTask = nil
        if stopped { process = nil; if let nativeLock { close(nativeLock) }; nativeLock = nil }
        cleanup = nil
        return stopped
    }
}
