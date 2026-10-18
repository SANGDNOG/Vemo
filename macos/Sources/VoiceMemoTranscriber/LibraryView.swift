import SwiftUI
import NativeCore
import UniformTypeIdentifiers

struct LibraryView: View {
    @ObservedObject var model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    @ViewState private var foldersExpanded = true
    @ViewState private var folderEditor = false
    @ViewState private var editingFolderID: String?
    @ViewState private var folderName = ""
    @ViewState private var folderParentID = ""
    @ViewState private var collapsedFolderIDs: Set<String> = []
    @ViewState private var deletingFolder: LibraryFolder?
    @ViewState private var dragSession = VemoLibraryDragSession()
    @ViewState private var dropTarget: String?
    @ViewState private var columnVisibility: NavigationSplitViewVisibility = .all

    var body: some View {
        splitContent
        // Let the full-height split panes draw one continuous column boundary.
        .toolbarBackground(.hidden, for: .windowToolbar)
        .toolbar {
            ToolbarItem(placement: .principal) { Text(model.overallStatus).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            ToolbarItem { if model.importing { ProgressView().controlSize(.small).help("오디오를 안전하게 가져오는 중") } }
        }
        .onChange(of: model.selection) { _, _ in Task { await model.loadSelection() } }
        .onChange(of: model.visibleJobs.map(\.id)) { _, _ in model.reconcileLibrarySelection() }
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            for provider in providers { _ = provider.loadObject(ofClass: URL.self) { url, _ in if let url { Task { @MainActor in model.importFiles([url]) } } } }; return !providers.isEmpty
        }
        .alert(model.recordingConfirmation?.title ?? "녹음",isPresented:Binding(
            get:{ model.recordingConfirmation != nil },set:{ if !$0 { model.recordingConfirmation = nil } }
        ),presenting:model.recordingConfirmation) { confirmation in
            Button("취소",role:.cancel) { model.recordingConfirmation = nil }
            Button(confirmation.button,role:confirmation.kind == "restore_recording" ? nil : .destructive) { model.confirmRecordingAction(confirmation) }
        } message: { confirmation in Text(confirmation.message) }
        .sheet(isPresented: $folderEditor) {
            VStack(alignment: .leading, spacing: 20) {
                HStack(spacing: 12) {
                    Image(systemName: "folder.badge.plus").font(.system(size: 19, weight: .light))
                        .foregroundStyle(VemoTheme.ink).frame(width: 42, height: 42)
                        .background(VemoTheme.wash, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                    VStack(alignment: .leading, spacing: 4) {
                        Text(editingFolderID == nil ? "새 폴더" : "폴더 수정").font(.title3.weight(.semibold))
                        Text("녹음을 원하는 구조로 정리하세요.").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                }
                VStack(alignment: .leading, spacing: 8) {
                    Text("폴더 이름").font(.caption.weight(.medium)).foregroundStyle(.secondary)
                    TextField("이름 입력", text: $folderName).textFieldStyle(.plain)
                        .padding(11).background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                        .accessibilityLabel("폴더 이름").onSubmit { saveFolder() }
                }
                VemoParentFolderPicker(folders: model.library.folders, editingID: editingFolderID, selection: $folderParentID)
                HStack(spacing: 10) {
                    Spacer()
                    Button("취소") { folderEditor = false }.keyboardShortcut(.cancelAction)
                    Button(editingFolderID == nil ? "만들기" : "저장") { saveFolder() }
                        .buttonStyle(VemoButtonStyle(emphasis: .primary)).keyboardShortcut(.defaultAction)
                        .disabled(folderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.libraryBusy)
                }
            }.padding(24).frame(width: 400).tint(VemoTheme.accent).buttonStyle(VemoButtonStyle())
        }

        .confirmationDialog("폴더를 삭제할까요?", isPresented: Binding(get: { deletingFolder != nil }, set: { if !$0 { deletingFolder = nil } }), titleVisibility: .visible) {
            if let folder = deletingFolder { Button("폴더 삭제", role: .destructive) { model.deleteLibraryFolder(folder.id); deletingFolder = nil }; Button("취소", role: .cancel) { deletingFolder = nil } }
        } message: { Text(folderDeletionMessage) }
        .safeAreaInset(edge: .bottom) {
            if let problem = model.problem {
                HStack(alignment: .top) { Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange); Text(problem).font(.callout).textSelection(.enabled); Spacer(); Button("닫기") { model.problem = nil } }.padding(12).background(.thinMaterial)
            }
        }
    }

    private var folderDeletionMessage: String {
        guard let folder = deletingFolder else { return "" }
        let count = LibraryFolderHierarchy.descendantIDs(of: folder.id, in: model.library.folders).count
        let scope = count == 0 ? "이 폴더를 삭제합니다." : "이 폴더와 하위 폴더 \(count)개를 함께 삭제합니다."
        return scope + " 안에 있던 녹음은 미분류로 돌아가며, 녹음 파일·전사문·노트는 보존됩니다."
    }

    private var folderNavigation: some View {
        VStack(spacing: 3) {
            HStack {
                Button { withAnimation(VemoTheme.motion(reducedMotion)) { foldersExpanded.toggle() } } label: {
                    HStack(spacing: 6) { Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold)).rotationEffect(.degrees(foldersExpanded ? 90 : 0)); Text("라이브러리").font(.caption.weight(.semibold)) }.foregroundStyle(.secondary).padding(.vertical, 5).contentShape(Rectangle())
                }.buttonStyle(.plain).accessibilityLabel(foldersExpanded ? "폴더 접기" : "폴더 펼치기")
                Spacer()
                Menu {
                    Button("하위 폴더 만들기…") { beginNewFolder(parentID: model.libraryFilter) }
                    Button("이름·상위 폴더 변경…") {
                        if let folder = model.library.folders.first(where: { $0.id == model.libraryFilter }) { beginEditing(folder) }
                    }
                    Button("폴더 삭제…", role: .destructive) { deletingFolder = model.library.folders.first { $0.id == model.libraryFilter } }
                } label: { Image(systemName: "ellipsis").font(.system(size: 12)) }
                    .menuStyle(.button).buttonStyle(VemoButtonStyle(emphasis: .quiet)).fixedSize().foregroundStyle(.secondary)
                    .accessibilityLabel("선택 폴더 관리").help("선택 폴더 관리")
                    .disabled(!model.libraryReady || model.libraryBusy || !model.library.folders.contains(where: { $0.id == model.libraryFilter }))
                Button { beginNewFolder(parentID: model.library.folders.first { $0.id == model.libraryFilter }?.id) } label: { Image(systemName: "plus").font(.system(size: 12, weight: .medium)) }.buttonStyle(VemoButtonStyle(emphasis: .quiet)).accessibilityLabel("새 폴더").help("새 폴더 · 선택한 폴더 안에 생성").disabled(!model.libraryReady || model.libraryBusy)
            }.padding(.horizontal, 5)
            if foldersExpanded {
                folderRow("전체 녹음", icon: "square.stack", filter: "all")
                folderRow("미분류", icon: "tray", filter: "unfiled")
                folderRow("휴지통", icon: "trash", filter: "trash")
                if !model.library.folders.isEmpty {
                    HStack(spacing: 7) {
                        Image(systemName: "square.stack.3d.up").font(.system(size: 10))
                        Text("폴더").font(.system(size: 10, weight: .medium))
                        Spacer()
                    }.foregroundStyle(dropTarget == "root" ? VemoTheme.ink : Color.secondary)
                        .padding(.horizontal, 10).frame(height: 28)
                        .background(dropTarget == "root" ? VemoTheme.wash : .clear, in: RoundedRectangle(cornerRadius: 8))
                        .contentShape(Rectangle())
                        .overlay(nativeInput(label: "폴더", target: .root, key: "root"))
                        .help("폴더를 여기에 드래그하면 최상위로 이동합니다")

                    ScrollView {
                        VStack(spacing: 3) {
                            ForEach(folderTreeRows, id: \.id) { row in
                                treeFolderRow(row)
                                    .transition(.opacity)
                                    .contextMenu {
                                        Button("하위 폴더 만들기…") { beginNewFolder(parentID: row.id) }.disabled(model.libraryBusy)
                                        Button("이름·상위 폴더 변경…") { beginEditing(row.folder) }.disabled(model.libraryBusy)
                                        Button("폴더 삭제…", role: .destructive) { deletingFolder = row.folder }.disabled(model.libraryBusy)
                                    }
                            }
                        }.animation(VemoTheme.motion(reducedMotion), value: folderTreeRows)
                            .background(VemoScrollAppearance())
                    }.frame(maxHeight: .infinity)
                }
            }
        }.padding(.horizontal, 12).padding(.top, 8).padding(.bottom, 12)
    }
    private var folderTreeRows: [LibraryFolderTreeRow] {
        LibraryFolderHierarchy.rows(model.library.folders, collapsedIDs: collapsedFolderIDs)
    }
    private func treeFolderRow(_ row: LibraryFolderTreeRow) -> some View {
        VemoFolderRow(name: row.folder.name, icon: model.libraryFilter == row.id ? "folder.fill" : "folder",
                      count: model.folderCount(row.id), depth: row.depth, selected: model.libraryFilter == row.id,
                      targeted: dropTarget == row.id, expanded: row.hasChildren ? !collapsedFolderIDs.contains(row.id) : nil,
                      select: { selectFolder(row.id) }, toggle: {
            withAnimation(VemoTheme.motion(reducedMotion)) {
                if !collapsedFolderIDs.insert(row.id).inserted { collapsedFolderIDs.remove(row.id) }
            }
        }, input: nativeInput(label: row.folder.name, target: .folder(row.id), key: row.id,
                              select: { selectFolder(row.id) }, folderID: row.id))
        .help(LibraryFolderHierarchy.path(for: row.id, in: model.library.folders) + " · 드래그로 이동 · 직접 넣은 녹음만 표시")
    }
    private func selectFolder(_ id: String) {
        withAnimation(VemoTheme.motion(reducedMotion)) { model.libraryFilter = id; model.reconcileLibrarySelection() }
    }
    private func dragProvider(kind: LibraryDragKind, id: String) -> NSItemProvider {
        return dragSession.provider(for: model.libraryDragPayload(kind: kind, id: id))
    }

    private func dropDelegate(_ target: LibraryDropTarget, key: String) -> VemoLibraryDropDelegate {
        VemoLibraryDropDelegate(model: model, target: target, session: dragSession,
                                targeted: Binding(get: { dropTarget == key }, set: { if $0 { dropTarget = key } else if dropTarget == key { dropTarget = nil } }),
                                accepted: {
            if case .folder(let id) = target { collapsedFolderIDs.remove(id) }
        })
    }
    private func beginNewFolder(parentID: String?) {
        editingFolderID = nil; folderName = ""; folderParentID = parentID ?? ""; folderEditor = true
    }
    private func beginEditing(_ folder: LibraryFolder) {
        editingFolderID = folder.id; folderName = folder.name; folderParentID = folder.parentID ?? ""; folderEditor = true
    }
    private func nativeInput(label: String, target: LibraryDropTarget? = nil, key: String,
                             select: (() -> Void)? = nil, folderID: String? = nil) -> VemoLibraryItemInteraction {
        VemoLibraryItemInteraction(label: label, selected: model.libraryFilter == key, suppressFocusRing: true,
            recordingCount: key == "root" ? nil : model.folderCount(key), select: select,
            dragData: folderID.map { id in { () -> Data? in
                let payload = model.libraryDragPayload(kind: .folder, id: id)
                _ = dragSession.provider(for: payload)
                return try? JSONEncoder().encode(payload)
            } },
            dragEnded: { data in
                if let value = try? JSONDecoder().decode(LibraryDragPayload.self, from: data) { dragSession.finish(ifCurrent: value) }
            },
            canDrop: target.map { target in { data in
                guard let payload = dragSession.matchingPayload(data: data) else { return false }
                return model.canDropLibraryItem(payload, on: target)
            } },
            receiveDrop: target.map { target in { data in
                guard let payload = dragSession.matchingPayload(data: data), model.dropLibraryItem(payload, on: target) else { return false }
                if case .folder(let id) = target { collapsedFolderIDs.remove(id) }
                dragSession.finish(ifCurrent: payload)
                return true
            } },
            targeted: { value in if value { dropTarget = key } else if dropTarget == key { dropTarget = nil } })
    }
    private func folderRow(_ name: String, icon: String, filter: String) -> some View {
        VemoFolderRow(name: name, icon: icon, count: model.folderCount(filter), depth: 0,
                      selected: model.libraryFilter == filter, targeted: dropTarget == filter,
                      select: { selectFolder(filter) },
                      input: nativeInput(label: name, target: filter == "unfiled" ? .unfiled : nil,
                                         key: filter, select: { selectFolder(filter) }))
    }
    private var splitContent: some View {
        NavigationSplitView(columnVisibility: $columnVisibility) {
            foldersColumn
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 250)
        } content: {
            recordingsColumn
                .toolbar(removing: .sidebarToggle)
                .navigationSplitViewColumnWidth(min: 280, ideal: 310, max: 360)
        } detail: {
            recordingDetail
                .toolbar(removing: .sidebarToggle)
        }
        .navigationSplitViewStyle(.balanced)
    }
    private var foldersColumn: some View {
        VStack(spacing: 0) {
            folderNavigation
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            Divider().padding(.horizontal, 16)
            SettingsLink {
                HStack(spacing: 10) {
                    Image(systemName: "gearshape").frame(width: 18)
                    Text("설정")
                    Spacer(minLength: 0)
                }
                .frame(maxWidth: .infinity, minHeight: 24, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(VemoButtonStyle(emphasis: .quiet))
            .accessibilityLabel("설정")
            .help("설정 ⌘,")
            .padding(12)
        }
    }
    private var recordingsColumn: some View {
        VStack(spacing: 0) {
            recordingSearch
            HStack {
                Text(model.libraryTitle).font(.caption.weight(.semibold)).lineLimit(1).help(model.libraryTitle)
                Spacer()
                Text(model.selectedIDs.count > 1 ? "\(model.selectedIDs.count)개 선택됨" : "\(model.visibleJobs.count)")
                    .font(.caption.monospacedDigit())
            }.foregroundStyle(.secondary).padding(.horizontal, 17).padding(.bottom, 6)
            if model.visibleJobs.isEmpty { emptyRecordings } else { recordingsList }
            HStack {
                Spacer()
                Button { model.chooseFiles() } label: {
                    Image(systemName:"plus").font(.system(size:20,weight:.medium))
                        .foregroundStyle(.white).frame(width:42,height:42)
                        .background(VemoTheme.accent,in:Circle()).contentShape(Circle())
                }.buttonStyle(.plain).accessibilityLabel("오디오 가져오기").help("오디오 가져오기 ⌘O")
            }.padding(14)
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }
    private var recordingSearch: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("제목·전사·키워드 검색", text: $model.search)
                .textFieldStyle(.plain).accessibilityLabel("녹음 검색")
            if !model.search.isEmpty {
                Button { model.search = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("검색 지우기")
            }
        }.font(.callout).padding(9)
            .background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 9)).padding(12)
    }
    private var emptyRecordings: some View {
        VStack(spacing: 9) {
            Image(systemName: model.search.isEmpty ? "folder" : "magnifyingglass").font(.title2)
            Text(model.search.isEmpty ? "이 폴더는 비어 있습니다" : "검색 결과가 없습니다").font(.callout)
            if model.search.isEmpty {
                Text(model.libraryFilter == "trash" ? "원본 파일은 삭제되지 않습니다." : "녹음을 폴더로 드래그해 정리하세요.").font(.caption)
            }
        }.foregroundStyle(.secondary).padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
    }
    @ViewBuilder private var recordingDetail: some View {
            if let job = model.selected {
                if job.deleted_at != nil {
                    VStack(spacing:16) {
                        Image(systemName:"trash").font(.largeTitle).foregroundStyle(VemoTheme.ink)
                        Text(job.title).font(.title2).textSelection(.enabled)
                        Text(job.purge_pending == true ? "Vemo 로컬 파일 정리가 끝나지 않았습니다. 영구 삭제를 다시 시도하세요. iPhone·Drive 원본과 내보낸 ZIP은 유지됩니다." : "Vemo 휴지통에 보관 중입니다. 원본 오디오와 전사문·노트는 삭제되지 않습니다.")
                            .foregroundStyle(.secondary).multilineTextAlignment(.center)
                        Button("녹음 복원",systemImage:"arrow.uturn.backward") { model.requestRecordingAction("restore_recording",ids:model.recordingActionIDs(job.id)) }.disabled(job.purge_pending == true)
                            .buttonStyle(VemoButtonStyle(emphasis:.primary))
                        Button("영구 삭제…",role:.destructive) { model.requestRecordingAction("purge_recording",ids:model.recordingActionIDs(job.id)) }
                            .disabled(!model.canChangeRecordings || model.libraryBusy || !model.libraryReady)
                    }.padding(32).frame(maxWidth:.infinity,maxHeight:.infinity)
                } else { TranscriptView(model: model, job: job) }
            } else if model.libraryFilter == "trash" {
                ContentUnavailableView("휴지통이 비어 있습니다",systemImage:"trash",description:Text("Vemo에서 숨긴 녹음을 여기에서 복원할 수 있습니다. 원본 파일은 삭제되지 않습니다."))
            } else { onboarding }
    }
    private var recordingsList: some View {
                    List(model.visibleJobs, selection: Binding<Set<String>>(get:{ model.selectedIDs },set:{ model.setRecordingSelection($0) })) { job in
                        recordingRow(job).tag(job.id)
                            .listRowBackground(model.selectedIDs.contains(job.id) ? VemoTheme.accent.opacity(0.14) : Color.clear)
                            .background(VemoRecordingSelectionAppearance())
                            .background(VemoScrollAppearance())
                            .overlay { if !model.isEditingTitle(job,at:.sidebar) { recordingInput(job) } }
                            .contextMenu { RecordingFolderMenu(model: model, jobID: job.id) }
                    }.listStyle(.sidebar)
                        .scrollContentBackground(.hidden)
                        .onKeyPress(.return) {
                            guard model.editingTitleJobID == nil, model.selectedIDs.count == 1, let job = model.selected, job.deleted_at == nil else { return .ignored }
                            model.beginTitleEdit(job,at:.sidebar); return .handled
                        }
    }
    private func recordingInput(_ job: Job) -> VemoLibraryItemInteraction {
        VemoLibraryItemInteraction(label: job.title, selected: model.selectedIDs.contains(job.id), suppressFocusRing: true,
            select: { model.selectRecording(job.id) },
            selectWithModifiers: { model.selectRecording(job.id,modifiers:$0) },
            selectAll: { model.selectAllRecordings() },
            editTitle: job.deleted_at == nil ? { model.beginTitleEdit(job,at:.sidebar) } : nil,
            canEditTitle: { model.selectedIDs.count == 1 && model.selectedIDs.contains(job.id) },
            dragData: {
                guard job.deleted_at == nil else { return nil }
                let payload = model.beginRecordingDrag(job.id)
                _ = dragSession.provider(for: payload)
                return try? JSONEncoder().encode(payload)
            },
            dragEnded: { data in
                if let payload = try? JSONDecoder().decode(LibraryDragPayload.self, from: data) {
                    model.finishRecordingDrag(payload)
                    dragSession.finish(ifCurrent: payload)
                }
            })
    }

    private func recordingRow(_ job: Job) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            if model.isEditingTitle(job,at:.sidebar) {
                VemoInlineTitleEditor(text:$model.titleDraft,save:model.saveTitleEdit,cancel:model.cancelTitleEdit)
            } else {
                Text(job.title).font(.system(size:13,weight:.semibold)).lineLimit(2)
            }
            Text([job.recording_date ?? "녹음일 확인 필요", job.duration_s.map { String(format: "%d분 %02d초", Int($0)/60, Int($0)%60) } ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 5) { Circle().fill(job.status == "failed" ? Color.red : job.status == "completed" ? VemoTheme.accent.opacity(0.65) : Color.secondary).frame(width: 5, height: 5); Text(job.purge_pending == true ? "로컬 정리 재시도 필요" : job.statusLabel + (job.progress.isEmpty ? "" : " · " + job.progress)).font(.caption).foregroundStyle(.secondary) }
        }.padding(.vertical, 8).frame(maxWidth: .infinity, alignment: .leading).contentShape(Rectangle())
    }
    private func saveFolder() {
        guard !folderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !model.libraryBusy else { return }
        let parentID = folderParentID.isEmpty ? nil : folderParentID
        if let id = editingFolderID { model.updateLibraryFolder(id, name: folderName, parentID: parentID) } else { model.createLibraryFolder(folderName, parentID: parentID) }
        if let parentID { collapsedFolderIDs.remove(parentID) }
        folderEditor = false
    }
    var onboarding: some View {
        VStack(alignment: .leading, spacing: 20) {
            Image(systemName: "waveform").font(.system(size: 40)).foregroundStyle(VemoTheme.ink)
            Text("Vemo").font(.largeTitle.bold())
            Text("녹음은 이 Mac에서 전사합니다. 결과를 읽거나 음성과 함께 확인하세요.").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 14) {
                Label("1. 설정에서 입력 경로와 결과 폴더 확인", systemImage: "folder")
                Label(model.connection, systemImage: "link")
                Label(model.modelsReady ? "2. 로컬 전사 준비됨" : "2. 처음 사용할 때 모델 다운로드", systemImage: model.modelsReady ? "checkmark.circle" : "arrow.down.circle")
                Label("3. 파일을 가져와 녹음 시작일 확인", systemImage: "doc.badge.plus")
            }
            HStack { Button("파일 가져오기") { model.chooseFiles() }.buttonStyle(VemoButtonStyle(emphasis: .primary)); if !model.modelsReady { Button("모델 다운로드") { model.command("download_models") } }; SettingsLink { Text("설정 열기") } }
            if model.connection == "엔진 연결 필요" { Button("엔진 다시 연결") { model.connect() } }
            Text("최초 모델 다운로드와 Apple iCloud 동기화에는 네트워크를 사용합니다. 전사는 로컬에서 처리합니다.").font(.caption).foregroundStyle(.secondary)
        }.padding(40).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
    }
}

