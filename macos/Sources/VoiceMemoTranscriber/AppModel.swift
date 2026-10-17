import SwiftUI
import NativeCore
import AVFoundation
import ServiceManagement

enum TitleEditingLocation { case sidebar, detail }
struct RecordingBulkConfirmation {
    let kind: String
    let ids: [String]
    var title: String { kind == "purge_recording" ? "\(ids.count)개 녹음을 영구 삭제할까요?" : kind == "restore_recording" ? "\(ids.count)개 녹음을 복원할까요?" : "\(ids.count)개 녹음을 휴지통으로 이동할까요?" }
    var button: String { kind == "purge_recording" ? "영구 삭제" : kind == "restore_recording" ? "복원" : "휴지통으로 이동" }
    var message: String {
        if kind == "purge_recording" { return "선택한 녹음의 Vemo 로컬 오디오 사본·전사문·노트를 영구 삭제합니다. 되돌릴 수 없습니다. iPhone·Drive 원본과 이미 내보낸 ZIP은 유지되며, 자동 검색으로 다시 추가되지 않습니다." }
        return kind == "restore_recording" ? "선택한 녹음을 Vemo 목록으로 복원합니다. 원본 파일과 내보낸 ZIP은 변경하지 않습니다." : "선택한 녹음을 Vemo 휴지통으로 이동합니다. 나중에 복원할 수 있으며 원본 파일과 내보낸 ZIP은 유지됩니다."
    }
}

