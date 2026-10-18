# Windows / Mac 남은 동작 차이

2026-10-18 독립 검증. Mac 소스는 읽기만 했으며 Windows 제품 소스와 기존 테스트는 수정하지 않았다. 이 문서는 기능 확대 제안이 아니라 현재 소스에서 확인한 사용자 동작 차이와 검증 범위를 기록한다. 실제 Windows 추론·설치·실계정 검증은 root 담당이다.

## 현재 수정 범위와 독립 재현

| 항목 | 관찰한 기존 동작 | 필요한 검증 |
| --- | --- | --- |
| 설정에서 같은 녹음 클릭 | 기존 `jobs.SelectionChanged`만으로는 같은 행 클릭 후 설정이 남았다. 최초 테스트 1실패로 재현. 현재 `MainWindow.RecordingInteraction.cs`는 실제 포인터 press/release를 처리한다. | 수정 후 실제 MouseDown/MouseUp 테스트 통과. 선택 유지와 상세 재표시도 확인. |
| 타임라인·카드 | 기존 Windows TextBox 대신 `TranscriptView`/`TranscriptDisplay`와 timing sidecar 추가. | millisecond 변환·반복 문장 순서·간격 유지·잘못된 정렬의 시각 미표시·실제 버튼 클릭 seek 통과. TXT/ZIP 원문 보존 통과. |
| 진행률 | 실제 whisper 출력의 104%를 parser/runner가 제한한다. | 음수·104%·후퇴·중복·NaN·다른 작업/늦은 이벤트 검증 통과. running 0..99, 단조 증가, 노트 단계 % 없음. 완료 시 UI가 숨겨지므로 완료 100% 표시 자체를 테스트했다고 주장하지 않는다. |
| 제목·날짜 | inline 제목 편집과 별도 nullable `RecordingDate` 추가. | written offset 날짜 접두부·suffix 1회·원래 ISO 값 유지·day-only 저장/재열기·잘못된 날짜의 title/date 원자적 거절·mtime 미추론 통과. |

native 확인 자료: `local-validation/windows-reading/asr-fixture.json`의 첫 구간은 offsets `0..5460`, timestamps `00:00:00,000..00:00:05,460`이다. 이 관찰만으로 모든 whisper 버전의 형식을 동일하다고 단정하지 않는다.

## 현재 범위 밖에 남은 구체적 차이

아래 참조는 저장소 루트 기준이며, 동시 수정으로 행 번호가 바뀔 수 있어 함수·타입 이름을 함께 표시했다.

| 우선순위 | 사용자에게 보이는 차이 | Mac 근거 | Windows 근거 / 범위 |
| --- | --- | --- | --- |
| P1 | 완료된 녹음의 노트만 다시 생성하는 직접 동작이 없다. 기존 결과를 보면서 재생성·실패 상태를 확인하는 Mac 동작과 다르다. | `macos/Sources/VoiceMemoTranscriber/NotesView.swift:102` 재생성 버튼, `AppModel.swift:1023` `canGenerateNotes`. | `windows/Vemo.Core/LibraryStore.cs` `Retry`: Failed/Cancelled만 허용. `windows/Vemo.App/MainWindow.cs` `SelectionMenu`: 실패 재시도만 노출. 완료 작업에 가짜 실패를 넣는 방식은 parity 해결로 보지 않는다. |
| P2 | 제목을 전사 내용에서 다시 생성하는 메뉴가 없다. inline 수동 편집과 별도 동작이다. | `macos/Sources/VoiceMemoTranscriber/TranscriptView.swift` 상세 메뉴의 `제목 다시 생성`, `AppModel.swift:594` `retitle(generate:)`. | `windows/Vemo.App/MainWindow.cs` `SelectionMenu`/`BeginTitleEdit`: 수동 제목 편집·실패 재시도. `windows/Vemo.Core/Inference.cs`는 ASR/notes 단계이며 제목 모델 실행 단계가 없다. |
| P1 | 긴 전사에서 노트 생성이 14,000자 입력 한도로 중단된다. | Mac은 `NotesView.swift`에서 노트 실패 원인·기존 노트 보존·재시도를 별도 표시한다. Mac 무제한 처리 성공을 주장하는 근거는 아님. | `windows/Vemo.Core/Inference.cs` `LocalCliInferenceEngine.GenerateNotesAsync`: `numbered.Length > 14000` 즉시 실패, chunked synthesis 미지원 명시. 한도 자체와 한국어 오류/복구 안내를 구분해서 검토해야 한다. |
| P2 | 노트의 근거를 펼치고 눌러 해당 전사 위치로 돌아갈 수 없다. 키워드별 필터·핵심 요약·강조 문장도 현재 정적 Markdown 표시와 다르다. | `NotesView.swift:53` 요약, `:65` 키워드, `:90` 강조, `evidence`/`quote`; `AppModel.swift:1026` `revealQuote`. | `windows/Vemo.App/MainWindow.Results.cs` `RenderNotes`: 행을 heading/bullet TextBlock으로 변환. `NotesDocument`는 숫자 refs를 저장하지만 해당 source 행으로 이동하는 UI가 없다. 현재 카드 작업을 근거 탐색까지 구현된 것으로 계산하지 않는다. |
| P2 | 자막 SRT/VTT와 전사 Markdown 내보내기가 없다. | `TranscriptView.swift` export 메뉴, `macos/Sources/NativeCore/TranscriptExport.swift:4`: TXT/MD/SRT/VTT, 자막은 정렬 성공 시만 허용. | `windows/Vemo.App/MainWindow.Results.cs` `SaveResult`: 전사 TXT / 노트 MD만 선택. 새 timing 저장은 자막 내보내기 지원의 증거가 아니다. |
| P2 | 모델 선택 후 앱 안에서 다운로드/재개·설치 상태·메모리 추정 확인까지 이어지는 흐름이 없다. | `ModelSettingsView.swift:33` 기기 추천, `:54` ASR/title 다운로드·재개, `:56` 노트 다운로드·재개, `:75` 용량/메모리. | `windows/Vemo.App/SettingsView.cs` 모델 프로필/로컬 경로/프리셋. `windows/Vemo.Provision`의 별도 도구가 있어도 Settings UI 연결을 의미하지 않는다. 다운로드 정책·릴리스 핀은 root 결정 범위. |
| P2 / 정책 확인 | 폴더 이동/이름 변경 후 기존 ZIP을 새 위치로 맞추는 동작이 다르다. Windows는 기존 ZIP을 보존하고 다음 export를 새 경로에 만들 수 있다. | `macos/Sources/NativeCore/PerRecordingZIPStore.swift:18` `relocateIfNeeded`, receipt와 source 검증 후 이동. | `windows/Vemo.Core/LibraryStore.cs` `Move`/`RenameFolder` 등은 `ExportPath=null`; `Export`는 충돌 시 숫자 suffix로 새 파일을 만든다. 기존 ZIP 보존은 현재 의도일 수 있으므로 임의 삭제/이동을 수정으로 제안하지 않는다. |

