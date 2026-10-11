import SwiftUI
import NativeCore

struct NotesView: View {
    @ObservedObject var model: AppModel
    let job: Job
    @ViewState private var keyword: String?
    var body: some View {
        VStack(alignment:.leading,spacing:14) {
            if let notes = model.notes {
                Text("AI가 정리한 노트 · 인용을 누르면 원문으로 이동합니다.").font(.caption).foregroundStyle(.secondary)
                if job.notes_status == "failed" { Text("노트 갱신에 실패했습니다. 이전 노트는 보존되어 있습니다.").font(.caption).foregroundStyle(.orange) }
                ScrollView {
                    LazyVStack(alignment:.leading,spacing:24) {
                        section("핵심 요약", icon:"text.alignleft") {
                            ForEach(Array(notes.summary.enumerated()),id:\.offset) { _,item in
                                VStack(alignment:.leading,spacing:8) { Text(item.text).textSelection(.enabled); quotes(item.quotes) }
                            }
                        }
                        section("강조 문장", icon:"highlighter") {
                            ForEach(Array(notes.highlights.enumerated()),id:\.offset) { _,item in
                                quote(item.quote).padding(10).frame(maxWidth:.infinity,alignment:.leading).background(.yellow.opacity(0.10),in:RoundedRectangle(cornerRadius:8))
                            }
                        }
                        section("키워드별 정리",icon:"number") {
                            ScrollView(.horizontal,showsIndicators:false) {
                                HStack(spacing:8) {
                                    keywordButton("모두",value:nil)
                                    ForEach(Array(Set(notes.topics.map(\.keyword))).sorted(),id:\.self) { value in keywordButton(value,value:value) }
                                }
                            }
                            ForEach(Array(notes.topics.enumerated()).filter { keyword == nil || $0.element.keyword == keyword },id:\.offset) { _,item in
                                VStack(alignment:.leading,spacing:8) { Text(item.keyword).font(.headline); Text(item.summary).textSelection(.enabled); quotes(item.quotes) }
                                Divider()
                            }
                        }
                    }.frame(maxWidth:.infinity,alignment:.leading).padding(.vertical,8)
                }
                HStack {
                    Button("노트 복사",systemImage:"doc.on.doc") { model.copyNotes() }
                    Button("Markdown 내보내기") { model.exportNotes() }
                    Spacer()
                    Button("다시 생성",systemImage:"arrow.clockwise") { model.generateNotes() }.disabled(!model.canGenerateNotes)
                    Button("결과 폴더",systemImage:"folder") { model.openResultFolder() }.disabled(job.status != "completed")
                }
            } else {
                Spacer()
                ContentUnavailableView(job.status == "noting" ? "노트를 정리하고 있습니다" : "이 녹음의 노트를 만들어 보세요",systemImage:"note.text",description:Text(model.notesReadError ?? (job.notes_status == "failed" ? "노트 생성에 실패했습니다. 원문은 보존되어 있으며 다시 생성할 수 있습니다." : "핵심 요약, 중요한 문장, 키워드별 내용을 Mac에서 정리합니다.")))
                if model.canGenerateNotes { Button("노트 생성",systemImage:"sparkles") { model.generateNotes() }.buttonStyle(.borderedProminent).frame(maxWidth:.infinity) }
                Spacer()
            }
        }.frame(maxWidth:.infinity,maxHeight:.infinity,alignment:.topLeading)
        .onChange(of:job.id) { _,_ in keyword = nil }
    }
    func section<Content:View>(_ title:String, icon:String,@ViewBuilder content:()->Content) -> some View {
        VStack(alignment:.leading,spacing:12) { Label(title,systemImage:icon).font(.headline); content() }
    }
    func quotes(_ values:[String]) -> some View {
        VStack(alignment:.leading,spacing:6) { ForEach(Array(values.enumerated()),id:\.offset) { _,value in quote(value) } }
    }
    func quote(_ value:String) -> some View {
        Button { model.revealQuote(value) } label: {
            HStack(alignment:.top,spacing:8) { Image(systemName:"quote.opening").font(.caption); Text(value).multilineTextAlignment(.leading); Spacer(minLength:4); Image(systemName:"arrow.up.left").font(.caption) }.foregroundStyle(.secondary)
        }.buttonStyle(.plain).help("전사 원문에서 확인")
    }
    func keywordButton(_ title:String,value:String?) -> some View {
        Button { keyword = value } label: { Text(title).font(.caption).padding(.horizontal,10).padding(.vertical,5).background(keyword == value ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.08),in:Capsule()) }.buttonStyle(.plain)
    }
}
