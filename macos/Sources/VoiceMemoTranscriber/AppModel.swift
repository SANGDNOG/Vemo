import SwiftUI
import NativeCore
import AVFoundation
import ServiceManagement

@MainActor final class AppModel: ObservableObject {
    static let shared = AppModel()
    let stateRoot = DemoContent.enabled ? FileManager.default.temporaryDirectory.appendingPathComponent("Vemo-demo-" + UUID().uuidString) : RuntimePaths.stateRoot()
    let engine = EngineClient()
    @Published var jobs: [Job] = []
    @Published var selection: String?
    @Published var search = ""
    @Published var connection = "엔진 연결 준비 중"
    @Published var problem: String?
    @Published var modelsReady = false
    @Published var modelStatus = "모델 상태 확인 중"
    @Published var paused = false
    @Published var transcript: Transcript?
    @Published var transcriptIndex: [String:String] = [:]
    @Published var outputFolder = UserDefaults.standard.string(forKey: "outputFolder") ?? ""
    @Published var inputFolder = UserDefaults.standard.string(forKey: "inputFolder") ?? ""
    @Published var sourceMode = UserDefaults.standard.string(forKey: "sourceMode") ?? "voice_memos"
    @Published var acOnly = UserDefaults.standard.object(forKey: "acOnly") as? Bool ?? true
    @Published var topic = ""
    @Published var dateInput = ""
    @Published var loginEnabled = SMAppService.mainApp.status == .enabled
    @Published var currentTime = 0.0
    @Published var playing = false
    @Published var audioDuration = 0.0
    private var player: AVAudioPlayer?
    private var started = false
    private lazy var imports = ImportBroker(root:stateRoot)
    private lazy var source = SourceBroker(root:stateRoot,adapter:AppleVoiceMemosAdapter(
        recordingsRoot:FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Group Containers/group.com.apple.VoiceMemos.shared/Recordings"),
        editingRisk:{ !NSRunningApplication.runningApplications(withBundleIdentifier:"com.apple.VoiceMemos").isEmpty }))
    private lazy var publisher = PublishBroker(stateRoot:stateRoot)
    private let watcher = FolderWatcher()
    private var scanning = false
    private var connecting = false
    private var connected = false
    private var ticks = 0
    private var lastSourceScan = Date.distantPast
    @Published var importing = false
    @Published var sourceDiagnostic: String?
    private var deliveries: [String:String] = [:]
    private var brokerRequests: Set<String> = []
    private var timer: Timer?
    private var loadID = UUID()
    private var loadedAudioKey: String?
    private var loadedSelection: String?
    var selected: Job? { jobs.first { $0.id == selection } }
    var visibleJobs: [Job] { jobs.filter { search.isEmpty || $0.title.localizedCaseInsensitiveContains(search) || (transcriptIndex[$0.id] ?? "").localizedCaseInsensitiveContains(search) }.sorted { ($0.recorded_at ?? "") > ($1.recorded_at ?? "") } }
    var overallStatus: String {
        if let j = jobs.first(where: { ["preparing", "transcribing", "aligning", "titling", "saving"].contains($0.status) }) { return "\(j.statusLabel) · \(j.progress)" }
        return "\(connection) · 대기 \(jobs.filter { ["waiting", "paused"].contains($0.status) }.count)개"
    }
    func start() {
        guard !started else { return }; started = true
        if DemoContent.enabled { jobs = DemoContent.jobs; selection = jobs.first?.id; connection = "예시 화면 · 실제 작업 아님"; modelsReady = true; modelStatus = "예시 화면 · 모델 다운로드 전"; loadDemoSelection(); return }; connect()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in self?.tick() } }
    }
    func connect() {
        guard !DemoContent.enabled else { return }; guard !connecting else { return }; connecting = true; connected = false
        connection = "엔진 연결 중"
        Task { [self] in
            defer { connecting = false }
            do {
                deliveries.removeAll()
                try await engine.start(root: stateRoot, acOnly: acOnly, event: { [weak self] event in await self?.receive(event) }, ended: { [weak self] message in await self?.disconnected(message) })
            } catch { disconnected(error.localizedDescription) }
        }
    }
    private func disconnected(_ message: String) { connected = false; deliveries.removeAll(); connection = "엔진 연결 필요"; problem = message }
    func command(_ kind: String, _ payload: [String:JSONValue] = [:]) {
        guard !DemoContent.enabled else { return }
        Task { do { try await engine.send(WireCommand(kind, payload)) } catch { problem = error.localizedDescription } }
    }
    func receive(_ event: WireEvent) async {
        do {
            switch event.kind {
            case "snapshot":
                if let value = event.payload["jobs"] { jobs = try value.decoded([Job].self); await indexTranscripts() }
                if let status = event.payload["engine_status"]?.object { updateEngine(status) }
                let first = !connected; connected = true
                connection = "로컬 엔진 연결됨"; if selection == nil { selection = jobs.first?.id }; await loadSelection(); if first { scanSources(force:true); await deliverPendingImports() }
            case "job_changed":
                if let value = event.payload["job"] { let job = try value.decoded(Job.self); if let i = jobs.firstIndex(where: { $0.id == job.id }) { jobs[i] = job } else { jobs.append(job) }; await index(job); if selection == nil { selection = job.id }; if selection == job.id { await loadSelection() } }
            case "engine_status": updateEngine(event.payload["engine_status"]?.object ?? event.payload)
            case "model_progress":
                modelStatus = ModelProgress.description(event.payload)
                if let e = event.payload["error"], e != .null { problem = "모델 다운로드가 중단되었습니다. 설정에서 다운로드 / 재개를 선택하세요." }
            case "snapshot_requested":
                guard let id = event.payload["job_id"]?.string, let reference = event.payload["source_reference"]?.string, !brokerRequests.contains("snapshot:" + id) else { break }
                brokerRequests.insert("snapshot:" + id)
                Task { [self] in
                    defer { brokerRequests.remove("snapshot:" + id) }
                    do {
                        let snapshot = reference.hasPrefix("import:") ? try await imports.snapshot(reference:reference,jobID:id) : try await source.snapshot(reference:reference,jobID:id)
                        command("snapshot_ready",["job_id":.string(id),"owned_path":.string(snapshot.owned_path),"revision_before":try .encoded(snapshot.revision_before),"revision_after":try .encoded(snapshot.revision_after)])
                    } catch { command("snapshot_ready",["job_id":.string(id),"error":brokerError(error)]) }
                }
            case "publication_ready":
                guard let id = event.payload["job_id"]?.string, let publication = event.payload["publication_id"]?.string, let path = event.payload["manifest_path"]?.string, !brokerRequests.contains("publish:" + publication) else { break }
                brokerRequests.insert("publish:" + publication)
                Task { [self] in
                    defer { brokerRequests.remove("publish:" + publication) }
                    do {
                        guard !outputFolder.isEmpty else { throw BrokerFailure("output_not_configured","설정에서 결과 폴더를 선택하세요. 로컬 결과는 보존됩니다.") }
                        let receipt = try await publisher.publish(manifestPath:path,jobID:id,publicationID:publication,outputRoot:URL(fileURLWithPath:outputFolder))
                        command("publish_ack",["job_id":.string(id),"publication_id":.string(publication),"receipt":try .encoded(receipt)])
                    } catch { problem = error.localizedDescription; command("publish_ack",["job_id":.string(id),"publication_id":.string(publication),"error":brokerError(error)]) }
                }
            case "command_result":
                if let request = event.payload["request_id"]?.string, let reference = deliveries.removeValue(forKey:request), event.payload["ok"]?.bool == true { try await imports.markDelivered(reference:reference) }
                if event.payload["ok"]?.bool == false { problem = event.payload["error"]?.object?["message"]?.string ?? "요청을 완료하지 못했습니다." }
            case "diagnostic": problem = event.payload["message"]?.string
            default: break
            }
        } catch { problem = "엔진 상태를 해석하지 못했습니다: \(error.localizedDescription)" }
    }
    private func updateEngine(_ status: [String:JSONValue]) {
        modelsReady = status["models_ready"]?.bool ?? modelsReady; paused = status["paused"]?.bool ?? paused
        if modelsReady { modelStatus = "로컬 모델 준비됨" }
        else if let states = status["model_status"]?.object, states.values.contains(where:{ $0.object?["state"]?.string == "verifying" }) { modelStatus = "모델 파일 검증 중" }
        else if !modelStatus.contains("다운로드 중") && !modelStatus.contains("기다리는 중") { modelStatus = "모델 다운로드가 필요합니다." }
    }
    private func readTranscript(_ path: String) async throws -> Transcript {
        let root = stateRoot
        return try await Task.detached { let url = try Validation.owned(path, root: root); let data = try Data(contentsOf: url); return try JSONDecoder().decode(Transcript.self, from: data) }.value
    }
    private func index(_ job: Job) async { if let path = job.artifact_path, let result = try? await readTranscript(path) { transcriptIndex[job.id] = result.text } }
    private func indexTranscripts() async { for job in jobs { await index(job) } }
    private func loadDemoSelection() {
        guard let job = selected else { return }; transcript = DemoContent.transcript(for:job.id); topic = job.topic ?? ""; dateInput = job.recording_date ?? ""; currentTime = 0; audioDuration = 0
    }
    func loadSelection() async {
        if DemoContent.enabled { loadDemoSelection(); return }
        let token = UUID(); loadID = token
        guard let job = selected else { player?.stop(); player = nil; loadedAudioKey = nil; loadedSelection = nil; transcript = nil; playing = false; return }
        if loadedSelection != job.id {
            player?.stop(); player = nil; loadedAudioKey = nil; playing = false; currentTime = 0; audioDuration = 0; transcript = nil
            topic = job.topic ?? ""; dateInput = job.recording_date ?? ""; loadedSelection = job.id
        }
        if let path = job.artifact_path { do { let value = try await readTranscript(path); guard loadID == token else { return }; transcript = value } catch { problem = error.localizedDescription } }
        if let path = job.playback_path {
            guard loadedAudioKey != job.id + ":" + path else { return }
            do { let url = try Validation.owned(path, root: stateRoot); let data = try await Task.detached { try Data(contentsOf: url) }.value; guard loadID == token else { return }; player = try AVAudioPlayer(data: data); audioDuration = player?.duration ?? 0; loadedAudioKey = job.id + ":" + path } catch { problem = "오디오를 열지 못했습니다: \(error.localizedDescription)" }
        } else {
            player?.stop(); player = nil; loadedAudioKey = nil; playing = false; currentTime = 0; audioDuration = 0
        }
    }
    func togglePlayback() { guard let player else { return }; if player.isPlaying { player.pause() } else { player.play() }; playing = player.isPlaying }
    func seek(_ value: Double) { player?.currentTime = max(0, min(value, audioDuration)); currentTime = player?.currentTime ?? 0 }
    private func tick() { currentTime = player?.currentTime ?? currentTime; playing = player?.isPlaying ?? false
        ticks += 1
        if ticks % 5 == 0 && connected { scanSources() }
    }
    func setPolicy(_ value: Bool) { UserDefaults.standard.set(value,forKey:"acOnly"); if connected { command("set_policy",["ac_only":.bool(value)]) } }
    private func brokerError(_ error: Error) -> JSONValue { .object(["code":.string((error as? BrokerFailure)?.code ?? "native_io"),"message":.string((error as? BrokerFailure)?.message ?? "파일 작업을 완료하지 못했습니다. 권한과 저장 공간을 확인하세요."),"recoverable":.bool(true)]) }
    private func deliverPendingImports() async {
        do {
            for memo in try await imports.pendingImports() where !deliveries.values.contains(memo.source_reference) {
                let command = WireCommand("import",["memo":try .encoded(memo)])
                deliveries[command.request_id] = memo.source_reference
                do { try await engine.send(command) } catch { deliveries.removeValue(forKey:command.request_id); throw error }
            }
        } catch { problem = error.localizedDescription }
    }
    func scanSources(force: Bool = false) {
        guard !DemoContent.enabled, connected, !scanning else { return }; scanning = true
        Task { [self] in
            defer { scanning = false }
            do {
                if sourceMode == "voice_memos" {
                    if !force && Date().timeIntervalSince(lastSourceScan) < 60 { return }; lastSourceScan = Date()
                    let memos = try await source.scan()
                    sourceDiagnostic = NSRunningApplication.runningApplications(withBundleIdentifier:"com.apple.VoiceMemos").isEmpty ? "음성 메모 연결됨 · 새 녹음은 자동 처리합니다." : "Mac 음성 메모가 열려 있습니다. 다운로드를 마친 뒤 음성 메모를 종료하면 전사를 시작합니다."
                    command("discover",["memos":try .encoded(memos)])
                } else if !inputFolder.isEmpty {
                    for url in try await watcher.stableFiles(in:URL(fileURLWithPath:inputFolder)) {
                        _ = try await imports.importFile(url); await watcher.accepted(url)
                    }
                    await deliverPendingImports()
                }
            } catch { let message = (error as? BrokerFailure)?.message ?? "입력 폴더를 읽지 못했습니다. 설정과 접근 권한을 확인하세요."; sourceDiagnostic = message; problem = message }
        }
    }
    func setDate() { guard let job = selected else { return }; do { let date = try Validation.recordingDate(dateInput); command("set_recording_date", ["job_id": .string(job.id), "recording_date": .string(date)]) } catch { problem = error.localizedDescription } }
    func retitle(generate: Bool) { guard let job = selected else { return }; command("retitle", ["job_id": .string(job.id), generate ? "generate" : "topic": generate ? .bool(true) : .string(topic)]) }
    func chooseFolder(output: Bool) { guard !DemoContent.enabled else { return };
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.canCreateDirectories = output
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if output { outputFolder = url.path; UserDefaults.standard.set(url.path, forKey: "outputFolder"); for job in jobs where job.status == "saving" { command("retry",["job_id":.string(job.id)]) } } else { inputFolder = url.path; UserDefaults.standard.set(url.path, forKey: "inputFolder"); scanSources(force:true) }
    }
    func importFiles(_ urls: [URL]) { guard !DemoContent.enabled else { return };
        Task { [self] in
            importing = true; defer { importing = false }
            for url in urls { do { _ = try await imports.importFile(url) } catch { problem = (error as? BrokerFailure)?.message ?? "오디오 파일을 가져오지 못했습니다." } }
            if connected { await deliverPendingImports() } else { problem = "파일을 안전하게 보관했습니다. 엔진을 연결하면 가져오기를 계속합니다." }
        }
    }
    func chooseFiles() { let panel = NSOpenPanel(); panel.allowsMultipleSelection = true; panel.canChooseDirectories = false; if panel.runModal() == .OK { importFiles(panel.urls) } }
    func copyTranscript() { guard let transcript else { return }; NSPasteboard.general.clearContents(); NSPasteboard.general.setString(transcript.text, forType: .string) }
    func export(_ format: String) {
        guard let job = selected, let transcript else { return }
        if ["srt", "vtt"].contains(format), transcript.alignment_status != "completed" && transcript.alignment_status != "success" { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = job.title.replacingOccurrences(of: "/", with: "_") + "." + format
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let text: String
        do { text = try TranscriptExport.render(transcript, title: job.title, format: format) } catch { problem = error.localizedDescription; return }
        Task { do { try await Task.detached { try text.write(to: url, atomically: true, encoding: .utf8) }.value } catch { problem = error.localizedDescription } }
    }
    func openResultFolder() { guard !DemoContent.enabled else { return };
        guard let job = selected, let date = job.recording_date, !outputFolder.isEmpty else { problem = "결과 폴더와 녹음일을 확인하세요."; return }
        Task { do { let url = try await publisher.activeRevision(uuid:job.uuid,recordingDate:date,outputRoot:URL(fileURLWithPath:outputFolder)); NSWorkspace.shared.open(url) } catch { problem = error.localizedDescription } }
    }
    func setLogin(_ value: Bool) { guard !DemoContent.enabled else { return }; do { if value { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }; loginEnabled = SMAppService.mainApp.status == .enabled } catch { loginEnabled = SMAppService.mainApp.status == .enabled; problem = "로그인 실행 설정 실패: \(error.localizedDescription)" } }
    func checkPermissions() { guard !DemoContent.enabled else { return }; Task { do { let status = try await source.writeDiagnostic(); sourceDiagnostic = status.message; problem = status.message } catch { problem = "접근 진단을 저장하지 못했습니다." } } }
    func openPermissions() { guard !DemoContent.enabled else { return }; if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") { NSWorkspace.shared.open(url) } }
    func quit() { NSApp.terminate(nil) }
}
