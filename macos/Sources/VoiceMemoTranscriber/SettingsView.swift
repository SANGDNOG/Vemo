import SwiftUI
struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ViewState private var confirmBackfill = false
    var body: some View {
        Form {
            Section("연결") {
                Picker("입력", selection: $model.sourceMode) { Text("음성 메모 직접 읽기").tag("voice_memos"); Text("공유한 파일 폴더").tag("folder") }
                    .onChange(of: model.sourceMode) { _, value in UserDefaults.standard.set(value, forKey: "sourceMode"); model.scanSources(force:true) }
                if model.sourceMode == "voice_memos" {
                    Text("직접 읽기에는 앱의 전체 디스크 접근 권한과 지원되는 저장소 형식이 필요합니다. 원본은 읽기 전용입니다.").font(.caption).foregroundStyle(.secondary)
                    Button("전체 디스크 접근 설정 열기") { model.openPermissions() }
                    Button("접근 상태 확인") { model.checkPermissions() }
                } else {
                    HStack { Text(model.inputFolder.isEmpty ? "입력 폴더를 선택하세요" : model.inputFolder).lineLimit(2); Spacer(); Button("선택") { model.chooseFolder(output: false) } }
                    Text("폰에서 공유 → 파일에 저장한 녹음을 가져옵니다. 녹음일을 모르면 확인이 필요합니다.").font(.caption).foregroundStyle(.secondary)
                }
                HStack { Text(model.outputFolder.isEmpty ? "결과 폴더를 선택하세요" : model.outputFolder).lineLimit(2); Spacer(); Button("결과 폴더 선택") { model.chooseFolder(output: true) } }
                if let problem = model.sourceDiagnostic ?? model.problem { Text(problem).font(.callout).foregroundStyle(.orange).textSelection(.enabled) }
                Text("iCloud Drive의 Voice Transcripts 폴더를 선택하면 파일 앱에서 읽을 수 있습니다. 폰 도착은 별도 확인이 필요합니다.").font(.caption).foregroundStyle(.secondary)
                HStack { Text(model.connection); Spacer(); Button("다시 연결") { model.connect() } }
            }
            Section("모델") {
                Text(model.modelStatus)
                Text("ASR: Qwen3-ASR-1.7B · 정렬: Qwen3-ForcedAligner-0.6B\n제목: Qwen3-4B-Instruct-2507-4bit").font(.caption).foregroundStyle(.secondary)
                Button("모델 다운로드 / 재개") { model.command("download_models") }
                Text("최초 다운로드에 네트워크와 저장 공간이 필요합니다. 처리할 때는 로컬 모델을 사용합니다.").font(.caption).foregroundStyle(.secondary)
            }
            Section("동작") {
                Toggle("전원 연결 시에만 처리", isOn: $model.acOnly).onChange(of: model.acOnly) { _, value in model.setPolicy(value) }
                Toggle("로그인 시 앱 실행", isOn: Binding(get: { model.loginEnabled }, set: { model.setLogin($0) }))
                Button(model.paused ? "처리 재개" : "새 작업 일시 중지") { model.command(model.paused ? "resume" : "pause") }
                Button("과거 미처리 녹음 모두 전사…") { confirmBackfill = true }.disabled(model.jobs.allSatisfy { $0.status != "unprocessed" })
                Text("창을 닫아도 메뉴 막대에서 계속 처리합니다. 최초 과거 녹음은 자동으로 전사하지 않습니다.").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).padding().frame(width: 580, height: 650)
        .confirmationDialog("과거 미처리 녹음을 모두 전사할까요?", isPresented: $confirmBackfill) { Button("모두 전사") { model.command("backfill", ["uuids": .array(model.jobs.filter { $0.status == "unprocessed" }.map { .string($0.uuid) })]) } } message: { Text("로컬 처리에 시간이 걸릴 수 있습니다.") }
    }
}