struct RecordingFolderMenu: View {
    @ObservedObject var model: AppModel
    let jobID: String
    var titleLocation: TitleEditingLocation = .sidebar
    var body: some View {
        if model.recordingActionIDs(jobID).count > 1 { Text("선택한 \(model.recordingActionIDs(jobID).count)개 녹음") }
        if let job = model.jobs.first(where: { $0.id == jobID }) {
            if job.deleted_at != nil {
                Button("녹음 복원",systemImage:"arrow.uturn.backward") { model.requestRecordingAction("restore_recording",ids:model.recordingActionIDs(job.id)) }.disabled(job.purge_pending == true)
                Button("영구 삭제…",role:.destructive) { model.requestRecordingAction("purge_recording",ids:model.recordingActionIDs(job.id)) }
            } else {
                Button("제목 수정") { model.beginTitleEdit(job,at:titleLocation) }
                Button("원본 다시 확인") { model.refreshRecording(job) }
                Button("Vemo 휴지통으로 이동",systemImage:"trash") { model.requestRecordingAction("trash_recording",ids:model.recordingActionIDs(job.id)) }
            }
        }
        Menu("폴더로 이동") {
            Button { model.moveRecordings(model.recordingActionIDs(jobID), to: nil) } label: { Label("미분류", systemImage: model.library.assignments[jobID] == nil ? "checkmark" : "tray") }
            ForEach(LibraryFolderHierarchy.rows(model.library.folders), id: \.id) { row in
                Button { model.moveRecordings(model.recordingActionIDs(jobID), to: row.id) } label: { Label(LibraryFolderHierarchy.path(for: row.id, in: model.library.folders), systemImage: model.library.assignments[jobID] == row.id ? "checkmark" : "folder") }
            }
        }.disabled(!model.libraryReady || model.libraryBusy || !model.activeJobs.contains(where: { $0.id == jobID }))
    }
}

