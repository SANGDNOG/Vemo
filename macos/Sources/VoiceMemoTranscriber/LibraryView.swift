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
        NavigationSplitView(columnVisibility: $columnVisibility) {
            VStack(spacing: 0) {
                folderNavigation
                Divider().padding(.horizontal, 14)
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("제목·전사·키워드 검색", text: $model.search).textFieldStyle(.plain).accessibilityLabel("녹음 검색")
                    if !model.search.isEmpty { Button { model.search = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("검색 지우기") }
                }.font(.callout).padding(9).background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 9)).padding(12)
                HStack { Text(model.libraryTitle).font(.caption.weight(.semibold)).lineLimit(1).help(model.libraryTitle); Spacer(); Text("\(model.visibleJobs.count)").font(.caption.monospacedDigit()) }.foregroundStyle(.secondary).padding(.horizontal, 17).padding(.bottom, 6)
                if model.visibleJobs.isEmpty {
                    VStack(spacing: 9) { Image(systemName: model.search.isEmpty ? "folder" : "magnifyingglass").font(.title2); Text(model.search.isEmpty ? "이 폴더는 비어 있습니다" : "검색 결과가 없습니다").font(.callout); if model.search.isEmpty { Text("녹음을 폴더로 드래그해 정리하세요.").font(.caption) } }.foregroundStyle(.secondary).padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(model.visibleJobs, selection: $model.selection) { job in
                        recordingRow(job).tag(job.id)
                            .background(VemoScrollAppearance())
                            .overlay(recordingInput(job))
                            .contextMenu { RecordingFolderMenu(model: model, jobID: job.id) }
                    }.listStyle(.sidebar)
                        .scrollContentBackground(.hidden)
                }
            }.frame(minWidth: 250, idealWidth: 290)
                .navigationSplitViewColumnWidth(min: 250, ideal: 290, max: 420)
                .toolbar(removing: .sidebarToggle)
                .toolbar {
                    if columnVisibility != .detailOnly {
                        if #available(macOS 26.0, *) {
                            ToolbarSpacer(.flexible, placement: .primaryAction)
                        }
                        ToolbarItem(placement: .primaryAction) { sidebarToggle }
                    }
                }
        } detail: {
            if let job = model.selected { TranscriptView(model: model, job: job) }
            else { onboarding }
        }
        .toolbar(removing: .sidebarToggle)
        // Let the full-height split panes draw one continuous column boundary.
        .toolbarBackground(.hidden, for: .windowToolbar)
        .toolbar {
            ToolbarItem(placement: .navigation) {
                if columnVisibility == .detailOnly { sidebarToggle }
            }
            ToolbarItem(placement: .principal) { Text(model.overallStatus).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            ToolbarItem { if model.importing { ProgressView().controlSize(.small).help("오디오를 안전하게 가져오는 중") } }
            ToolbarItem { Button { model.chooseFiles() } label: { Label("파일 가져오기", systemImage: "square.and.arrow.down") }.help("파일 가져오기 ⌘O") }
            ToolbarItem { SettingsLink { Label("설정", systemImage: "gearshape") } }
        }
        .onChange(of: model.selection) { _, _ in Task { await model.loadSelection() } }
        .onChange(of: model.visibleJobs.map(\.id)) { _, _ in model.reconcileLibrarySelection() }
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            for provider in providers { _ = provider.loadObject(ofClass: URL.self) { url, _ in if let url { Task { @MainActor in model.importFiles([url]) } } } }; return !providers.isEmpty
        }
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

    private var sidebarToggle: some View {
        Button {
            withAnimation(VemoTheme.motion(reducedMotion)) {
                columnVisibility = columnVisibility == .detailOnly ? .all : .detailOnly
            }
        } label: {
            Image(systemName: "sidebar.left")
        }
        .accessibilityLabel(columnVisibility == .detailOnly ? "사이드바 보기" : "사이드바 가리기")
        .help(columnVisibility == .detailOnly ? "사이드바 보기" : "사이드바 가리기")
        .keyboardShortcut("s", modifiers: [.command, .control])
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
                    }.frame(maxHeight: min(CGFloat(folderTreeRows.count) * 37, 200))
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
        VemoLibraryItemInteraction(label: label, selected: model.libraryFilter == key,
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
    private func recordingInput(_ job: Job) -> VemoLibraryItemInteraction {
        VemoLibraryItemInteraction(label: job.title, selected: model.selection == job.id,
            select: { model.selection = job.id },
            dragData: {
                let payload = model.libraryDragPayload(kind: .recording, id: job.id)
                _ = dragSession.provider(for: payload)
                return try? JSONEncoder().encode(payload)
            },
            dragEnded: { data in
                if let payload = try? JSONDecoder().decode(LibraryDragPayload.self, from: data) {
                    dragSession.finish(ifCurrent: payload)
                }
            })
    }

    private func recordingRow(_ job: Job) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Text(job.title).font(.system(size: 13, weight: .semibold)).lineLimit(2)
            Text([job.recording_date ?? "녹음일 확인 필요", job.duration_s.map { String(format: "%d분 %02d초", Int($0)/60, Int($0)%60) } ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 5) { Circle().fill(job.status == "failed" ? Color.red : job.status == "completed" ? VemoTheme.accent.opacity(0.65) : Color.secondary).frame(width: 5, height: 5); Text(job.statusLabel + (job.progress.isEmpty ? "" : " · " + job.progress)).font(.caption).foregroundStyle(.secondary) }
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
    var body: some View {
        Menu("폴더로 이동") {
            Button { model.moveRecording(jobID, to: nil) } label: { Label("미분류", systemImage: model.library.assignments[jobID] == nil ? "checkmark" : "tray") }
            ForEach(LibraryFolderHierarchy.rows(model.library.folders), id: \.id) { row in
                Button { model.moveRecording(jobID, to: row.id) } label: { Label(LibraryFolderHierarchy.path(for: row.id, in: model.library.folders), systemImage: model.library.assignments[jobID] == row.id ? "checkmark" : "folder") }
            }
        }.disabled(!model.libraryReady || model.libraryBusy)
    }
}
