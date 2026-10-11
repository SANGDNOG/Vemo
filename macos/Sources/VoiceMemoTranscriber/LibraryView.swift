import SwiftUI
import NativeCore
struct LibraryView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                TextField("제목·전사·키워드 검색", text: $model.search).textFieldStyle(.roundedBorder).padding(12).accessibilityLabel("녹음 검색")
                List(model.visibleJobs, selection: $model.selection) { job in
                    VStack(alignment: .leading, spacing: 5) {
                        Text(job.title).font(.headline).lineLimit(2)
                        Text([job.recording_date ?? "녹음일 확인 필요", job.duration_s.map { String(format: "%d분 %02d초", Int($0)/60, Int($0)%60) } ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                        Text(job.statusLabel + (job.progress.isEmpty ? "" : " · " + job.progress)).font(.caption).foregroundStyle(job.status == "failed" ? .red : .secondary)
                    }.padding(.vertical, 5).tag(job.id)
                }.listStyle(.sidebar)
            }.navigationSplitViewColumnWidth(min: 220, ideal: 280, max: 420)
        } detail: {
            if let job = model.selected { TranscriptView(model: model, job: job) }
            else { onboarding }
        }
        .toolbar {
            ToolbarItem(placement: .principal) { Text(model.overallStatus).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            ToolbarItem { if model.importing { ProgressView().controlSize(.small).help("오디오를 안전하게 가져오는 중") } }
            ToolbarItem { Button { model.chooseFiles() } label: { Label("파일 가져오기", systemImage: "square.and.arrow.down") } }
            ToolbarItem { SettingsLink { Label("설정", systemImage: "gearshape") } }
        }
        .onChange(of: model.selection) { _, _ in Task { await model.loadSelection() } }
        .onDrop(of: [.fileURL], isTargeted: nil) { providers in
            for provider in providers { _ = provider.loadObject(ofClass: URL.self) { url, _ in if let url { Task { @MainActor in model.importFiles([url]) } } } }; return !providers.isEmpty
        }
        .safeAreaInset(edge: .bottom) {
            if let problem = model.problem {
                HStack(alignment: .top) { Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange); Text(problem).font(.callout).textSelection(.enabled); Spacer(); Button("닫기") { model.problem = nil } }.padding(12).background(.thinMaterial)
            }
        }
    }
    var onboarding: some View {
        VStack(alignment: .leading, spacing: 20) {
            Image(systemName: "waveform").font(.system(size: 40)).foregroundStyle(.tint)
            Text("음성 기록").font(.largeTitle.bold())
            Text("녹음은 이 Mac에서 전사합니다. 결과를 읽거나 음성과 함께 확인하세요.").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 14) {
                Label("1. 설정에서 입력 경로와 결과 폴더 확인", systemImage: "folder")
                Label(model.connection, systemImage: "link")
                Label(model.modelsReady ? "2. 로컬 전사 준비됨" : "2. 처음 사용할 때 모델 다운로드", systemImage: model.modelsReady ? "checkmark.circle" : "arrow.down.circle")
                Label("3. 파일을 가져와 녹음 시작일 확인", systemImage: "doc.badge.plus")
            }
            HStack { Button("파일 가져오기") { model.chooseFiles() }; if !model.modelsReady { Button("모델 다운로드") { model.command("download_models") } }; SettingsLink { Text("설정 열기") } }
            if model.connection == "엔진 연결 필요" { Button("엔진 다시 연결") { model.connect() } }
            Text("최초 모델 다운로드와 Apple iCloud 동기화에는 네트워크를 사용합니다. 전사는 로컬에서 처리합니다.").font(.caption).foregroundStyle(.secondary)
        }.padding(40).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
    }
}
