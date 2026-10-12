import SwiftUI
struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ViewState private var confirmBackfill = false
    var body: some View {
        Form {
            Section("연결") {
                Picker("입력", selection: $model.sourceMode) { Text("음성 메모 직접 읽기").tag("voice_memos"); Text("Drive · 파일 폴더").tag("folder") }
                    .onChange(of: model.sourceMode) { _, value in UserDefaults.standard.set(value, forKey: "sourceMode"); model.scanSources(force:true) }
                if model.sourceMode == "voice_memos" {
                    Text("직접 읽기에는 앱의 전체 디스크 접근 권한과 지원되는 저장소 형식이 필요합니다. 원본은 읽기 전용입니다.").font(.caption).foregroundStyle(.secondary)
                    Button("전체 디스크 접근 설정 열기") { model.openPermissions() }
                    Button("접근 상태 확인") { model.checkPermissions() }
                } else {
                    HStack { Text(model.inputFolder.isEmpty ? "입력 폴더를 선택하세요" : model.inputFolder).lineLimit(2); Spacer(); Button("선택") { model.chooseFolder(output: false) } }
                    Text("iPhone 음성 메모 → 공유 → Drive의 입력 폴더에 저장하세요. Mac에서는 해당 폴더를 동기화하고 오프라인 사용을 켭니다. 녹음일이 없으면 직접 확인합니다.").font(.caption).foregroundStyle(.secondary)
                }
                HStack { Text(model.outputFolder.isEmpty ? "결과 폴더를 선택하세요" : model.outputFolder).lineLimit(2); Spacer(); Button("결과 폴더 선택") { model.chooseFolder(output: true) } }
                if let problem = model.sourceDiagnostic ?? model.problem { Text(problem).font(.callout).foregroundStyle(.orange).textSelection(.enabled) }
                Text("iCloud Drive · Google Drive · Dropbox · OneDrive의 Mac 동기화 폴더를 선택할 수 있습니다. 같은 Drive 안에 Recordings(입력)와 Notes(결과)를 따로 두세요. 결과는 로컬 폴더에 저장하며 폰으로의 업로드는 Drive 앱이 진행합니다.").font(.caption).foregroundStyle(.secondary)
                HStack { Text(model.connection); Spacer(); Button("다시 연결") { model.connect() } }
            }
            Section("ZIP 내보내기") {
                HStack {
                    VStack(alignment:.leading,spacing:4) {
                        Text("ZIP 저장 위치")
                        Text(model.zipExportFolder.isEmpty ? "선택하지 않음" : model.zipExportFolder).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                    Spacer()
                    Button("폴더 선택") { model.chooseZIPFolder() }.disabled(model.zipExportBusy)
                        .accessibilityLabel("ZIP 저장 위치 폴더 선택")
                }
                Button("완료된 녹음 ZIP 내보내기") { model.exportCompletedZIP() }
                    .disabled(model.zipExportBusy || model.completedZIPCount == 0 || model.zipExportFolder.isEmpty)
                Text("완료된 녹음 \(model.completedZIPCount)개의 원본 오디오, TXT · Markdown 전사문, 구간 JSON을 하나의 ZIP으로 저장합니다. Nextcloud 등 클라우드 데스크톱 앱이 선택한 폴더를 동기화합니다.")
                    .font(.caption).foregroundStyle(.secondary)
                if model.zipExportBusy {
                    HStack { ProgressView().controlSize(.small); Text(model.zipExportProgress).font(.caption) }
                } else if !model.zipExportProgress.isEmpty {
                    Text(model.zipExportProgress).font(.caption)
                }
                if let error = model.zipExportError { Text(error).font(.caption).foregroundStyle(.orange).textSelection(.enabled) }
                if let result = model.zipExportResult {
                    Text(result.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    Button("ZIP 파일 위치 보기") { NSWorkspace.shared.activateFileViewerSelecting([result]) }
                }
            }
            Section("모델") {
                Text(model.modelStatus)
                Text("ASR: Qwen3-ASR-1.7B · 정렬: Qwen3-ForcedAligner-0.6B\n제목: Qwen3-4B-Instruct-2507-4bit").font(.caption).foregroundStyle(.secondary)
                Button("모델 다운로드 / 재개") { model.command("download_models") }
                Text("최초 다운로드에 네트워크와 저장 공간이 필요합니다. 처리할 때는 로컬 모델을 사용합니다.").font(.caption).foregroundStyle(.secondary)
            }
            Section("상세 노트 모델 · 선택 다운로드") {
                Text(model.notesModelStatus)
                Text("Qwen3.5-27B-4bit · 다운로드 약 16 GB · 메모리 24 GB 이상 권장").font(.caption).foregroundStyle(.secondary)
                Button("상세 노트 모델 다운로드 / 재개") { model.downloadNotesModel() }
                Text("노트 생성에만 사용합니다. 전사 · 시간 정렬 · 제목 생성에는 필요하지 않습니다. 다운로드 후 로컬에서 처리합니다.").font(.caption).foregroundStyle(.secondary)
            }
            Section("동작") {
                Toggle("전원 연결 시에만 처리", isOn: $model.acOnly).onChange(of: model.acOnly) { _, value in model.setPolicy(value) }
                Toggle("로그인 시 앱 실행", isOn: Binding(get: { model.loginEnabled }, set: { model.setLogin($0) }))
                Button(model.paused ? "처리 재개" : "새 작업 일시 중지") { model.command(model.paused ? "resume" : "pause") }
                Button("과거 미처리 녹음 모두 전사…") { confirmBackfill = true }.disabled(model.jobs.allSatisfy { $0.status != "unprocessed" })
                Text("창을 닫아도 메뉴 막대에서 계속 처리합니다. 최초 과거 녹음은 자동으로 전사하지 않습니다.").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).padding().frame(width: 580, height: 650)
        .confirmationDialog("과거 미처리 녹음을 모두 전사할까요?", isPresented: $confirmBackfill) { Button(model.manualProcessingTitle("모두 전사")) { model.processingCommand("backfill", ["uuids": .array(model.jobs.filter { $0.status == "unprocessed" }.map { .string($0.uuid) })]) } } message: { Text(model.paused ? "대기 중인 작업도 함께 재개합니다. 로컬 처리에 시간이 걸릴 수 있습니다." : "로컬 처리에 시간이 걸릴 수 있습니다.") }
    }
}
