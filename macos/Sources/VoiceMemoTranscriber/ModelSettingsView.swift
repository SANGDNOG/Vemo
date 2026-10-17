import SwiftUI
import NativeCore

struct SelectableModel: Decodable, Identifiable {
    let id: String
    let role: String
    let label: String
    let download_bytes_estimate: Double
    let memory_gb_estimate: Double
    let quality_status: String
    let supported: Bool
    let state: String

    var preparationLabel: String {
        switch state {
        case "ready": return "설치됨"
        case "verifying": return "파일 확인 중"
        default: return "다운로드 필요"
        }
    }
}

struct ModelSettingsView: View {
    @ObservedObject var model: AppModel
    @ViewState private var asr = ""
    @ViewState private var notes = ""

    var body: some View {
        Section("AI 모델") {
            if model.availableModels.isEmpty {
                Text(model.modelStatus)
            } else {
                Text(model.modelDeviceDescription).font(.caption).foregroundStyle(.secondary)
                selection("전사", role: "asr", value: $asr)
                selection("노트", role: "notes", value: $notes)
                HStack {
                    Button("기기에 맞는 추천 선택") {
                        if let value = model.recommendedASR { asr = value }
                        if let value = model.recommendedNotes { notes = value }
                    }.disabled(!model.canChangeModels || (model.recommendedASR == nil && model.recommendedNotes == nil))
                    Spacer()
                    Button("선택 적용") { model.applyModelSelection(asr: asr, notes: notes) }
                        .disabled(!model.canChangeModels || asr.isEmpty || notes.isEmpty || (asr == model.selectedASRModel && notes == model.selectedNotesModel))
                }
                if let reason = model.modelChangeMessage { Text(reason).font(.caption).foregroundStyle(.secondary) }
                if model.recommendedNotes == nil {
                    Text("현재 기기에서 추천할 수 있는 검증된 노트 모델이 없습니다. 기존 노트 선택은 유지합니다.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Text("추천은 메모리 용량에 따른 추정입니다. 작은 모델은 속도·메모리에 유리하지만 한국어 정확도와 상세 노트 품질이 달라질 수 있습니다. 선택을 바꿔도 기존 결과는 유지됩니다.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(model.modelStatus).font(.caption)
            Button("전사 · 제목 모델 다운로드 / 재개") { model.command("download_models") }
            Text(model.notesModelStatus).font(.caption)
            Button("선택한 노트 모델 다운로드 / 재개") { model.downloadNotesModel() }
            Text("다운로드에는 인터넷이 필요합니다. 녹음 처리와 노트 생성은 이 기기에서 실행합니다.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .onAppear { resetSelection() }
        .onChange(of: model.selectedASRModel) { _, _ in resetSelection() }
        .onChange(of: model.selectedNotesModel) { _, _ in resetSelection() }
    }

    private func resetSelection() { asr = model.selectedASRModel; notes = model.selectedNotesModel }

    @ViewBuilder private func selection(_ title: String, role: String, value: Binding<String>) -> some View {
        Picker(title, selection: value) {
            ForEach(model.availableModels.filter { $0.role == role }) { item in
                Text(item.quality_status == "synthetic_validation_failed" ? "\(item.label) · 품질 검증 실패, 사용 불가" : item.label)
                    .tag(item.id).disabled(!item.supported)
            }
        }.disabled(!model.canChangeModels)
        if let item = model.availableModels.first(where: { $0.id == value.wrappedValue }) {
            Text("\(item.preparationLabel) · 다운로드 약 \(item.download_bytes_estimate / 1_000_000_000, specifier: "%.1f") GB · 메모리 약 \(item.memory_gb_estimate, specifier: "%.0f") GB 이상")
                .font(.caption).foregroundStyle(.secondary)
            if item.quality_status != "existing_default" {
                Text("실험적 선택 · 한국어 전사 또는 상세 노트 품질 비교가 아직 완료되지 않았습니다.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