## 누락으로 분류하지 않은 항목

Windows에는 실제 player, 입력 폴더 watcher, 전원 정책, 자동 ZIP, Google 연결 및 processor ownership 보호, 폴더 계층·이동·접기, Startup/Tray 지원이 있다. Mac의 Apple Voice Memos 직접 읽기는 macOS 전용 접근이므로 Windows 일반 기능 누락으로 세지 않았다. 플랫폼별 글꼴 렌더링 차이도 이번 동작 parity 판정에 사용하지 않았다.

## 검증 상태

- 최초 포인터 재현: `RecordingInteractionIndependentTests` 0 pass / 1 fail. 수정 후 최종 **4 pass / 0 fail / 0 skip**. 실제 동일 행 pointer, 카드 seek, inline 제목 입력, 노트 indeterminate 진행 표시를 확인했다.
- Core 최종 **25 pass / 0 fail / 0 skip**: `ReadingIntegrityIndependentTests` 18건, `RecordingMetadataIndependentTests` 7건. offset/day-only/mtime, timing hash/정렬, 104%/jitter/늦은 이벤트, retry revision, plain ZIP을 확인했다.
- 중간 Core 23 pass / 1 fail에서 duplicate JSON timing sidecar가 예외를 던지는 제품 문제를 발견했다. Lagrange가 `TranscriptTimingFile.Read`의 `InvalidDataException` fallback을 수정한 뒤 동일 테스트로 재검증했다. 잘못된 timing을 허용하도록 테스트를 약화하지 않았다.
- 명령: `dotnet test windows/Vemo.Core.Tests --filter 'FullyQualifiedName~ReadingIntegrityIndependentTests|FullyQualifiedName~RecordingMetadataIndependentTests' --no-restore -v minimal`; `dotnet test windows/Vemo.App.Tests --filter FullyQualifiedName~RecordingInteractionIndependentTests --no-restore -v minimal`. 지정 SDK 사용. 전체 suite와 native 실행은 중복 실행하지 않았다.
- timing/progress API는 Lagrange, App 표시·입력은 Aristotle 소유. 이 문서 작성자는 새 독립 테스트와 이 문서만 수정했다.
- 현재 문서는 소스 기반 parity 감사이며 실제 Mac 앱 재실행이나 Windows 설치 성공을 의미하지 않는다.

## 통합 및 설치 검증 (root)

- 최종 전체 로컬 테스트: Core 183통과/1플랫폼 skip, App 96통과/1실제 추론 opt-in skip, StartupTray 17통과, Provision 12통과, Playback 2통과/1플랫폼 skip.
- 실제 Windows PC App 테스트: **97통과, 실패·skip 0**, 93.714초. 합성 한국어 음성 파일의 실제 whisper/llama 처리 70.87초, 전사→노트→폴더별 ZIP 완료. 실제 진행 이벤트 preparing→transcribing 52%→99%→notes→saving→completed 확인.
- 실제 whisper 구간 9개와 문장 행 9개, 원문 전사·원본 음성 hash·ZIP 3개 항목·재실행 후 timing 유지 검증. 원본 녹음이나 사용자 라이브러리는 테스트에 사용하지 않았다.
- Windows Skia/headless 실제 UI 렌더 1280×820, 980×540 확인. 물리 디스플레이/DPI나 사용자의 수동 클릭 확인과는 다르다.
- 설치: `%LOCALAPPDATA%\Programs\Vemo\0.1.8-transcript-reading\Vemo.App.exe`, 바탕화면 `Vemo.lnk` 연결. 배포 App/Core DLL SHA256이 Windows에서 테스트한 DLL과 일치. 기존 0.1.7 프로세스·라이브러리는 종료/변경하지 않았으므로 사용자가 앱을 완전히 종료 후 바로가기로 다시 실행해야 한다.
- 이번 작업은 Google OAuth 실계정 재승인/Android 왕복을 다시 수행하지 않았다. 이전 Drive 수정과 회귀 테스트는 유지된다.
- 로컬 증거: `local-validation/windows-reading/`의 local-final.log, native.trx, reading-screenshots, install-receipt.json. 자동 생성/계정 설정 파일은 Git에 추가하지 않는다.
