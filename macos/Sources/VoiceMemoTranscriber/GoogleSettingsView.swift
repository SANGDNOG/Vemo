import SwiftUI

struct GoogleSettingsView: View {
    @ObservedObject var model: GoogleConnectionModel
    var onSync: () -> Void = {}
    @ViewState private var confirmDisconnect = false

    var body: some View {
        Section("Galaxy · Google Drive 연결") {
            Text("Galaxy에서 녹음 → Google Drive → 이 Mac에서 전사·노트 → Galaxy에서 결과 확인")
                .font(.callout)
            Text("Mac과 Galaxy에서 같은 Google 계정으로 연결하고, Galaxy에서는 아래 표시된 이 Mac의 전용 폴더와 ‘PC 자동 처리’ 모드를 선택하세요. Mac용 Google Drive 앱 설치는 필요하지 않습니다.")
                .font(.caption).foregroundStyle(.secondary)
            if let name = model.boundRootDisplayName {
                Text("연결된 Google Drive 폴더: \(name)")
                    .font(.callout).textSelection(.enabled)
            }
            HStack {
                Button(model.canCompleteFolderConnection ? "폴더 연결 완료" : (model.isConnected ? "같은 계정으로 다시 승인" : "Google로 연결")) { model.connect() }
                    .disabled(model.isBusy || model.needsKeychainRecovery)
                if model.canCompleteFolderConnection {
                    Button("같은 계정으로 다시 승인") { model.connect(forceReauthorization: true) }
                        .disabled(model.isBusy || model.needsKeychainRecovery)
                }
                Button("지금 확인", action: onSync)
                    .disabled(model.isBusy || !model.isConnected)
                if model.canDisconnect {
                    Button("이 Mac 연결 해제…") { confirmDisconnect = true }.disabled(model.isBusy)
                }
                if model.isBusy {
                    ProgressView().controlSize(.small)
                    Button("취소") { model.cancel() }
                }
            }
            DisclosureGroup("고급 · 사용자 지정 OAuth 설정") {
                Text("별도의 Desktop OAuth 클라이언트를 사용할 때만 JSON을 선택하세요. 기존 계정이나 폴더 연결은 자동으로 바꾸지 않습니다.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("OAuth JSON 선택…") { model.selectClientJSON() }
                    .disabled(!model.canSelectClient)
            }
            if model.needsKeychainRecovery {
                Button("Keychain 접근 복구…") { model.recoverKeychainAccess() }.disabled(model.isBusy)
                Text("macOS의 기존 토큰 접근 승인입니다. Google 권한 재동의나 새 폴더 생성은 하지 않습니다.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(model.status).font(.caption).foregroundStyle(.secondary)
                .accessibilityLabel("Google 연결 상태")
                .accessibilityValue(model.status)
            Text("macOS 기본 브라우저에 Google 승인 페이지 열기를 요청합니다. 권한은 Vemo가 사용하는 파일의 drive.file로 제한합니다.")
                .font(.caption).foregroundStyle(.secondary)
            Text("이 Mac의 처리기가 연결되어 있으면 완료 녹음과 결과를 30초마다 확인합니다. 처리 일시정지를 해제하고 전사·노트 모델을 준비하세요. Mac이 잠자거나 꺼져 있으면 기기 원본을 유지하며 다음 연결을 기다립니다. 실제 계정의 AI 왕복 검증은 진행 중입니다.")
                .font(.caption).foregroundStyle(.secondary)
            if model.hasBinding {
                Text("기존 계정·전용 폴더 연결을 유지합니다. 다른 계정이나 OAuth 설정으로 자동 전환하지 않습니다.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .confirmationDialog("이 Mac의 Google 연결을 해제할까요?", isPresented: $confirmDisconnect) {
            Button("이 Mac 토큰 삭제", role: .destructive) { model.disconnect() }
        } message: {
            Text("이 Mac의 토큰만 삭제합니다. Google의 기존 파일과 앱 권한, 이 Mac의 가져오기 기록과 폴더 연결은 보존됩니다.")
        }
    }
}
