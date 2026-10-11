import SwiftUI
import NativeCore

struct LibraryView: View {
    @ObservedObject var model: AppModel
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    @ViewState private var foldersExpanded = true
    @ViewState private var folderEditor = false
    @ViewState private var editingFolderID: String?
    @ViewState private var folderName = ""
    @ViewState private var deletingFolder: LibraryFolder?

    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                folderNavigation
                Divider().padding(.horizontal, 14)
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("제목·전사·키워드 검색", text: $model.search).textFieldStyle(.plain).accessibilityLabel("녹음 검색")
                    if !model.search.isEmpty { Button { model.search = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("검색 지우기") }
                }.font(.callout).padding(9).background(Color.primary.opacity(0.045), in: RoundedRectangle(cornerRadius: 9)).padding(12)
                HStack { Text(model.libraryTitle).font(.caption.weight(.semibold)); Spacer(); Text("\(model.visibleJobs.count)").font(.caption.monospacedDigit()) }.foregroundStyle(.secondary).padding(.horizontal, 17).padding(.bottom, 6)
                if model.visibleJobs.isEmpty {
                    VStack(spacing: 9) { Image(systemName: model.search.isEmpty ? "folder" : "magnifyingglass").font(.title2); Text(model.search.isEmpty ? "이 폴더는 비어 있습니다" : "검색 결과가 없습니다").font(.callout); if model.search.isEmpty { Text("녹음의 메뉴에서 폴더로 이동하세요.").font(.caption) } }.foregroundStyle(.secondary).padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(model.visibleJobs, selection: $model.selection) { job in
                        recordingRow(job).tag(job.id)
                            .contextMenu { RecordingFolderMenu(model: model, jobID: job.id) }
                    }.listStyle(.sidebar)
                }
            }.navigationSplitViewColumnWidth(min: 250, ideal: 290, max: 420)
        } detail: {
            if let job = model.selected { TranscriptView(model: model, job: job) }
            else { onboarding }
        }
        .toolbar {
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
            VStack(alignment: .leading, spacing: 18) {
                Text(editingFolderID == nil ? "새 폴더" : "폴더 이름 변경").font(.title3.weight(.semibold))
                TextField("폴더 이름", text: $folderName).textFieldStyle(.roundedBorder).onSubmit { saveFolder() }
                HStack { Spacer(); Button("취소") { folderEditor = false }.keyboardShortcut(.cancelAction); Button(editingFolderID == nil ? "만들기" : "저장") { saveFolder() }.buttonStyle(VemoButtonStyle(emphasis: .primary)).keyboardShortcut(.defaultAction).disabled(folderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || model.libraryBusy) }
            }.padding(24).frame(width: 340).tint(VemoTheme.accent).buttonStyle(VemoButtonStyle())
        }
        .confirmationDialog("폴더를 삭제할까요?", isPresented: Binding(get: { deletingFolder != nil }, set: { if !$0 { deletingFolder = nil } }), titleVisibility: .visible) {
            if let folder = deletingFolder { Button("폴더 삭제", role: .destructive) { model.deleteLibraryFolder(folder.id); deletingFolder = nil }; Button("취소", role: .cancel) { deletingFolder = nil } }
        } message: { Text("녹음은 미분류로 돌아갑니다. 녹음 파일, 전사문과 노트는 보존됩니다.") }
        .safeAreaInset(edge: .bottom) {
            if let problem = model.problem {
                HStack(alignment: .top) { Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange); Text(problem).font(.callout).textSelection(.enabled); Spacer(); Button("닫기") { model.problem = nil } }.padding(12).background(.thinMaterial)
            }
        }
    }

    private var folderNavigation: some View {
        VStack(spacing: 3) {
            HStack {
                Button { withAnimation(VemoTheme.motion(reducedMotion)) { foldersExpanded.toggle() } } label: {
                    HStack(spacing: 6) { Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold)).rotationEffect(.degrees(foldersExpanded ? 90 : 0)); Text("라이브러리").font(.caption.weight(.semibold)) }.foregroundStyle(.secondary)
                }.buttonStyle(.plain).accessibilityLabel(foldersExpanded ? "폴더 접기" : "폴더 펼치기")
                Spacer()
                Menu {
                    Button("폴더 이름 변경…") {
                        if let folder = model.library.folders.first(where: { $0.id == model.libraryFilter }) { editingFolderID = folder.id; folderName = folder.name; folderEditor = true }
                    }
                    Button("폴더 삭제…", role: .destructive) { deletingFolder = model.library.folders.first { $0.id == model.libraryFilter } }
                } label: { Image(systemName: "ellipsis").font(.system(size: 12)) }
                    .menuStyle(.borderlessButton).fixedSize().padding(5).foregroundStyle(.secondary)
                    .accessibilityLabel("선택 폴더 관리").help("선택 폴더 관리")
                    .disabled(!model.libraryReady || model.libraryBusy || !model.library.folders.contains(where: { $0.id == model.libraryFilter }))
                Button { editingFolderID = nil; folderName = ""; folderEditor = true } label: { Image(systemName: "plus").font(.system(size: 12, weight: .medium)) }.buttonStyle(VemoButtonStyle(emphasis: .quiet)).accessibilityLabel("새 폴더").help("새 폴더").disabled(!model.libraryReady || model.libraryBusy)
            }.padding(.horizontal, 5)
            if foldersExpanded {
                folderRow("전체 녹음", icon: "square.stack", filter: "all")
                folderRow("미분류", icon: "tray", filter: "unfiled")
                if !model.library.folders.isEmpty {
                    ScrollView {
                        VStack(spacing: 3) {
                            ForEach(model.library.folders, id: \.id) { folder in
                                folderRow(folder.name, icon: "folder", filter: folder.id)
                                    .contextMenu {
                                        Button("이름 변경") { editingFolderID = folder.id; folderName = folder.name; folderEditor = true }.disabled(model.libraryBusy)
                                        Button("폴더 삭제…", role: .destructive) { deletingFolder = folder }.disabled(model.libraryBusy)
                                    }
                            }
                        }
                    }.frame(maxHeight: min(CGFloat(model.library.folders.count) * 33, 140))
                }
            }
        }.padding(.horizontal, 12).padding(.top, 8).padding(.bottom, 12)
    }
    private func folderRow(_ name: String, icon: String, filter: String) -> some View {
        let active = model.libraryFilter == filter
        return Button { withAnimation(VemoTheme.motion(reducedMotion)) { model.libraryFilter = filter; model.reconcileLibrarySelection() } } label: {
            HStack(spacing: 9) { Image(systemName: icon).frame(width: 16); Text(name).lineLimit(1); Spacer(minLength: 4); Text("\(model.folderCount(filter))").font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
                .font(.system(size: 12, weight: active ? .semibold : .regular))
                .foregroundStyle(active ? VemoTheme.ink : Color.primary)
                .padding(.horizontal, 10).padding(.vertical, 7)
                .background(active ? VemoTheme.accent.opacity(0.12) : .clear, in: RoundedRectangle(cornerRadius: 8))
        }.buttonStyle(.plain).accessibilityAddTraits(active ? .isSelected : []).accessibilityLabel("\(name), \(model.folderCount(filter))개 녹음")
    }
    private func recordingRow(_ job: Job) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Text(job.title).font(.system(size: 13, weight: .semibold)).lineLimit(2)
            Text([job.recording_date ?? "녹음일 확인 필요", job.duration_s.map { String(format: "%d분 %02d초", Int($0)/60, Int($0)%60) } ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 5) { Circle().fill(job.status == "failed" ? Color.red : job.status == "completed" ? VemoTheme.accent.opacity(0.65) : Color.secondary).frame(width: 5, height: 5); Text(job.statusLabel + (job.progress.isEmpty ? "" : " · " + job.progress)).font(.caption).foregroundStyle(.secondary) }
        }.padding(.vertical, 8)
    }
    private func saveFolder() {
        guard !folderName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !model.libraryBusy else { return }
        if let id = editingFolderID { model.renameLibraryFolder(id, name: folderName) } else { model.createLibraryFolder(folderName) }
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
            ForEach(model.library.folders, id: \.id) { folder in
                Button { model.moveRecording(jobID, to: folder.id) } label: { Label(folder.name, systemImage: model.library.assignments[jobID] == folder.id ? "checkmark" : "folder") }
            }
        }.disabled(!model.libraryReady || model.libraryBusy)
    }
}