@MainActor final class AppModel: ObservableObject {
    static let shared = AppModel()
    let stateRoot = DemoContent.enabled ? FileManager.default.temporaryDirectory.appendingPathComponent("Vemo-demo-" + UUID().uuidString) : RuntimePaths.stateRoot()
    let engine = EngineClient()
    @Published var jobs: [Job] = []
    var activeJobs: [Job] { jobs.filter { $0.deleted_at == nil } }
    @Published var editingTitleJobID: String?
    @Published var editingTitleLocation: TitleEditingLocation?
    @Published var titleDraft = ""
    private var importPanel: NSOpenPanel?
    private var refreshRequests: [String:String] = [:]
    private var pendingRecordingCommands: [String:String] = [:]
    private var refreshPendingJobs: Set<String> = []
    private var automaticNotesRequested: Set<String> = []
    private var automaticNoteRequests: [String:String] = [:]
    private var automaticNotesRetryAfter: [String:Date] = [:]
    private var automaticNotesTask: Task<Void,Never>?
    private var folderConnectionPanel: NSOpenPanel?
    private var rescanRequested = false
    @Published var selectedIDs: Set<String> = []
    private var selectionAnchor: String?
    private var updatingSelection = false
    private var recordingDragGroups: [String:(origin:String, ids:[String])] = [:]
    @Published var recordingConfirmation: RecordingBulkConfirmation?
    @Published var recordingBatchBusy = false
    @Published var selection: String? {
        didSet {
            if selection != oldValue { cancelTitleEdit() }
            if !updatingSelection { selectedIDs = Set(selection.map { [$0] } ?? []); selectionAnchor = selection }
        }
    }
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
    @Published var notesModelReady = false
    @Published var notesModelState = "verifying"
    @Published var notesModelStatus = "노트 모델 상태 확인 중"
    @Published var availableModels: [SelectableModel] = []
    @Published var selectedASRModel = "asr-qwen3-1.7b"
    @Published var selectedNotesModel = "notes-qwen3.5-27b"
    @Published var canChangeModels = false
    @Published var modelChangeMessage: String?
    @Published var modelDeviceDescription = "기기 성능 확인 중"
    @Published var recommendedASR: String?
    @Published var recommendedNotes: String?
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
    @Published var autoZIPEnabled = UserDefaults.standard.object(forKey:"autoZIPEnabled") as? Bool ?? true
    private lazy var perRecordingZIPStore = PerRecordingZIPStore(root:stateRoot)
    private var zipChecked: Set<String> = []
    private var zipRetryAfter: [String:Date] = [:]
    private var zipRescheduleRequested = false
    private var zipFolderPanel: NSOpenPanel?
    private var activeExportTask: Task<URL?,Error>?
    private var activeExportJobID: String?
    private var activeExportKey: String?
    private var zipExportTask: Task<Void,Never>?
    private var zipExportClosing = false
    @Published var zipExportBusy = false
    @Published var zipExportProgress = ""
    @Published var zipExportError: String?
    @Published var zipExportResult: URL?
    var completedZIPCount: Int { jobs.filter { AutomaticRecordingPolicy.zipEligible($0) && !recordingCommandPending($0.id) }.count }
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
    private lazy var mobileSync = MobileSyncBroker(root:stateRoot)
    @Published var mobileSyncFolder = ""
    @Published var mobileSyncStatus = "연결하지 않음"
    @Published var mobileSyncBusy = false
    private var mobileSyncPanel: NSOpenPanel?
    private var mobileSyncTask: Task<Void, Never>?
    private var mobileSyncLoaded = false
    private var mobileResultKeys: Set<String> = []
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
    private var nativeSnapshotTasks: [String:[UUID:Task<Void,Never>]] = [:]
    private var internalBrokerReplies: Set<String> = []
    private var timer: Timer?
    private var loadID = UUID()
    private var loadedAudioKey: String?
    private var loadedSelection: String?
    var selected: Job? { jobs.first { $0.id == selection } }
    var visibleJobs: [Job] { LibraryQuery.visible(jobs, assignments: library.assignments, filter: libraryFilter, search: search, index: transcriptIndex) }
    var libraryTitle: String { libraryFilter == "trash" ? "휴지통" : libraryFilter == "all" ? "전체 녹음" : libraryFilter == "unfiled" ? "미분류" : LibraryFolderHierarchy.path(for: libraryFilter, in: library.folders) }
    func folderCount(_ filter: String) -> Int { LibraryQuery.visible(jobs, assignments: library.assignments, filter: filter, search: "", index: [:]).count }
    func folderName(for jobID: String) -> String {
        guard let id = library.assignments[jobID] else { return "미분류" }
        return LibraryFolderHierarchy.path(for: id, in: library.folders)
    }
    func setRecordingSelection(_ ids: Set<String>, primary: String? = nil) {
        let visible = visibleJobs.map(\.id)
        let valid = ids.intersection(visible)
        updatingSelection = true
        selectedIDs = valid
        selection = primary.flatMap { valid.contains($0) ? $0 : nil }
            ?? selection.flatMap { valid.contains($0) ? $0 : nil }
            ?? visible.first(where:valid.contains)
        updatingSelection = false
        if valid.count == 1 { selectionAnchor = selection }
        if let editingTitleJobID, valid != [editingTitleJobID] { cancelTitleEdit() }
    }
    func selectRecording(_ id: String, modifiers: NSEvent.ModifierFlags = []) {
        let visible = visibleJobs.map(\.id)
        guard visible.contains(id) else { return }
        if modifiers.contains(.shift), let anchor = selectionAnchor,
           let start = visible.firstIndex(of:anchor), let end = visible.firstIndex(of:id) {
            let range = Set(visible[min(start,end)...max(start,end)])
            setRecordingSelection(modifiers.contains(.command) ? selectedIDs.union(range) : range,primary:id)
        } else if modifiers.contains(.command) {
            var next = selectedIDs
            if !next.insert(id).inserted { next.remove(id) }
            setRecordingSelection(next,primary:id)
            selectionAnchor = id
        } else { setRecordingSelection([id],primary:id); selectionAnchor = id }
    }
    func selectAllRecordings() { guard editingTitleJobID == nil else { return }; setRecordingSelection(Set(visibleJobs.map(\.id))) }
    func recordingActionIDs(_ id: String) -> [String] {
        visibleJobs.filter { selectedIDs.contains(id) ? selectedIDs.contains($0.id) : $0.id == id }.map(\.id)
    }
    func reconcileLibrarySelection() {
        let valid = selectedIDs.intersection(visibleJobs.map(\.id))
        setRecordingSelection(valid.isEmpty ? Set(visibleJobs.first.map { [$0.id] } ?? []) : valid)
        if let anchor = selectionAnchor, !visibleJobs.contains(where:{ $0.id == anchor }) { selectionAnchor = selection }
    }
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
                scheduleAutomaticOutputs()
            } catch { libraryReady = false; problem = "폴더 분류를 읽지 못했습니다. 기존 분류 파일은 보존됩니다. " + error.localizedDescription }
        }
    }
    private func changeLibrary(_ operation: @escaping @Sendable (LibraryFolderStore) async throws -> LibraryOrganization) {
        guard libraryReady, !libraryBusy else { return }
        libraryBusy = true
        let store = libraryStore
        libraryWriteTask = Task {
            defer { libraryBusy = false; scheduleAutomaticOutputs() }
            do {
                let value = try await operation(store)
                if value.revision >= library.revision { library = value }
                cancelObsoleteExport()
                if libraryFilter != "all", libraryFilter != "unfiled", libraryFilter != "trash", !library.folders.contains(where: { $0.id == libraryFilter }) { libraryFilter = "all" }
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
    func moveRecording(_ jobID: String, to folderID: String?) { guard activeJobs.contains(where: { $0.id == jobID }) else { return }; changeLibrary { try await $0.assign(jobIDs: [jobID], folderID: folderID) } }
    func moveRecordings(_ ids: [String], to folderID: String?) {
        guard !ids.isEmpty, Set(ids).isSubset(of:Set(activeJobs.map(\.id))) else { return }
        changeLibrary { try await $0.assign(jobIDs:ids,folderID:folderID) }
    }
    func beginRecordingDrag(_ id: String) -> LibraryDragPayload {
        if !selectedIDs.contains(id) { selectRecording(id) }
        let payload = libraryDragPayload(kind:.recording,id:id)
        recordingDragGroups = [payload.dragID:(origin:id,ids:recordingActionIDs(id))]
        return payload
    }
    func finishRecordingDrag(_ payload: LibraryDragPayload) { recordingDragGroups.removeValue(forKey:payload.dragID) }
    private func recordingDrop(_ payload: LibraryDragPayload, on target: LibraryDropTarget) throws -> (ids:[String], folder:String?)? {
        guard payload.kind == .recording, payload.sessionID == libraryDragSessionID,
              let group = recordingDragGroups[payload.dragID], group.origin == payload.id,
              !group.ids.isEmpty, Set(group.ids).isSubset(of:Set(activeJobs.map(\.id))) else { return nil }
        var changed = false
        for id in group.ids {
            let item = LibraryDragPayload(kind:.recording,id:id,sessionID:payload.sessionID,version:payload.version,dragID:payload.dragID)
            if try LibraryDropRules.action(for:item,target:target,organization:library,knownJobIDs:Set(activeJobs.map(\.id)),sessionID:libraryDragSessionID) != nil { changed = true }
        }
        guard changed else { return nil }
        switch target {
        case .folder(let id): return (group.ids,id)
        case .unfiled: return (group.ids,nil)
        case .root: return nil
        }
    }
    func libraryDragPayload(kind: LibraryDragKind, id: String) -> LibraryDragPayload {
        LibraryDragPayload(kind: kind, id: id, sessionID: libraryDragSessionID)
    }
    func canDropLibraryItem(_ payload: LibraryDragPayload, on target: LibraryDropTarget) -> Bool {
        guard libraryReady, !libraryBusy else { return false }
        if payload.kind == .recording { return (try? recordingDrop(payload,on:target)) != nil }
        return (try? LibraryDropRules.action(for: payload, target: target, organization: library,
                                           knownJobIDs: Set(activeJobs.map(\.id)), sessionID: libraryDragSessionID)) != nil
    }
    @discardableResult
    func dropLibraryItem(_ payload: LibraryDragPayload, on target: LibraryDropTarget) -> Bool {
        guard libraryReady, !libraryBusy else { return false }
        do {
            if payload.kind == .recording {
                guard let group = try recordingDrop(payload,on:target) else { return false }
                changeLibrary { try await $0.assign(jobIDs:group.ids,folderID:group.folder) }
                finishRecordingDrag(payload)
                return true
            }
            guard let action = try LibraryDropRules.action(for: payload, target: target, organization: library,
                                                         knownJobIDs: Set(activeJobs.map(\.id)), sessionID: libraryDragSessionID) else { return false }
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
        if paused { return "새 작업 일시 중지 · 대기 \(activeJobs.filter { ["waiting", "paused"].contains($0.status) }.count)개" }
        if let j = activeJobs.first(where: { ["preparing", "transcribing", "aligning", "titling", "noting", "saving"].contains($0.status) }) { return "\(j.statusLabel) · \(j.progress)" }
        return "\(connection) · 대기 \(activeJobs.filter { ["waiting", "paused"].contains($0.status) }.count)개"
    }
    func start() {
        guard !started else { return }; started = true; loadLibrary()
        if DemoContent.enabled { jobs = DemoContent.jobs; selection = jobs.first?.id; connection = "예시 화면 · 실제 작업 아님"; modelsReady = true; modelStatus = "예시 화면 · 모델 다운로드 전"; loadDemoSelection(); return }; connect()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in self?.tick() } }
    }
    func connect() {
        guard !DemoContent.enabled else { return }; guard !connecting else { return }; connecting = true; connected = false
        resetPendingConnectionRequests()
        connection = "엔진 연결 중"
        Task { [self] in
            defer { connecting = false }
            do {
                deliveries.removeAll()
                try await engine.start(root: stateRoot, acOnly: acOnly, event: { [weak self] event in await self?.receive(event) }, ended: { [weak self] message in await self?.disconnected(message) })
            } catch { disconnected(error.localizedDescription) }
        }
    }
    private func disconnected(_ message: String) { connected = false; resetPendingConnectionRequests(); deliveries.removeAll(); connection = "엔진 연결 필요"; problem = message }
    private func startNativeSnapshot(jobID: String, requestKey: String, operation: @escaping @MainActor () async -> Void) {
        guard brokerRequests.insert(requestKey).inserted else { return }
        let taskID = UUID()
        let task = Task { [self] in
            defer {
                brokerRequests.remove(requestKey)
                nativeSnapshotTasks[jobID]?.removeValue(forKey:taskID)
                if nativeSnapshotTasks[jobID]?.isEmpty == true { nativeSnapshotTasks.removeValue(forKey:jobID) }
            }
            await operation()
        }
        nativeSnapshotTasks[jobID,default:[:]][taskID] = task
    }
    private func sendRecordingCommand(_ request: WireCommand) async throws {
        if request.kind == "purge_recording", let id = request.payload["job_id"]?.string {
            // Recheck after every suspension: another token may register while an older one finishes.
            while let tasks = nativeSnapshotTasks[id], !tasks.isEmpty {
                for task in tasks.values { await task.value }
            }
        }
        try await engine.send(request)
    }
    func command(_ kind: String, _ payload: [String:JSONValue] = [:]) {
        guard !DemoContent.enabled else { return }
        let request = WireCommand(kind,payload)
        if ["snapshot_ready","publish_ack"].contains(kind) { internalBrokerReplies.insert(request.request_id) }
        if ["trash_recording","purge_recording","retitle","generate_notes","set_recording_date"].contains(kind), let id = payload["job_id"]?.string { pendingRecordingCommands[request.request_id] = id; cancelObsoleteExport() }
        Task {
            do { try await sendRecordingCommand(request) }
            catch { internalBrokerReplies.remove(request.request_id); pendingRecordingCommands.removeValue(forKey:request.request_id); problem = error.localizedDescription }
        }
    }
    func manualProcessingTitle(_ title: String) -> String { paused ? title + " · 처리 재개" : title }
    func processingCommand(_ kind: String, _ payload: [String:JSONValue] = [:]) {
        guard !DemoContent.enabled else { return }
        if let id = payload["job_id"]?.string, jobs.contains(where:{ $0.id == id && $0.purge_pending == true }) { return }
        let resume = paused ? WireCommand("resume") : nil
        let requested = WireCommand(kind, payload)
        if let id = payload["job_id"]?.string { pendingRecordingCommands[requested.request_id] = id; cancelObsoleteExport() }
        Task {
            do {
                if let resume { try await engine.send(resume) }
                try await sendRecordingCommand(requested)
            } catch { pendingRecordingCommands.removeValue(forKey:requested.request_id); problem = error.localizedDescription }
        }
    }
    func processRecording(_ job: Job) {
        guard job.purge_pending != true, job.deleted_at == nil else { return }
        if job.status == "unprocessed" { processingCommand("enqueue", ["uuids": .array([.string(job.uuid)])]) }
        else if job.status == "failed" { processingCommand("retry", ["job_id": .string(job.id)]) }
    }
    func receive(_ event: WireEvent) async {
        do {
            switch event.kind {
            case "snapshot":
                if let value = event.payload["jobs"] {
                    jobs = try value.decoded([Job].self)
                    refreshPendingJobs.removeAll()
                    if case .array(let values) = value { for item in values { trackRefreshState(item) } }
                    cancelObsoleteExport()
                    await indexTranscripts()
                }
                if let status = event.payload["engine_status"]?.object { updateEngine(status) }
                let first = !connected; connected = true
                connection = "로컬 엔진 연결됨"; reconcileLibrarySelection(); await loadSelection(); if first { scanSources(force:true); await deliverPendingImports() }
            case "job_changed":
                if let value = event.payload["job"] {
                    let job = try value.decoded(Job.self); trackRefreshState(value)
                    if let i = jobs.firstIndex(where: { $0.id == job.id }) {
                        if jobs[i].status != job.status || jobs[i].notes_status != job.notes_status {
                            zipChecked.remove(AutomaticRecordingPolicy.zipKey(jobs[i],destination:zipExportFolder,libraryRevision:library.revision))
                        }
                        jobs[i] = job
                    } else { jobs.append(job) }
                    cancelObsoleteExport()
                    await index(job); reconcileLibrarySelection(); await loadSelection()
                }
            case "job_removed":
                if let id = event.payload["job_id"]?.string {
                    removeRecordingState(id)
                    await loadSelection()
                    while libraryBusy { await libraryWriteTask?.value }
                    if library.assignments[id] != nil {
                        changeLibrary { try await $0.assign(jobIDs:[id],folderID:nil) }
                    }
                }
            case "engine_status": updateEngine(event.payload["engine_status"]?.object ?? event.payload)
            case "model_progress":
                if event.payload["model"]?.string == "notes" {
                    notesModelState = event.payload["state"]?.string ?? notesModelState
                    notesModelStatus = ModelProgress.description(event.payload)
                } else { modelStatus = ModelProgress.description(event.payload) }
                if let e = event.payload["error"], e != .null { problem = "모델 다운로드가 중단되었습니다. 설정에서 다운로드 / 재개를 선택하세요." }
            case "snapshot_requested":
                guard let id = event.payload["job_id"]?.string, let reference = event.payload["source_reference"]?.string else { break }
                let token = event.payload["snapshot_request_id"]
                let requestKey = "snapshot:" + id + ":" + (token?.string ?? "legacy")
                startNativeSnapshot(jobID:id,requestKey:requestKey) { [self] in
                    do {
                        let snapshot = reference.hasPrefix("import:") ? try await imports.snapshot(reference:reference,jobID:id) : try await source.snapshot(reference:reference,jobID:id)
                        var reply: [String:JSONValue] = ["job_id":.string(id),"owned_path":.string(snapshot.owned_path),"revision_before":try .encoded(snapshot.revision_before),"revision_after":try .encoded(snapshot.revision_after)]
                        if let token { reply["snapshot_request_id"] = token }
                        command("snapshot_ready",reply)
                    } catch {
                        var reply: [String:JSONValue] = ["job_id":.string(id),"error":brokerError(error)]
                        if let token { reply["snapshot_request_id"] = token }
                        command("snapshot_ready",reply)
                    }
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
                let obsoleteInternalReply = consumeObsoleteBrokerReply(event.payload)
                if let request = event.payload["request_id"]?.string {
                    completeAutomaticNoteRequest(request,success:event.payload["ok"]?.bool == true)
                    pendingRecordingCommands.removeValue(forKey:request)
                }
                if let request = event.payload["request_id"]?.string, refreshRequests.removeValue(forKey:request) != nil, event.payload["ok"]?.bool == true { scanSources(force:true) }
                if let request = event.payload["request_id"]?.string, let reference = deliveries.removeValue(forKey:request), event.payload["ok"]?.bool == true { try await imports.markDelivered(reference:reference) }
                if event.payload["ok"]?.bool == false && !obsoleteInternalReply { problem = event.payload["error"]?.object?["message"]?.string ?? "요청을 완료하지 못했습니다." }
            case "diagnostic": problem = event.payload["message"]?.string
            default: break
            }
            if ["snapshot","job_changed","job_removed","engine_status","command_result"].contains(event.kind) { scheduleAutomaticOutputs() }
        } catch { problem = "엔진 상태를 해석하지 못했습니다: \(error.localizedDescription)" }
    }
    private func updateEngine(_ status: [String:JSONValue]) {
        if let catalog = status["model_catalog"], let decoded = try? catalog.decoded([SelectableModel].self) { availableModels = decoded }
        if let selection = status["model_selection"]?.object {
            selectedASRModel = selection["asr"]?.string ?? selectedASRModel
            selectedNotesModel = selection["notes"]?.string ?? selectedNotesModel
            canChangeModels = selection["can_change"]?.bool == true
            switch selection["disabled_reason"]?.string {
            case "processing": modelChangeMessage = "진행 중인 녹음 처리를 마친 뒤 모델을 바꿀 수 있습니다."
            case "download_active": modelChangeMessage = "다운로드를 마친 뒤 모델을 바꿀 수 있습니다."
            case .some: modelChangeMessage = "현재 모델 설정을 변경할 수 없습니다."
            case .none: modelChangeMessage = nil
            }
        }
        if let recommendation = status["model_recommendations"]?.object {
            recommendedASR = recommendation["asr"]?.string
            recommendedNotes = recommendation["notes"]?.string
            if let device = recommendation["device"]?.object {
                let memory = device["ram_bytes"]?.numeric.map { "\(Int(($0 / 1_073_741_824).rounded())) GB" } ?? "확인 불가"
                modelDeviceDescription = device["unified_memory"]?.bool == true ? "Apple Silicon · 통합 메모리 \(memory)" : "메모리 \(memory)"
            }
        }
        modelsReady = status["models_ready"]?.bool ?? modelsReady; paused = status["paused"]?.bool ?? paused
        let states = status["model_status"]?.object ?? [:]
        if modelsReady { modelStatus = "전사 · 정렬 · 제목 모델 준비됨" }
        else if ["asr","aligner","title"].contains(where:{ states[$0]?.object?["state"]?.string == "verifying" }) { modelStatus = "필수 모델 파일 검증 중" }
        else if !modelStatus.contains("다운로드 중") && !modelStatus.contains("기다리는 중") { modelStatus = "필수 모델 다운로드가 필요합니다." }
        if let ready = status["notes_ready"]?.bool { notesModelReady = ready }
        if let state = states["notes"]?.object?["state"]?.string {
            // Inventory snapshots can arrive while the explicit downloader is still active.
            let downloading = ["resolving","downloading","stalled"].contains(notesModelState)
            if !downloading || state == "ready" {
                notesModelState = state
                notesModelStatus = state == "ready" ? "상세 노트 모델 준비됨" : state == "verifying" ? "상세 노트 모델 파일 검증 중" : "상세 노트 모델 다운로드가 필요합니다."
            }
        }
    }
    func downloadNotesModel() { command("download_models",["notes_only":.bool(true)]) }
    func applyModelSelection(asr: String, notes: String) {
        guard canChangeModels else { return }
        command("set_models", ["asr": .string(asr), "notes": .string(notes)])
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
        guard job.deleted_at == nil else { transcriptIndex.removeValue(forKey:job.id); return }
        guard let path = job.artifact_path, let result = try? await readTranscript(path) else { return }
        var text = result.text
        if let path = job.notes_path, let note = try? await readNotes(path,text:result.text) { text += "\n" + NotesDisplay.markdown(note,title:job.title) }
        guard jobs.contains(where:{ $0.id == job.id && $0.deleted_at == nil }) else { return }
        transcriptIndex[job.id] = text
    }
    private func indexTranscripts() async { for job in jobs { await index(job) } }
    private func loadDemoSelection() {
        guard let job = selected else { transcript = nil; notes = nil; quoteRow = nil; focusedQuote = nil; return }; transcript = DemoContent.transcript(for:job.id); notes = DemoContent.notes(for:job.id); quoteRow = nil; focusedQuote = nil; topic = job.topic ?? ""; dateInput = job.recording_date ?? ""; currentTime = 0; audioDuration = 0
    }
    func loadSelection() async {
        if DemoContent.enabled { loadDemoSelection(); return }
        let token = UUID(); loadID = token
        guard let job = selected, job.deleted_at == nil else { player?.stop(); player = nil; loadedAudioKey = nil; loadedSelection = nil; transcript = nil; notes = nil; notesReadError = nil; playing = false; return }
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
        if ticks % 30 == 0 && connected { checkMobileSync() }
        if ticks % 60 == 0 { scheduleAutomaticOutputs(reconcile:true) }
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
        guard !DemoContent.enabled else { return }
        if force { rescanRequested = true }
        guard connected, !scanning else { return }
        let force = force || rescanRequested
        rescanRequested = false; scanning = true
        Task { [self] in
            defer {
                scanning = false
                if rescanRequested { scanSources(force:true) }
            }
            do {
                if sourceMode == "voice_memos" {
                    if !force && Date().timeIntervalSince(lastSourceScan) < 60 { return }; lastSourceScan = Date()
                    let memos = try await source.scan()
                    sourceDiagnostic = NSRunningApplication.runningApplications(withBundleIdentifier:"com.apple.VoiceMemos").isEmpty ? "음성 메모 연결됨 · 새 녹음은 자동 처리합니다." : "Mac 음성 메모가 열려 있습니다. 다운로드를 마친 뒤 음성 메모를 종료하면 전사를 시작합니다."
                    command("discover",["memos":try .encoded(memos)])
                } else if !inputFolder.isEmpty {
                    if !outputFolder.isEmpty { try FolderConnection.validate(input:URL(fileURLWithPath:inputFolder),output:URL(fileURLWithPath:outputFolder)) }
                    if force { await watcher.retryFiles() }
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
    func setDate() { guard let job = selected, job.deleted_at == nil else { return }; do { let date = try Validation.recordingDate(dateInput); command("set_recording_date", ["job_id": .string(job.id), "recording_date": .string(date)]) } catch { problem = error.localizedDescription } }
    func retitle(generate: Bool) { guard let job = selected, job.deleted_at == nil else { return }; command("retitle", ["job_id": .string(job.id), generate ? "generate" : "topic": generate ? .bool(true) : .string(topic)]) }
    func chooseFolder(output: Bool) {
        guard !DemoContent.enabled else { return }
        if let panel = folderConnectionPanel { panel.makeKeyAndOrderFront(nil); return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.canCreateDirectories = output
        panel.allowsMultipleSelection = false
        panel.title = output ? "결과 폴더 선택" : "입력 폴더 다시 연결"
        let currentPath = output ? outputFolder : inputFolder
        if !currentPath.isEmpty { panel.directoryURL = URL(fileURLWithPath:currentPath) }
        folderConnectionPanel = panel
        panel.begin { [weak self,weak panel] response in
            Task { @MainActor in
                guard let self, let panel, self.folderConnectionPanel === panel else { return }
                defer { self.folderConnectionPanel = nil }
                guard response == .OK, let url = panel.url else { return }
                do {
                    if output, self.sourceMode == "folder", !self.inputFolder.isEmpty { try FolderConnection.validate(input:URL(fileURLWithPath:self.inputFolder),output:url) }
                    if !output, !self.outputFolder.isEmpty { try FolderConnection.validate(input:url,output:URL(fileURLWithPath:self.outputFolder)) }
                } catch { self.problem = error.localizedDescription; return }
                if output {
                    self.outputFolder = url.path; UserDefaults.standard.set(url.path,forKey:"outputFolder")
                    for job in self.activeJobs where job.status == "saving" { self.command("retry",["job_id":.string(job.id)]) }
                } else {
                    self.inputFolder = url.path; UserDefaults.standard.set(url.path,forKey:"inputFolder")
                    self.sourceMode = "folder"; UserDefaults.standard.set("folder",forKey:"sourceMode")
                    self.scanSources(force:true)
                }
            }
        }
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
                self.zipChecked.removeAll(); self.zipRetryAfter.removeAll()
                self.scheduleAutomaticOutputs()
            }
        }
    }
    private func trackRefreshState(_ value: JSONValue) {
        guard let object = value.object, let id = object["id"]?.string else { return }
        if object["refresh_pending"]?.bool == true || object["wait_reason"]?.string == "source_refresh" { refreshPendingJobs.insert(id) }
        else { refreshPendingJobs.remove(id) }
    }
    private func resetPendingConnectionRequests() {
        clearUnacknowledgedNotes()
        pendingRecordingCommands.removeAll()
        refreshRequests.removeAll()
    }
    private func consumeObsoleteBrokerReply(_ payload: [String:JSONValue]) -> Bool {
        guard let requestID = payload["request_id"]?.string,
              internalBrokerReplies.remove(requestID) != nil,
              payload["ok"]?.bool == false,
              let error = payload["error"]?.object,
              error["code"]?.string == "invalid_command",
              let message = error["message"]?.string else { return false }
        return ["stale_snapshot","snapshot_not_requested","stale_publication","recording_trashed"].contains(message)
    }
    private func completeAutomaticNoteRequest(_ requestID: String, success: Bool) {
        guard let key = automaticNoteRequests.removeValue(forKey:requestID) else { return }
        if !success {
            automaticNotesRequested.remove(key)
            automaticNotesRetryAfter[key] = Date().addingTimeInterval(60)
        }
    }
    private func clearUnacknowledgedNotes() {
        for key in automaticNoteRequests.values {
            automaticNotesRequested.remove(key)
            automaticNotesRetryAfter.removeValue(forKey:key)
        }
        automaticNoteRequests.removeAll()
    }
    private func cancelObsoleteExport() {
        guard let id = activeExportJobID else { return }
        guard let job = jobs.first(where: { $0.id == id }), job.deleted_at == nil,
              !recordingCommandPending(id), libraryReady, !libraryBusy,
              AutomaticRecordingPolicy.zipKey(job,destination:zipExportFolder,libraryRevision:library.revision) == activeExportKey else {
            activeExportTask?.cancel()
            return
        }
    }
    private func recordingCommandPending(_ id: String) -> Bool {
        refreshPendingJobs.contains(id) || refreshRequests.values.contains(id) || pendingRecordingCommands.values.contains(id)
    }
    private func scheduleAutomaticOutputs(reconcile: Bool = false) {
        cancelObsoleteExport()
        guard !DemoContent.enabled, connected, !zipExportClosing else { return }
        requestMissingNotes()
        if !mobileSyncLoaded || reconcile { checkMobileSync() }
        guard autoZIPEnabled else { return }
        checkRecordingZIPs(manual:false,reconcile:reconcile)
    }
    private func requestMissingNotes() {
        guard !paused, notesModelReady, automaticNotesTask == nil else { return }
        let ids = jobs.filter { AutomaticRecordingPolicy.needsNotes($0) && !automaticNotesRequested.contains(AutomaticRecordingPolicy.notesKey($0)) && (automaticNotesRetryAfter[AutomaticRecordingPolicy.notesKey($0)] ?? .distantPast) <= Date() && !recordingCommandPending($0.id) }.map(\.id)
        guard !ids.isEmpty else { return }
        automaticNotesTask = Task { [self] in
            defer { automaticNotesTask = nil }
            for id in ids {
                guard connected, !paused, !zipExportClosing, notesModelReady else { break }
                guard let job = jobs.first(where: { $0.id == id }), AutomaticRecordingPolicy.needsNotes(job), !recordingCommandPending(id) else { continue }
                let key = AutomaticRecordingPolicy.notesKey(job)
                guard (automaticNotesRetryAfter[key] ?? .distantPast) <= Date(), automaticNotesRequested.insert(key).inserted else { continue }
                let request = WireCommand("generate_notes",["job_id":.string(id)])
                pendingRecordingCommands[request.request_id] = id
                automaticNoteRequests[request.request_id] = key
                do { try await sendRecordingCommand(request) }
                catch {
                    completeAutomaticNoteRequest(request.request_id,success:false)
                    pendingRecordingCommands.removeValue(forKey:request.request_id); problem = error.localizedDescription
                }
            }
        }
    }
    func setAutoZIPEnabled(_ enabled: Bool) {
        autoZIPEnabled = enabled; UserDefaults.standard.set(enabled,forKey:"autoZIPEnabled")
        if enabled { scheduleAutomaticOutputs() }
    }
    func exportCompletedZIP() { checkRecordingZIPs(manual:true,reconcile:true) }
    private func checkRecordingZIPs(manual: Bool, reconcile: Bool) {
        guard !DemoContent.enabled, connected, !zipExportClosing, !zipExportFolder.isEmpty, libraryReady, !libraryBusy else { return }
        if zipExportBusy { zipRescheduleRequested = true; return }
        let destination = URL(fileURLWithPath:zipExportFolder)
        let now = Date()
        let candidates = jobs.filter { job in
            guard job.deleted_at == nil, !recordingCommandPending(job.id) else { return false }
            let key = AutomaticRecordingPolicy.zipKey(job,destination:destination.path,libraryRevision:library.revision)
            return (manual || (zipRetryAfter[key] ?? .distantPast) <= now) && (reconcile || !zipChecked.contains(key))
        }.map(\.id)
        guard !candidates.isEmpty else { return }
        zipExportBusy = true; zipExportError = nil
        if manual { zipExportProgress = "녹음별 ZIP 확인 중" }
        zipExportTask = Task { [self] in
            var failures: [String] = [], written = 0, checked = 0
            defer {
                zipExportBusy = false
                if zipRescheduleRequested { zipRescheduleRequested = false; scheduleAutomaticOutputs() }
            }
            for id in candidates {
                guard !zipExportClosing, manual || autoZIPEnabled else { break }
                // Resolve the latest immutable value immediately before the actor call.
                guard let job = jobs.first(where: { $0.id == id }), job.deleted_at == nil, !recordingCommandPending(id) else { continue }
                guard libraryReady, !libraryBusy else { zipRescheduleRequested = true; break }
                let organization = library
                let key = AutomaticRecordingPolicy.zipKey(job,destination:destination.path,libraryRevision:organization.revision)
                activeExportJobID = id; activeExportKey = key
                let child = Task { [self] in
                    try Task.checkCancellation()
                    let moved = try await perRecordingZIPStore.relocateIfNeeded(jobID:id,destination:destination,organization:organization) { [weak self] message in
                        await self?.updateZIPProgress(message)
                    }
                    try Task.checkCancellation()
                    guard AutomaticRecordingPolicy.zipEligible(job) else { return moved }
                    let created = try await perRecordingZIPStore.exportIfNeeded(job:job,destination:destination,organization:organization) { [weak self] message in
                        await self?.updateZIPProgress(message)
                    }
                    return created ?? moved
                }
                activeExportTask = child
                defer { activeExportTask = nil; activeExportJobID = nil; activeExportKey = nil }
                do {
                    let result = try await child.value
                    zipChecked.insert(key); zipRetryAfter.removeValue(forKey:key); checked += 1
                    if let result { zipExportResult = result; written += 1 }
                } catch is CancellationError {
                    // A newer lifecycle state superseded this capture; continue the other recordings.
                    continue
                } catch {
                    if child.isCancelled { continue }
                    failures.append(job.title + ": " + error.localizedDescription)
                    zipRetryAfter[key] = Date().addingTimeInterval(60)
                }
            }
            zipExportError = failures.isEmpty ? nil : failures.joined(separator:"\n")
            if !failures.isEmpty { zipExportProgress = "ZIP 저장 \(written)개 · 확인 필요 \(failures.count)개" }
            else if written > 0 { zipExportProgress = "녹음별 ZIP 저장 완료 · \(written)개" }
            else if manual { zipExportProgress = checked > 0 ? "모든 ZIP이 최신입니다" : "현재 ZIP 저장을 기다리는 녹음입니다" }
        }
    }
    func flushZIPExport() async {
        zipExportClosing = true
        mobileSyncTask?.cancel()
        await mobileSyncTask?.value
        if zipExportBusy { zipExportProgress = "종료 대기 · ZIP 내보내기를 마치는 중" }
        await automaticNotesTask?.value
        await zipExportTask?.value
    }
    func chooseMobileSyncFolder() {
        guard !DemoContent.enabled, !mobileSyncBusy, !zipExportClosing else { return }
        if let panel = mobileSyncPanel { panel.makeKeyAndOrderFront(nil); return }
        let panel = NSOpenPanel()
        panel.title = "모바일 녹음 동기화 폴더"
        panel.prompt = "연결"
        panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.allowsMultipleSelection = false; panel.canCreateDirectories = true
        mobileSyncPanel = panel
        panel.begin { [weak self, weak panel] response in
            Task { @MainActor in
                guard let self, let panel, self.mobileSyncPanel === panel else { return }
                self.mobileSyncPanel = nil
                guard response == .OK, let url = panel.url else { return }
                self.mobileSyncBusy = true
                self.mobileSyncTask = Task { [self] in
                    defer { self.mobileSyncBusy = false; self.mobileSyncTask = nil }
                    do {
                        for value in [self.inputFolder, self.outputFolder, self.zipExportFolder] where !value.isEmpty {
                            try FolderConnection.validate(input: URL(fileURLWithPath: value), output: url)
                        }
                        let config = try await self.mobileSync.register(syncRoot: url, displayName: Host.current().localizedName ?? "Vemo Mac")
                        self.mobileSyncFolder = config.syncRoot.path; self.mobileSyncLoaded = true
                        self.mobileResultKeys.removeAll()
                        self.mobileSyncStatus = "연결됨 · 클라우드 앱에서 이 폴더의 오프라인 사용을 켜세요."
                    } catch { self.mobileSyncStatus = (error as? BrokerFailure)?.message ?? "폴더 연결을 완료하지 못했습니다." }
                }
            }
        }
    }
    func disconnectMobileSync() {
        guard !mobileSyncBusy, !zipExportClosing else { return }
        mobileSyncBusy = true
        mobileSyncTask = Task { [self] in
            defer { mobileSyncBusy = false; mobileSyncTask = nil }
            do {
                try await mobileSync.disconnect()
                mobileSyncFolder = ""; mobileResultKeys.removeAll(); mobileSyncStatus = "연결 해제됨 · 원본과 클라우드 파일은 유지됩니다."
            } catch { mobileSyncStatus = "연결을 해제하지 못했습니다. 다시 시도하세요." }
        }
    }
    func checkMobileSync() {
        guard !DemoContent.enabled, connected, !mobileSyncBusy, !zipExportClosing else { return }
        mobileSyncBusy = true
        mobileSyncTask = Task { [self] in
            defer { mobileSyncBusy = false; mobileSyncTask = nil }
            do {
                mobileSyncLoaded = true
                guard let config = try await mobileSync.configuration() else { return }
                mobileSyncFolder = config.syncRoot.path
                let report = try await mobileSync.scan(importBroker: imports)
                try Task.checkCancellation()
                await deliverPendingImports()
                let mappings = try await mobileSync.mappings()
                var saved = 0
                var publicationFailures: [String] = []
                for mapping in mappings where !mapping.suppressed {
                    try Task.checkCancellation()
                    guard let job = jobs.first(where: { $0.uuid == mapping.memoUUID }) else { continue }
                    if !mapping.delivered { try await mobileSync.bindJob(memoUUID: job.uuid, jobID: job.id) }
                    guard job.deleted_at == nil, job.purge_pending != true, !recordingCommandPending(job.id),
                          job.status == "completed", job.notes_status == "completed" else { continue }
                    let key = AutomaticRecordingPolicy.zipKey(job, destination: config.syncRoot.path, libraryRevision: 0)
                    // A provider or user can remove the remote-facing file during
                    // this session. Reconcile missing files even after a prior save.
                    let resultURL = config.syncRoot.appendingPathComponent("results")
                        .appendingPathComponent(mapping.recordingID)
                        .appendingPathComponent(mapping.audioSHA256)
                        .appendingPathComponent("result.json")
                    guard !mobileResultKeys.contains(key) || !FileManager.default.fileExists(atPath: resultURL.path) else { continue }
                    do {
                        _ = try await mobileSync.publish(job: job)
                        mobileResultKeys.insert(key); saved += 1
                    } catch is CancellationError { throw CancellationError() }
                    catch { publicationFailures.append((error as? BrokerFailure)?.message ?? "모바일 결과 저장을 다시 시도합니다.") }
                }
                if let failure = publicationFailures.first {
                    mobileSyncStatus = "결과 저장 \(saved)개 · 확인 필요 \(publicationFailures.count)개 · \(failure)"
                } else if let issue = report.issues.first {
                    mobileSyncStatus = "확인 필요 \(report.issues.count)개 · \(issue.message)"
                } else if saved > 0 {
                    mobileSyncStatus = "결과 \(saved)개를 로컬 폴더에 저장했습니다. 업로드는 클라우드 앱이 진행합니다."
                } else {
                    mobileSyncStatus = "연결됨 · 완료된 녹음과 결과를 30초마다 확인합니다."
                }
            } catch is CancellationError { }
            catch { mobileSyncStatus = (error as? BrokerFailure)?.message ?? "모바일 동기화 폴더를 확인하지 못했습니다." }
        }
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
    func chooseFiles() {
        guard !DemoContent.enabled else { return }
        if let panel = importPanel { panel.makeKeyAndOrderFront(nil); return }
        let panel = NSOpenPanel()
        panel.title = "오디오 가져오기"; panel.prompt = "가져오기"
        panel.allowsMultipleSelection = true; panel.canChooseDirectories = false; panel.canChooseFiles = true
        importPanel = panel
        // A retained standalone panel avoids both duplicate panels and unreliable background sheet anchors.
        panel.begin { [weak self, weak panel] response in
            Task { @MainActor in
                guard let self, let panel, self.importPanel === panel else { return }
                defer { self.importPanel = nil }
                if response == .OK { self.importFiles(panel.urls) }
            }
        }
    }
    func trashRecording(_ job: Job) {
        guard job.deleted_at == nil else { return }
        command("trash_recording",["job_id":.string(job.id)])
    }
    var canChangeRecordings: Bool { connected && !recordingBatchBusy }
    func requestRecordingAction(_ kind: String, ids: [String]) {
        guard ["trash_recording","restore_recording","purge_recording"].contains(kind), !recordingBatchBusy else { return }
        guard connected else { problem = "엔진을 다시 연결한 뒤 시도하세요."; return }
        let needsTrash = kind != "trash_recording"
        let valid = jobs.filter { ids.contains($0.id) && ($0.deleted_at != nil) == needsTrash && (kind == "purge_recording" || $0.purge_pending != true) }.map(\.id)
        guard !valid.isEmpty else { return }
        recordingConfirmation = RecordingBulkConfirmation(kind:kind,ids:valid)
    }
    func confirmRecordingAction(_ confirmation: RecordingBulkConfirmation) {
        recordingConfirmation = nil
        guard !DemoContent.enabled, connected, !recordingBatchBusy, ["trash_recording","restore_recording","purge_recording"].contains(confirmation.kind) else { return }
        recordingBatchBusy = true
        Task { [self] in
            defer { recordingBatchBusy = false }
            var errors: [String] = []
            for id in confirmation.ids {
                guard let job = jobs.first(where:{ $0.id == id }),
                      (job.deleted_at != nil) == (confirmation.kind != "trash_recording"), (confirmation.kind == "purge_recording" || job.purge_pending != true), !recordingCommandPending(id) else { continue }
                let request = WireCommand(confirmation.kind,["job_id":.string(id)])
                pendingRecordingCommands[request.request_id] = id
                cancelObsoleteExport()
                do { try await sendRecordingCommand(request) }
                catch { pendingRecordingCommands.removeValue(forKey:request.request_id); errors.append(job.title + ": " + error.localizedDescription) }
                await Task.yield()
            }
            if !errors.isEmpty { problem = errors.joined(separator:"\n") }
        }
    }
    func purgeRecording(_ job: Job) {
        guard jobs.contains(where:{ $0.id == job.id && $0.deleted_at != nil }), !recordingCommandPending(job.id) else { return }
        command("purge_recording",["job_id":.string(job.id)])
    }
    private func removeRecordingState(_ id: String) {
        jobs.removeAll { $0.id == id }
        transcriptIndex.removeValue(forKey:id)
        refreshPendingJobs.remove(id)
        refreshRequests = refreshRequests.filter { $0.value != id }
        pendingRecordingCommands = pendingRecordingCommands.filter { $0.value != id }
        // Session fingerprints are JSON arrays; match the exact job slot, never a substring.
        func belongs(_ key: String, slot: Int) -> Bool {
            guard let values = try? JSONDecoder().decode([String?].self,from:Data(key.utf8)), values.indices.contains(slot) else { return false }
            return values[slot] == id
        }
        automaticNotesRequested = automaticNotesRequested.filter { !belongs($0,slot:0) }
        automaticNotesRetryAfter = automaticNotesRetryAfter.filter { !belongs($0.key,slot:0) }
        automaticNoteRequests = automaticNoteRequests.filter { !belongs($0.value,slot:0) }
        zipChecked = zipChecked.filter { !belongs($0,slot:1) }
        zipRetryAfter = zipRetryAfter.filter { !belongs($0.key,slot:1) }
        if editingTitleJobID == id { cancelTitleEdit() }
        cancelObsoleteExport()
        reconcileLibrarySelection()
    }
    func restoreRecording(_ job: Job) { guard let current = jobs.first(where:{ $0.id == job.id }), current.deleted_at != nil, current.purge_pending != true else { return }; command("restore_recording",["job_id":.string(job.id)]) }
    func refreshRecording(_ job: Job) {
        guard !DemoContent.enabled, job.deleted_at == nil else { return }
        let request = WireCommand("refresh_recording",["job_id":.string(job.id)])
        refreshRequests[request.request_id] = job.id
        cancelObsoleteExport()
        Task {
            do { try await sendRecordingCommand(request) }
            catch { refreshRequests.removeValue(forKey:request.request_id); problem = error.localizedDescription }
        }
    }
    func isEditingTitle(_ job: Job, at location: TitleEditingLocation) -> Bool {
        editingTitleJobID == job.id && editingTitleLocation == location
    }
    func beginTitleEdit(_ job: Job, at location: TitleEditingLocation) {
        guard activeJobs.contains(where:{ $0.id == job.id }), job.deleted_at == nil else { return }
        setRecordingSelection([job.id],primary:job.id)
        titleDraft = job.topic ?? job.original_title ?? ""
        editingTitleJobID = job.id
        editingTitleLocation = location
    }
    func cancelTitleEdit() {
        editingTitleJobID = nil
        editingTitleLocation = nil
        titleDraft = ""
    }
    func saveTitleEdit() {
        guard let id = editingTitleJobID, selection == id,
              activeJobs.contains(where:{ $0.id == id }) else { cancelTitleEdit(); return }
        let title = titleDraft.trimmingCharacters(in:.whitespacesAndNewlines)
        guard !title.isEmpty else { return }
        command("retitle",["job_id":.string(id),"topic":.string(title)])
        cancelTitleEdit()
    }
    var canGenerateNotes: Bool { transcript != nil && selected?.artifact_path != nil && selected.map { $0.deleted_at == nil && ["completed","needs_recording_date","failed"].contains($0.status) } == true }
    func generateNotes() { guard let job = selected, canGenerateNotes else { return }; processingCommand("generate_notes",["job_id":.string(job.id)]) }
    func revealQuote(_ quote: String) {
        guard let transcript, let target = NotesDisplay.quoteTarget(quote,in:transcript) else { return }
        focusedQuote = quote; quoteRow = target.row; detailMode = "transcript"
        if let timing = target.timing { seek(timing.start_s) }
    }
    func copyNotes() { guard let notes, let job = selected, job.deleted_at == nil else { return }; NSPasteboard.general.clearContents(); NSPasteboard.general.setString(NotesDisplay.markdown(notes,title:job.title),forType:.string) }
    func exportNotes() {
        guard let notes, let job = selected, job.deleted_at == nil else { return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = job.title.replacingOccurrences(of:"/",with:"_") + " 노트.md"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let text = NotesDisplay.markdown(notes,title:job.title)
        Task { do { try await Task.detached { try text.write(to:url,atomically:true,encoding:.utf8) }.value } catch { problem = error.localizedDescription } }
    }
    func copyTranscript() { guard selected?.deleted_at == nil, let transcript else { return }; NSPasteboard.general.clearContents(); NSPasteboard.general.setString(transcript.text, forType: .string) }
    func export(_ format: String) {
        guard let job = selected, job.deleted_at == nil, let transcript else { return }
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

// Pure readiness policy shared by event-driven and periodic scheduling.
enum AutomaticRecordingPolicy {
    static func settled(_ job: Job) -> Bool {
        job.deleted_at == nil && ["completed","needs_recording_date"].contains(job.status)
    }
    static func needsNotes(_ job: Job) -> Bool {
        settled(job) && job.artifact_path != nil && job.notes_path == nil && (job.notes_status == nil || job.notes_status == "pending")
    }
    static func zipEligible(_ job: Job) -> Bool {
        settled(job) && job.artifact_path != nil && job.audio_path != nil && job.notes_path != nil && job.notes_status == "completed"
    }
    static func notesKey(_ job: Job) -> String { key([job.id,job.artifact_path]) }
    static func zipKey(_ job: Job, destination: String, libraryRevision: Int = 0) -> String {
        key([destination,job.id,job.title,job.recording_date,job.audio_path,job.artifact_path,job.notes_path,String(libraryRevision),job.status,job.notes_status,job.deleted_at])
    }
    private static func key(_ values: [String?]) -> String {
        String(decoding:(try? JSONEncoder().encode(values)) ?? Data(),as:UTF8.self)
    }
}
