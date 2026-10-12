import SwiftUI
import NativeCore

struct NotesView: View {
    @ObservedObject var model: AppModel
    let job: Job
    @ViewState private var keyword: String?
    @ViewState private var expandedEvidence: Set<String> = []
    private var notesContent: Data? {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return model.notes.flatMap { try? encoder.encode($0) }
    }
    @Environment(\.accessibilityReduceMotion) private var reducedMotion
    private var notesProgressTitle: String? {
        let pending = job.notes_status == "pending" || job.notes_status == "running"
        let notesStage = job.stage == "noting" || job.stage == "notes"
        guard job.status == "noting" || (pending && notesStage && ["waiting", "paused"].contains(job.status)) else { return nil }
        if job.status == "paused" || job.wait_reason == "paused" || (model.paused && job.status == "waiting") { return "노트 생성이 일시 중지되었습니다" }
        return job.status == "noting" ? (job.progress.isEmpty ? "전체 내용 정리 중" : "노트를 정리하고 있습니다") : "노트 생성 대기 중"
    }
    private var notesProgressText: String? {
        notesProgressTitle.map { $0 + (job.progress.isEmpty ? "" : " · " + job.progress) }
    }
    private var notesFailureText: String? {
        guard job.notes_status == "failed" else { return nil }
        switch job.notes_error?.object?["code"]?.string {
        case "input_too_long", "notes_input_too_long": return "원문이 모델의 입력 한도를 넘었습니다. 원문과 이전 노트는 보존되어 있습니다."
        case "output_truncated", "notes_output_truncated": return "노트 출력이 끝까지 생성되지 않았습니다. 이전 노트는 보존되어 있으며 다시 생성할 수 있습니다."
        case "model_missing", "notes_model_missing": return "상세 노트 모델이 필요합니다. 설정에서 다운로드하세요. 원문과 이전 노트는 보존되어 있습니다."
        default: return "노트 생성에 실패했습니다. 원문과 이전 노트는 보존되어 있으며 다시 생성할 수 있습니다."
        }
    }
    var body: some View {
        VStack(alignment:.leading,spacing:14) {
            Text("AI는 내용을 누락하거나 조건을 잘못 요약할 수 있습니다. 중요한 내용은 원문 근거를 확인하세요.")
                .font(.caption).foregroundStyle(.secondary)
            if notesProgressTitle != nil {
                Text("중단하면 현재 분석은 처음부터 다시 시작합니다. 이전 노트는 보존됩니다.").font(.caption).foregroundStyle(.secondary)
            }
            if !DemoContent.enabled && model.notesModelState == "missing_or_invalid" {
                HStack {
                    Text("상세 노트 모델이 필요합니다.").font(.caption).foregroundStyle(.secondary)
                    SettingsLink { Text("설정에서 다운로드") }
                }
            }
            if let notes = model.notes {
                Text("AI가 정리한 노트 · 근거를 펼친 뒤 인용을 누르면 원문으로 이동합니다.").font(.caption).foregroundStyle(.secondary)
                if let progress = notesProgressText { Text(progress).font(.caption).foregroundStyle(VemoTheme.ink) }
                if let failure = notesFailureText { Text(failure).font(.caption).foregroundStyle(.orange) }
                ScrollView {
                    LazyVStack(alignment:.leading,spacing:24) {
                        section("핵심 요약", icon:"text.alignleft") {
                            ForEach(Array(notes.summary.enumerated()),id:\.offset) { index,item in
                                HStack(alignment:.top,spacing:10) {
                                    Text("\(index + 1).").font(.callout.weight(.semibold)).foregroundStyle(VemoTheme.ink)
                                        .frame(minWidth:24,alignment:.trailing).accessibilityHidden(true)
                                    VStack(alignment:.leading,spacing:6) {
                                        Text(item.text).textSelection(.enabled).fixedSize(horizontal:false,vertical:true)
                                        evidence(item.quotes, id:"summary-\(index)", context:"핵심 요약 \(index + 1)")
                                    }.frame(maxWidth:.infinity,alignment:.leading)
                                }
                            }
                        }
                        section("키워드별 정리",icon:"number") {
                            let keywords = notes.topics.map(\.keyword).reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
                            ScrollView(.horizontal,showsIndicators:false) {
                                HStack(spacing:8) {
                                    keywordButton("모두",value:nil)
                                    ForEach(keywords,id:\.self) { value in keywordButton(value,value:value) }
                                }
                            }
                            ForEach(keywords.filter { keyword == nil || $0 == keyword },id:\.self) { value in
                                VStack(alignment:.leading,spacing:12) {
                                    Text(value).font(.headline).foregroundStyle(VemoTheme.ink)
                                    ForEach(Array(notes.topics.enumerated()).filter { $0.element.keyword == value },id:\.offset) { index,item in
                                        VStack(alignment:.leading,spacing:6) {
                                            HStack(alignment:.top,spacing:8) {
                                                Text("•").accessibilityHidden(true)
                                                Text(item.summary).textSelection(.enabled).fixedSize(horizontal:false,vertical:true)
                                            }
                                            evidence(item.quotes, id:"topic-\(index)", context:"\(value) 주제 \(index + 1)")
                                        }
                                    }
                                }.frame(maxWidth:.infinity,alignment:.leading)
                                    .padding(14)
                                    .background(Color.secondary.opacity(0.05),in:RoundedRectangle(cornerRadius:12))
                            }
                        }
                        section("강조 문장", icon:"highlighter") {
                            evidence(notes.highlights.map(\.quote), id:"highlights", context:"강조 문장", highlighted:true)
                        }
                    }.frame(maxWidth:.infinity,alignment:.leading).padding(.vertical,8)
                }
                HStack {
                    Button("노트 복사",systemImage:"doc.on.doc") { model.copyNotes() }
                    Menu("내보내기") {
                        Button("Markdown (.md)") { model.exportNotes() }
                        Button("결과 폴더 열기") { model.openResultFolder() }.disabled(job.status != "completed")
                    }.vemoMenuSurface()
                    Spacer()
                    Button(model.manualProcessingTitle("다시 생성"),systemImage:"arrow.clockwise") { model.generateNotes() }.disabled(!model.canGenerateNotes)
                }
            } else {
                VStack(spacing: 14) {
                    Image(systemName: "note.text").font(.system(size: 40)).foregroundStyle(.secondary).accessibilityHidden(true)
                    Text(notesProgressTitle ?? (job.notes_status == "failed" ? "노트 생성에 실패했습니다" : "이 녹음의 노트를 만들어 보세요")).font(.title2.weight(.semibold))
                    Text(notesProgressTitle != nil ? (job.progress.isEmpty ? "잠시 기다려 주세요. 길이에 따라 몇 분 걸릴 수 있습니다." : job.progress) : (model.notesReadError ?? notesFailureText ?? "핵심 요약, 중요한 문장, 키워드별 내용을 Mac에서 정리합니다."))
                        .font(.callout).foregroundStyle(.secondary)
                    if notesProgressTitle == nil && model.canGenerateNotes {
                        Button(model.manualProcessingTitle(job.notes_status == "failed" ? "노트 다시 생성" : "노트 생성"), systemImage: job.notes_status == "failed" ? "arrow.clockwise" : "plus") { model.generateNotes() }.buttonStyle(VemoButtonStyle(emphasis: .primary))
                    }
                }
                .multilineTextAlignment(.center)
                .frame(maxWidth: 460)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
            }
        }.frame(maxWidth:.infinity,maxHeight:.infinity,alignment:.topLeading)
        .onChange(of:job.id) { _,_ in resetDisclosure() }
        .onChange(of:notesContent) { _,_ in resetDisclosure() }
    }
    func section<Content:View>(_ title:String, icon:String,@ViewBuilder content:()->Content) -> some View {
        VStack(alignment:.leading,spacing:12) { Label(title,systemImage:icon).font(.headline); content() }
    }
    private func resetDisclosure() {
        keyword = nil
        expandedEvidence = []
    }
    @ViewBuilder
    private func evidence(_ values:[String], id:String, context:String, highlighted:Bool = false) -> some View {
        if !values.isEmpty {
            let expanded = expandedEvidence.contains(id)
            VStack(alignment:.leading,spacing:8) {
                Button {
                    withAnimation(VemoTheme.motion(reducedMotion)) {
                        if expanded { expandedEvidence.remove(id) } else { expandedEvidence.insert(id) }
                    }
                } label: {
                    HStack(spacing:6) {
                        Image(systemName:expanded ? "chevron.down" : "chevron.right").font(.caption2)
                        Text(expanded ? "근거 접기" : "근거 보기")
                        Text("\(values.count)").foregroundStyle(.secondary)
                        Spacer(minLength:0)
                    }.font(.caption).foregroundStyle(VemoTheme.ink)
                        .padding(.vertical,7).padding(.horizontal,8)
                        .frame(maxWidth:.infinity,alignment:.leading).contentShape(Rectangle())
                }.buttonStyle(.plain)
                    .accessibilityLabel("\(context) 근거 \(values.count)개")
                    .accessibilityValue(expanded ? "펼쳐짐" : "접힘")
                    .accessibilityHint(expanded ? "근거를 접습니다" : "근거를 펼칩니다")
                if expanded {
                    VStack(alignment:.leading,spacing:6) {
                        ForEach(Array(values.enumerated()),id:\.offset) { _,value in quote(value,highlighted:highlighted) }
                    }
                }
            }
        }
    }
    func quote(_ value:String, highlighted: Bool = false) -> some View {
        Button { model.revealQuote(value) } label: {
            HStack(alignment:.top,spacing:8) { Image(systemName:"quote.opening").font(.caption); Text(value).multilineTextAlignment(.leading); Spacer(minLength:4); Image(systemName:"arrow.up.left").font(.caption) }.foregroundStyle(.secondary)
                .padding(highlighted ? 10 : 8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(highlighted ? VemoTheme.wash : .clear, in: RoundedRectangle(cornerRadius: 10))
                .contentShape(RoundedRectangle(cornerRadius: highlighted ? 10 : 0))
        }.buttonStyle(.plain).help("전사 원문에서 확인")
            .accessibilityLabel("원문 인용: " + value).accessibilityHint("전사 원문으로 이동합니다")
    }
    func keywordButton(_ title:String,value:String?) -> some View {
        Button { withAnimation(VemoTheme.motion(reducedMotion)) { keyword = value } } label: { Text(title).font(.caption).padding(.horizontal,10).padding(.vertical,5).background(keyword == value ? VemoTheme.accent.opacity(0.14) : Color.secondary.opacity(0.08),in:Capsule()).contentShape(Capsule()) }.buttonStyle(.plain).foregroundStyle(keyword == value ? VemoTheme.ink : Color.secondary).accessibilityAddTraits(keyword == value ? .isSelected : [])
    }
}
