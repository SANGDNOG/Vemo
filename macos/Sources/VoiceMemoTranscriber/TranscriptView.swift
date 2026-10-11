import SwiftUI
import NativeCore
struct TranscriptView: View {
    @ObservedObject var model: AppModel
    let job: Job
    @ViewState private var editingTopic = false
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) { Text(job.title).font(.title.bold()).textSelection(.enabled); Text([job.recorded_at ?? job.recording_date ?? "녹음 시작일 미확인", job.statusLabel].joined(separator: " · ")).font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Menu { RecordingFolderMenu(model: model, jobID: job.id); Divider(); Button("주제 수정") { editingTopic = true }; Button("제목 다시 생성") { model.retitle(generate: true) } } label: { Image(systemName: "ellipsis.circle") }.vemoMenuSurface().accessibilityLabel("녹음 메뉴")
            }
            Label(model.folderName(for: job.id), systemImage: "folder").font(.caption).foregroundStyle(VemoTheme.ink)
            if job.status == "needs_recording_date" || job.recording_date == nil {
                HStack { TextField("녹음 시작일 YYYY-MM-DD", text: $model.dateInput).textFieldStyle(.roundedBorder); Button("날짜 확인") { model.setDate() }.buttonStyle(VemoButtonStyle(emphasis: .primary)) }.help("파일 수정일 대신 실제 녹음 시작일을 입력하세요.")
            }
            if editingTopic { HStack { TextField("주제만 수정", text: $model.topic).textFieldStyle(.roundedBorder); Button("저장") { model.retitle(generate: false); editingTopic = false }.buttonStyle(VemoButtonStyle(emphasis: .primary)); Button("취소") { editingTopic = false } }; Text("녹음일은 그대로 유지됩니다.").font(.caption).foregroundStyle(.secondary) }
            if job.status != "completed" { stage }
            if model.audioDuration > 0 {
                HStack { Button { model.togglePlayback() } label: { Image(systemName: model.playing ? "pause.fill" : "play.fill") }.buttonStyle(VemoButtonStyle(emphasis: .primary)).accessibilityLabel(model.playing ? "일시 정지" : "재생")
                    Slider(value: Binding(get: { model.currentTime }, set: { model.seek($0) }), in: 0...max(1, model.audioDuration)).accessibilityLabel("재생 위치")
                    Text("\(clock(model.currentTime)) / \(clock(model.audioDuration))").font(.caption.monospacedDigit())
                }
            }
            HStack(spacing: 4) {
                detailTab("전사", icon: "text.alignleft", value: "transcript")
                detailTab("노트", icon: "note.text", value: "notes")
            }.padding(4).background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 11))
            Divider()
            if model.detailMode == "notes" { NotesView(model:model,job:job) }
            else {
            if let transcript = model.transcript {
                if transcript.alignment_status != "completed" && transcript.alignment_status != "success" { Text("시간 정렬이 완료되지 않아 정밀 구간 이동과 자막 내보내기가 제한됩니다.").font(.caption).foregroundStyle(.secondary) }
                if job.status != "completed" { Text("처리 중 결과 · 최종 게시 전").font(.caption).foregroundStyle(.secondary) }
                ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 18) {
                        ForEach(Array(TranscriptDisplay.rows(transcript).enumerated()), id: \.offset) { index, row in
                            HStack(alignment: .top, spacing: 16) {
                                if let timing = row.timing {
                                    Button(clock(timing.start_s)) { model.seek(timing.start_s) }.buttonStyle(.plain).font(.caption.monospacedDigit()).foregroundStyle(.tint)
                                }
                                Text(row.text).font(.body).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).fixedSize(horizontal: false, vertical: true)
                            }.padding(.vertical,4).padding(.horizontal,6).background(model.quoteRow == index ? VemoTheme.accent.opacity(0.10) : Color.clear,in:RoundedRectangle(cornerRadius:6)).id(index)
                        }
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 8)
                }
                .onAppear { if let row = model.quoteRow { proxy.scrollTo(row,anchor:.center) } }
                .onChange(of:model.quoteRow) { _,row in if let row { withAnimation(VemoTheme.motion(reducedMotion)) { proxy.scrollTo(row,anchor:.center) } } }
                }
                HStack {
                    Button("복사", systemImage: "doc.on.doc") { model.copyTranscript() }
                    Menu("내보내기") { Button("텍스트 (.txt)") { model.export("txt") }; Button("Markdown (.md)") { model.export("md") }; if ["completed", "success"].contains(transcript.alignment_status) { Button("자막 (.srt)") { model.export("srt") }; Button("자막 (.vtt)") { model.export("vtt") } } }.vemoMenuSurface()
                    Button("결과 폴더", systemImage: "folder") { model.openResultFolder() }.disabled(job.status != "completed")
                }
            } else { Spacer(); ContentUnavailableView("아직 전사문이 없습니다", systemImage: "text.alignleft", description: Text(job.status == "unprocessed" ? "전사하기를 눌러 이 녹음을 처리하세요." : "단계가 진행되면 원문을 여기에서 확인할 수 있습니다.")); Spacer() }
            }
        }.padding(24).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
    private func detailTab(_ title: String, icon: String, value: String) -> some View {
        let active = model.detailMode == value
        return Button { withAnimation(VemoTheme.motion(reducedMotion)) { model.detailMode = value } } label: {
            Label(title, systemImage: icon).font(.system(size: 12, weight: active ? .semibold : .medium))
                .foregroundStyle(active ? VemoTheme.ink : Color.secondary)
                .padding(.horizontal, 15).padding(.vertical, 7)
                .background(active ? VemoTheme.accent.opacity(0.10) : .clear, in: RoundedRectangle(cornerRadius: 8))
        }.buttonStyle(.plain).accessibilityAddTraits(active ? .isSelected : [])
    }
    var stage: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack { if ["preparing", "transcribing", "aligning", "titling", "noting", "saving"].contains(job.status) { ProgressView().controlSize(.small) }; Text(job.statusLabel + (job.progress.isEmpty ? "" : " · " + job.progress)); Spacer(); Text("경과 \(clock(job.elapsed_seconds))").font(.caption.monospacedDigit()).foregroundStyle(.secondary) }
            if let reason = job.wait_reason { Text(waitReason(reason)).font(.caption).foregroundStyle(.secondary) }
            if let error = job.error, error != .null { Text(error.string ?? error.object?["message"]?.string ?? "처리 실패").foregroundStyle(.red).textSelection(.enabled) }
            HStack { if job.status == "unprocessed" { Button("전사하기") { model.command("enqueue", ["uuids": .array([.string(job.uuid)])]) }.buttonStyle(VemoButtonStyle(emphasis: .primary)) }; if ["failed", "waiting", "paused"].contains(job.status) { Button("다시 시도") { model.command("retry", ["job_id": .string(job.id)]) } } }
        }.padding(14).background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 12))
    }
    func waitReason(_ value: String) -> String {
        switch value {
        case "models": return "설정에서 모델 다운로드를 완료하면 처리합니다."
        case "ac_power": return "전원을 연결하면 처리합니다."
        case "paused": return "새 작업이 일시 중지되었습니다. 메뉴에서 처리 재개를 선택하세요."
        case "source_incomplete": return model.sourceDiagnostic ?? "녹음 완료와 동기화를 기다립니다."
        case "source_copy": return "원본 파일을 안전하게 복사하지 못했습니다. 접근 상태를 확인하세요."
        case "publication": return "결과 폴더에 안전하게 저장하는 중입니다."
        case "restart": return "마지막 저장 지점에서 처리를 재개합니다."
        default: return "처리 조건을 확인하고 있습니다."
        }
    }
    func clock(_ seconds: Double) -> String { let n = Int(max(0, seconds)); return String(format: "%d:%02d", n/60, n%60) }
}
