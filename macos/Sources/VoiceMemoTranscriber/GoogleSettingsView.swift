import SwiftUI

struct GoogleSettingsView: View {
    @ObservedObject var model: GoogleConnectionModel
    var onSync: () -> Void = {}
    @ViewState private var confirmDisconnect = false

    var body: some View {
        Section("Google Drive · Vemo 테스트") {
            Text(model.hasClientConfiguration ? "Desktop OAuth 설정 준비됨" : "Desktop OAuth JSON을 먼저 선택하세요.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("OAuth JSON 선택…") { model.selectClientJSON() }
                    .disabled(!model.canSelectClient)
                Button(model.isConnected ? "같은 계정으로 다시 승인" : "Google 연결") { model.connect() }
                    .disabled(model.isBusy || !model.hasClientConfiguration || model.needsKeychainRecovery)
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
            if model.needsKeychainRecovery {
                Button("Keychain 접근 복구…") { model.recoverKeychainAccess() }.disabled(model.isBusy)
                Text("macOS의 기존 토큰 접근 승인입니다. Google 권한 재동의나 새 폴더 생성은 하지 않습니다.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(model.status).font(.caption).foregroundStyle(.secondary)
                .accessibilityLabel("Google 연결 상태")
                .accessibilityValue(model.status)
            Text("설치된 Chrome에서 Google 승인을 엽니다. Chrome이 없으면 기본 브라우저를 사용합니다. 권한은 Vemo가 사용하는 파일의 drive.file로 제한합니다.")
                .font(.caption).foregroundStyle(.secondary)
            Text("완료된 폰 녹음을 Mac으로 가져와 처리하고, 전사와 노트가 모두 완료된 결과를 Google에 올립니다. 로컬 가져오기와 원격 결과 확인은 상태에 구분해 표시합니다. 실제 폰·Mac 왕복은 검증 중입니다.")
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
