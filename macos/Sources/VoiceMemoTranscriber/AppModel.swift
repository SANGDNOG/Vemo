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
    @Published var library = LibraryOrganization()
    @Published var libraryFilter = "all"
    @Published var libraryReady = false
    @Published var libraryBusy = false
    let libraryDragSessionID = UUID().uuidString
    private var libraryWriteTask: Task<Void, Never>?
    private lazy var libraryStore = LibraryFolderStore(root: stateRoot)
    @Published var connection = "엔진 연결 준비 중"
    @Published var problem: String?
    @Published var modelsReady = false
    @Published var modelStatus = "모델 상태 확인 중"
    @Published var paused = false
    @Published var transcript: Transcript?
    @Published var notes: RecordingNotes?
    @Published var notesReadError: String?
    @Published var detailMode = "transcript"
    @Published var quoteRow: Int?
    @Published var focusedQuote: String?
    @Published var transcriptIndex: [String:String] = [:]
    @Published var outputFolder = UserDefaults.standard.string(forKey: "outputFolder") ?? ""
    @Published var zipExportFolder = UserDefaults.standard.string(forKey: "zipExportFolder") ?? ""
    private var zipFolderPanel: NSOpenPanel?
    private var zipExportTask: Task<Void,Never>?
    private var zipExportClosing = false
    @Published var zipExportBusy = false
    @Published var zipExportProgress = ""
    @Published var zipExportError: String?
    @Published var zipExportResult: URL?
    var completedZIPCount: Int { jobs.filter { $0.status == "completed" && $0.artifact_path != nil }.count }
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
    var visibleJobs: [Job] { LibraryQuery.visible(jobs, assignments: library.assignments, filter: libraryFilter, search: search, index: transcriptIndex) }
    var libraryTitle: String { libraryFilter == "all" ? "전체 녹음" : libraryFilter == "unfiled" ? "미분류" : LibraryFolderHierarchy.path(for: libraryFilter, in: library.folders) }
    func folderCount(_ filter: String) -> Int { LibraryQuery.visible(jobs, assignments: library.assignments, filter: filter, search: "", index: [:]).count }
    func folderName(for jobID: String) -> String {
        guard let id = library.assignments[jobID] else { return "미분류" }
        return LibraryFolderHierarchy.path(for: id, in: library.folders)
    }
    func reconcileLibrarySelection() { if selection == nil || !visibleJobs.contains(where: { $0.id == selection }) { selection = visibleJobs.first?.id } }
    func loadLibrary() {
        let store = libraryStore
        Task {
            do {
                var value = try await store.load()
                if DemoContent.enabled && value.folders.isEmpty {
                    value = try await store.create(name: "회의")
                    if let meetings = value.folders.first {
                        value = try await store.create(name: "프로젝트", parentID: meetings.id)
                        if let project = value.folders.last {
                            value = try await store.assign(jobIDs: ["demo-completed"], folderID: project.id)
                            value = try await store.create(name: "스프린트", parentID: project.id)
                            if let sprint = value.folders.last { value = try await store.assign(jobIDs: ["demo-progress"], folderID: sprint.id) }
                        }
                        value = try await store.assign(jobIDs: ["demo-empty"], folderID: meetings.id)
                    }
                    value = try await store.create(name: "강의")
                }
                if value.revision >= library.revision { library = value }
                libraryReady = true; reconcileLibrarySelection()
            } catch { libraryReady = false; problem = "폴더 분류를 읽지 못했습니다. 기존 분류 파일은 보존됩니다. " + error.localizedDescription }
        }
    }
    private func changeLibrary(_ operation: @escaping @Sendable (LibraryFolderStore) async throws -> LibraryOrganization) {
        guard libraryReady, !libraryBusy else { return }
        libraryBusy = true
        let store = libraryStore
        libraryWriteTask = Task {
            defer { libraryBusy = false }
            do {
                let value = try await operation(store)
                if value.revision >= library.revision { library = value }
                if libraryFilter != "all", libraryFilter != "unfiled", !library.folders.contains(where: { $0.id == libraryFilter }) { libraryFilter = "all" }
                reconcileLibrarySelection()
            } catch { problem = error.localizedDescription }
        }
    }
    func flushLibraryChanges() async { await libraryWriteTask?.value }
    func createLibraryFolder(_ name: String, parentID: String? = nil) { changeLibrary { try await $0.create(name: name, parentID: parentID) } }
    func renameLibraryFolder(_ id: String, name: String) { changeLibrary { try await $0.rename(id: id, name: name) } }
    func updateLibraryFolder(_ id: String, name: String, parentID: String?) { changeLibrary { try await $0.update(id: id, name: name, parentID: parentID) } }
    func deleteLibraryFolder(_ id: String) {
        changeLibrary { try await $0.delete(id: id) }
    }
    func moveRecording(_ jobID: String, to folderID: String?) { changeLibrary { try await $0.assign(jobIDs: [jobID], folderID: folderID) } }
    func libraryDragPayload(kind: LibraryDragKind, id: String) -> LibraryDragPayload {
        LibraryDragPayload(kind: kind, id: id, sessionID: libraryDragSessionID)
    }
    func canDropLibraryItem(_ payload: LibraryDragPayload, on target: LibraryDropTarget) -> Bool {
        guard libraryReady, !libraryBusy else { return false }
        return (try? LibraryDropRules.action(for: payload, target: target, organization: library,
                                           knownJobIDs: Set(jobs.map(\.id)), sessionID: libraryDragSessionID)) != nil
    }
    @discardableResult
    func dropLibraryItem(_ payload: LibraryDragPayload, on target: LibraryDropTarget) -> Bool {
        guard libraryReady, !libraryBusy else { return false }
        do {
            guard let action = try LibraryDropRules.action(for: payload, target: target, organization: library,
                                                         knownJobIDs: Set(jobs.map(\.id)), sessionID: libraryDragSessionID) else { return false }
            changeLibrary { store in
                switch action {
                case .recording(let id, let folderID): return try await store.assign(jobIDs: [id], folderID: folderID)
                case .folder(let id, let parentID): return try await store.moveFolder(id: id, parentID: parentID)
                }
            }
            return true
        } catch {
            problem = error.localizedDescription
            return false
        }
    }
    var overallStatus: String {
        if zipExportBusy { return zipExportProgress }
        if paused { return "새 작업 일시 중지 · 대기 \(jobs.filter { ["waiting", "paused"].contains($0.status) }.count)개" }
        if let j = jobs.first(where: { ["preparing", "transcribing", "aligning", "titling", "noting", "saving"].contains($0.status) }) { return "\(j.statusLabel) · \(j.progress)" }
        return "\(connection) · 대기 \(jobs.filter { ["waiting", "paused"].contains($0.status) }.count)개"
    }
    func start() {
        guard !started else { return }; started = true; loadLibrary()
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
    func manualProcessingTitle(_ title: String) -> String { paused ? title + " · 처리 재개" : title }
    func processingCommand(_ kind: String, _ payload: [String:JSONValue] = [:]) {
        guard !DemoContent.enabled else { return }
        let resume = paused ? WireCommand("resume") : nil
        let requested = WireCommand(kind, payload)
        Task {
            do {
                if let resume { try await engine.send(resume) }
                try await engine.send(requested)
            } catch { problem = error.localizedDescription }
        }
    }
    func processRecording(_ job: Job) {
        if job.status == "unprocessed" { processingCommand("enqueue", ["uuids": .array([.string(job.uuid)])]) }
        else if job.status == "failed" { processingCommand("retry", ["job_id": .string(job.id)]) }
    }
    func receive(_ event: WireEvent) async {
        do {
            switch event.kind {
            case "snapshot":
                if let value = event.payload["jobs"] { jobs = try value.decoded([Job].self); await indexTranscripts() }
                if let status = event.payload["engine_status"]?.object { updateEngine(status) }
                let first = !connected; connected = true
                connection = "로컬 엔진 연결됨"; reconcileLibrarySelection(); await loadSelection(); if first { scanSources(force:true); await deliverPendingImports() }
            case "job_changed":
                if let value = event.payload["job"] { let job = try value.decoded(Job.self); if let i = jobs.firstIndex(where: { $0.id == job.id }) { jobs[i] = job } else { jobs.append(job) }; await index(job); reconcileLibrarySelection(); if selection == job.id { await loadSelection() } }
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
                        if sourceMode == "folder", !inputFolder.isEmpty { try FolderConnection.validate(input:URL(fileURLWithPath:inputFolder),output:URL(fileURLWithPath:outputFolder)) }
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
    private func readNotes(_ path: String, text: String) async throws -> RecordingNotes {
        let root = stateRoot
        return try await Task.detached {
            let url = try Validation.owned(path,root:root)
            let data = try Data(contentsOf:url)
            guard data.count <= WireCodec.limit else { throw NativeFailure.invalid("노트 파일이 너무 큽니다.") }
            return try NotesDisplay.validated(JSONDecoder().decode(RecordingNotes.self,from:data),text:text)
        }.value
    }
    private func index(_ job: Job) async {
        guard let path = job.artifact_path, let result = try? await readTranscript(path) else { return }
        var text = result.text
        if let path = job.notes_path, let note = try? await readNotes(path,text:result.text) { text += "\n" + NotesDisplay.markdown(note,title:job.title) }
        transcriptIndex[job.id] = text
    }
    private func indexTranscripts() async { for job in jobs { await index(job) } }
    private func loadDemoSelection() {
        guard let job = selected else { transcript = nil; notes = nil; quoteRow = nil; focusedQuote = nil; return }; transcript = DemoContent.transcript(for:job.id); notes = DemoContent.notes(for:job.id); quoteRow = nil; focusedQuote = nil; topic = job.topic ?? ""; dateInput = job.recording_date ?? ""; currentTime = 0; audioDuration = 0
    }
    func loadSelection() async {
        if DemoContent.enabled { loadDemoSelection(); return }
        let token = UUID(); loadID = token
        guard let job = selected else { player?.stop(); player = nil; loadedAudioKey = nil; loadedSelection = nil; transcript = nil; notes = nil; notesReadError = nil; playing = false; return }
        if loadedSelection != job.id {
            player?.stop(); player = nil; loadedAudioKey = nil; playing = false; currentTime = 0; audioDuration = 0; transcript = nil; notes = nil; notesReadError = nil; quoteRow = nil; focusedQuote = nil
            topic = job.topic ?? ""; dateInput = job.recording_date ?? ""; loadedSelection = job.id
        }
        if let path = job.artifact_path {
            do { let value = try await readTranscript(path); guard loadID == token else { return }; transcript = value }
            catch { guard loadID == token else { return }; transcript = nil; notes = nil; problem = error.localizedDescription }
        } else { transcript = nil; notes = nil; quoteRow = nil; focusedQuote = nil }
        if let path = job.notes_path, let transcript {
            do { let value = try await readNotes(path,text:transcript.text); guard loadID == token else { return }; notes = value; notesReadError = nil }
            catch { guard loadID == token else { return }; notes = nil; notesReadError = error.localizedDescription }
        } else { notes = nil; notesReadError = nil }
        if let path = job.playback_path {
            guard loadedAudioKey != job.id + ":" + path else { return }
            do { let url = try Validation.owned(path, root: stateRoot); let data = try await Task.detached { try Data(contentsOf: url) }.value; guard loadID == token else { return }; player = try AVAudioPlayer(data: data); audioDuration = player?.duration ?? 0; loadedAudioKey = job.id + ":" + path } catch { guard loadID == token else { return }; problem = "오디오를 열지 못했습니다: \(error.localizedDescription)" }
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
                    if !outputFolder.isEmpty { try FolderConnection.validate(input:URL(fileURLWithPath:inputFolder),output:URL(fileURLWithPath:outputFolder)) }
                    let scan = try await watcher.scan(in:URL(fileURLWithPath:inputFolder))
                    var failures = 0
                    for url in scan.files {
                        do { _ = try await imports.importFile(url); await watcher.accepted(url) }
                        catch { failures += 1; if (error as? BrokerFailure)?.code == "invalid_media" { await watcher.rejected(url) } }
                    }
                    let waiting = scan.waitingForDownload > 0 ? " · 다운로드 대기 \(scan.waitingForDownload)개 (Finder에서 오프라인 사용을 켜세요)" : ""
                    let changing = scan.changingCount > 0 ? " · 안정화 대기 \(scan.changingCount)개" : ""
                    let invalid = scan.unavailableCount + failures > 0 ? " · 가져오기 불가 \(scan.unavailableCount + failures)개" : ""
                    sourceDiagnostic = "폴더 연결됨" + waiting + changing + invalid
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
        do {
            if output, sourceMode == "folder", !inputFolder.isEmpty { try FolderConnection.validate(input:URL(fileURLWithPath:inputFolder),output:url) }
            if !output, !outputFolder.isEmpty { try FolderConnection.validate(input:url,output:URL(fileURLWithPath:outputFolder)) }
        } catch { problem = error.localizedDescription; return }
        if output { outputFolder = url.path; UserDefaults.standard.set(url.path, forKey: "outputFolder"); for job in jobs where job.status == "saving" { command("retry",["job_id":.string(job.id)]) } } else { inputFolder = url.path; UserDefaults.standard.set(url.path, forKey: "inputFolder"); scanSources(force:true) }
    }
    func chooseZIPFolder() {
        guard !DemoContent.enabled, !zipExportBusy, !zipExportClosing else { return }
        if let panel = zipFolderPanel {
            panel.makeKeyAndOrderFront(nil)
            return
        }
        let panel = NSOpenPanel()
        panel.title = "ZIP 저장 위치"
        panel.prompt = "선택"
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        if !zipExportFolder.isEmpty { panel.directoryURL = URL(fileURLWithPath:zipExportFolder) }
        zipFolderPanel = panel
        panel.begin { [weak self, weak panel] response in
            Task { @MainActor in
                guard let self, let panel, self.zipFolderPanel === panel else { return }
                defer { self.zipFolderPanel = nil }
                guard response == .OK, let url = panel.url else { return }
                self.zipExportFolder = url.path
                UserDefaults.standard.set(url.path,forKey:"zipExportFolder")
                self.zipExportError = nil; self.zipExportResult = nil
            }
        }
    }
    func exportCompletedZIP() {
        guard !DemoContent.enabled, !zipExportClosing, !zipExportBusy, !zipExportFolder.isEmpty, completedZIPCount > 0 else { return }
        // Copy value types and paths now; export never consults changing selection or job state.
        let snapshot = jobs.filter { $0.status == "completed" && $0.artifact_path != nil }
        let root = stateRoot
        let destination = URL(fileURLWithPath:zipExportFolder)
        zipExportBusy = true; zipExportError = nil; zipExportResult = nil
        zipExportProgress = "ZIP 내보내기 준비 중 · \(snapshot.count)개 녹음"
        zipExportTask = Task { [self] in
            defer { zipExportBusy = false }
            do {
                let result = try await RecordingZIPExport.export(jobs:snapshot,stateRoot:root,destination:destination) { [weak self] message in
                    await self?.updateZIPProgress(message)
                }
                zipExportResult = result
                zipExportProgress = "완료 · \(snapshot.count)개 녹음"
            } catch {
                zipExportProgress = "ZIP 내보내기 실패"
                zipExportError = error.localizedDescription
            }
        }
    }
    func flushZIPExport() async {
        zipExportClosing = true
        if zipExportBusy { zipExportProgress = "종료 대기 · ZIP 내보내기를 마치는 중" }
        await zipExportTask?.value
    }
    private func updateZIPProgress(_ message: String) {
        zipExportProgress = (zipExportClosing ? "종료 대기 · " : "") + message
    }
    func importFiles(_ urls: [URL]) { guard !DemoContent.enabled else { return };
        Task { [self] in
            importing = true; defer { importing = false }
            for url in urls { do { _ = try await imports.importFile(url) } catch { problem = (error as? BrokerFailure)?.message ?? "오디오 파일을 가져오지 못했습니다." } }
            if connected { await deliverPendingImports() } else { problem = "파일을 안전하게 보관했습니다. 엔진을 연결하면 가져오기를 계속합니다." }
        }
    }
    func chooseFiles() { let panel = NSOpenPanel(); panel.allowsMultipleSelection = true; panel.canChooseDirectories = false; if panel.runModal() == .OK { importFiles(panel.urls) } }
    var canGenerateNotes: Bool { transcript != nil && selected?.artifact_path != nil && selected.map { ["completed","needs_recording_date","failed"].contains($0.status) } == true }
    func generateNotes() { guard let job = selected, canGenerateNotes else { return }; processingCommand("generate_notes",["job_id":.string(job.id)]) }
    func revealQuote(_ quote: String) {
        guard let transcript, let target = NotesDisplay.quoteTarget(quote,in:transcript) else { return }
        focusedQuote = quote; quoteRow = target.row; detailMode = "transcript"
        if let timing = target.timing { seek(timing.start_s) }
    }
    func copyNotes() { guard let notes, let job = selected else { return }; NSPasteboard.general.clearContents(); NSPasteboard.general.setString(NotesDisplay.markdown(notes,title:job.title),forType:.string) }
    func exportNotes() {
        guard let notes, let job = selected else { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = job.title.replacingOccurrences(of:"/",with:"_") + " 노트.md"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let text = NotesDisplay.markdown(notes,title:job.title)
        Task { do { try await Task.detached { try text.write(to:url,atomically:true,encoding:.utf8) }.value } catch { problem = error.localizedDescription } }
    }
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