/// Disable AppKit's active/inactive selection paint only in the recording list.
/// Selection binding, keyboard handling and the native drag surface stay intact.
private struct VemoRecordingSelectionAppearance: NSViewRepresentable {
    func makeNSView(context: Context) -> Surface { Surface() }
    func updateNSView(_ view: Surface, context: Context) { view.configure() }
    final class Surface: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
        override func viewDidMoveToSuperview() { super.viewDidMoveToSuperview(); configure() }
        override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); configure() }
        func configure() {
            var ancestor = superview
            while let view = ancestor {
                if let table = view as? NSTableView {
                    table.selectionHighlightStyle = .none
                    table.focusRingType = .none
                    return
                }
                ancestor = view.superview
            }
        }
    }
}

/// Request focus after attachment and the native row's responder teardown have finished.
struct VemoInlineTitleEditor: NSViewRepresentable {
    @Binding var text: String
    var font: NSFont = .systemFont(ofSize:13,weight:.semibold)
    let save: () -> Void
    let cancel: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeNSView(context: Context) -> Field {
        let field = Field()
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.isEditable = true
        field.isSelectable = true
        field.usesSingleLineMode = true
        field.cell?.isScrollable = true
        field.delegate = context.coordinator
        field.setContentCompressionResistancePriority(.defaultLow,for:.horizontal)
        field.setAccessibilityLabel("녹음 제목 수정")
        field.toolTip = "Enter로 저장 · Escape로 취소"
        return field
    }
    func updateNSView(_ field: Field, context: Context) {
        context.coordinator.parent = self
        field.font = font
        if field.stringValue != text { field.stringValue = text }
    }
    final class Field: NSTextField {
        private var requestedFocus = false
        override var intrinsicContentSize: NSSize {
            NSSize(width:NSView.noIntrinsicMetric,height:super.intrinsicContentSize.height)
        }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window, !requestedFocus else { return }
            requestedFocus = true
            DispatchQueue.main.async { [weak self, weak window] in
                guard let self, let window, self.window === window else { return }
                if window.makeFirstResponder(self), let editor = self.currentEditor() {
                    editor.selectAll(nil)
                }
            }
        }
    }
    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: VemoInlineTitleEditor
        init(_ parent: VemoInlineTitleEditor) { self.parent = parent }
        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            parent.text = field.stringValue
        }
        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            if selector == #selector(NSResponder.insertNewline(_:)) {
                parent.text = textView.string
                parent.save()
                return true
            }
            if selector == #selector(NSResponder.cancelOperation(_:)) {
                parent.cancel()
                return true
            }
            return false
        }
    }
}
