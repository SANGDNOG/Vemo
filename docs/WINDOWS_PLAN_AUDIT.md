# Windows 승인 디자인 / 구현 독립 감사

2026-10-18 · 실행 검증 갱신. 담당: 명시적으로 선택된 `test_debug_pro` (Team2 test_debug_complex 할당량 제한 대체). 구현 소스는 여러 작업자가 변경 중이며 본 담당자는 문서와 지정 테스트만 수정한다.

## 기준과 판정 범위

승인 패키지: `/Users/sangdong/Documents/ChatGPT/Vemo/designs/2026-10-18-android-windows/`의 `START_HERE.txt`, `IMPLEMENTATION_HANDOFF.md`, `tokens.json`, `prototype.js`, `exports/windows-*.png`. Native White A가 기본이다. HTML은 가상 데이터 시연이며 네이티브 실행 증거로 취급하지 않는다. 실제 대상은 이 작업 트리의 `windows/Vemo.App` 및 `Vemo.Core`다.

## 최종 동결 상태

root 통보 기준 App과 테스트는 동결됐고 최종 게시 산출물은 root만 관리한다. 이 갱신에서는 문서만 수정했으며 소스·테스트·게시 폴더를 변경하거나 검사를 다시 실행하지 않았다.

root의 최종 전체 솔루션 검사 보고:

| 프로젝트 | 통과 | 제외 | 실패 |
|---|---:|---:|---:|
| App | 76 | 1 | 0 |
| Core | 118 | 1 | 0 |
| Startup | 17 | 0 | 0 |
| Playback | 2 | 1 | 0 |
| Provision | 12 | 0 | 0 |

이는 root가 수행한 전체 검사 보고다. 본 담당자의 직접 실행 근거는 아래 App 76 통과/1 제외 전체 TRX와 강화된 최소 창·하단 복사 단일 테스트 통과 결과다. 최소 창 노트 viewport 회귀는 해결됐다. 현재 수정 후 시각 근거는 `notes-980x540-fixed.png`(viewport 386×174)이며 나머지 8장은 이전 시점 비교 자료로 보존했다. 최종 스타일 전체를 다시 캡처했다고 주장하지 않는다.

최종 동결본 Windows 네이티브 **77건 검사는 root 실행 예정**이며 아직 결과를 받지 않았다. 앞서 확인한 네이티브 71/71 결과를 이 최종본의 77건 통과로 바꿔 해석하지 않는다. 실제 OAuth, Windows DPI/Narrator·OS 대화상자·전원 전환 검증과 최종 NAS 기록은 해당 root 검증 범위에 남는다.

## 즉시 우선순위

| 우선순위 | 승인 기준 / 현재 근거 | 판정 / 필요한 검증 |
|---|---|---|
| P0 | Settings는 우측 상세 영역에 표시하고 라이브러리·목록·선택을 유지. `MainWindow.OpenSettings`가 `SettingsView`를 상세 host에 추가함 | 기존 OwnedWindow 가정을 우측 패널 계약으로 갱신. 저장·취소·오류·재열기·포커스·선택 보존 실제 UI 테스트 통과 |
| P0 | 빌드 및 네이티브 실행 검증 | 초기 공유 WIP 오류 해소. 최신 직접 App 검사 76 통과/1 제외, 최소 창 회귀 해결. 이전 Windows 네이티브 71/71 확인; 최종 동결본 77건 결과 대기 |
| P1 | 노트의 키워드 필터, 인용 근거 펼침/접기 및 원문 연결 (`IMPLEMENTATION_HANDOFF` §3) | `MainWindow.Results.RenderNotes`는 Markdown 제목/목록을 TextBlock으로 렌더링하지만 키워드·구조화 인용 탐색은 아직 확인되지 않음 |
| P1 | 전사 TXT/MD, 정렬된 경우 SRT/VTT; 노트 MD | `SaveResult`는 전사 TXT / 노트 MD 저장 핸들러만 확인됨. 지원 형식과 정렬 기능은 별도 확인 필요. 지원하지 않는 자막 형식을 노출하지 않는 것은 올바름 |
| P1 | 날짜 미확인은 YYYY-MM-DD 입력·검증, 수정일로 대체 금지 | 현재 입력 안내와 기존 테스트는 시간대를 포함한 ISO 8601. 날짜만 입력하는 승인 흐름과 차이 있음. root 결정: 별도 recordingdate 스키마 없이 자정/시간대를 만들지 않고 현 ISO 계약을 유지; 후속 결정 항목 |
| P1 | 설정 초점·키보드·축소 창에서도 모든 작업에 접근 | 초기 초점 및 호출 버튼 복원 테스트 통과. 980×540 본문 높이 0을 재현한 후 root가 전체 상세 스크롤을 적용; 동일 기준 회귀 통과, 실제 PNG viewport 높이 174 확인 |
| P2 | Native White: sidebar 220, list 310 이상적 너비, 최솟값 980×540; 토큰 색상·타이포그래피 | 최소 창 크기, 220/310 열 너비와 주요 색상은 소스에 일치. 실제 PNG 8장 확인. 승인 PNG에 비해 설정 카드 스타일/간격은 초기 캡처에서 미적용; UI 담당자가 수정 중이므로 최종 픽셀 동등성은 미확인 |

## 현재 반영된 항목과 남은 근거

- 3열 구조, 폴더 컨텍스트 이동, 상시 사이드바 폴더 이동 버튼 제거, 접기/관리 버튼, 접근성 이름 `오디오 파일 가져오기`가 있는 작은 `+`가 소스에 존재한다. 클릭 대상과 자기/하위 폴더 제외 동작은 UI 테스트에서 통과했다.
- `MainWindow.Folders.cs`는 이름 변경·오류 표시·삭제 확인과 녹음/결과 미분류 보존을 연결한다. 폴더 관리 버튼의 클릭 대상/이름 변경, 접기/펼치기, 접힌 부모 드롭 및 컨텍스트 이동/재열기 보존 테스트 통과. 삭제 API의 backend 담당 테스트 결과는 root가 통합한다. 폴더 backend/다른 담당자의 Folder* 테스트는 수정하지 않는다.
- 노트는 `renderedNotes`로 제목·목록을 표시한다. 원본 Markdown은 복사/내보내기에 사용한다. raw TextBox가 읽기 탭에 노출되지 않고 제목 크기 위계가 유지됨을 테스트와 1280×820 PNG에서 확인했다.
- 복사·내보내기·결과 폴더 핸들러가 있다. 전사·노트 복사 결과가 테스트 클립보드에서 저장 원문과 일치했다. 저장 대화상자와 Explorer는 headless만으로 OS 동작을 보증하지 않는다.
- 현재 MainWindow에서 player, 5초 감시 폴더 스캔, AutoExport, RequireExternalPower/Windows API 연결을 확인했다. 980×540 및 1200×720에서 입력 경로·자동 ZIP·전원 설정을 실제 키보드/버튼으로 저장하고 재열기 값 보존을 확인했다. Windows 오디오·실전 전원 전환 검증은 별도다.
- 모델 다운로드/재개, 제목 재생성, 트레이/로그인 실행, 과거 녹음 처리, 인용 기반 seek, Windows 경로 충돌/placeholder, 실기기 DPI 및 Narrator 항목은 완료 증거가 아직 없다. 목록의 존재는 이번 담당자에게 구현 권한을 부여하지 않는다.

## 검증 기록

SDK: `/Users/sangdong/Projects/vemo-publication/.worktrees/notes-drive/build/toolchains/dotnet/dotnet`.

Initial shared-WIP build attempts failed with CS0246, CS8795 and CS1729; these were resolved by the owning workers. Actual execution evidence follows. No HTML proxy, real user data, real authentication or Parsec was used.

## 실제 실행 결과 및 발견한 회귀

1. 초기 `--no-restore` 실행: UI 70 실패/1 제외. NAudio package 추가 후 테스트 자산이 오래되어 `NAudio.Wasapi` 로드가 실패했다. 복원을 포함한 `dotnet test windows/Vemo.App.Tests`로 바로잡았다. 이를 제품 오디오 회귀로 판정하지 않는다.
2. 복원 후: 61 통과/9 실패/1 제외. 고급 Expander를 열지 않은 테스트 2건을 실제 헤더 클릭 helper로 수정했다. 나머지는 제품의 Copy 활성화 시점, ContextMenu 연결 대상 불일치, 설정 종료 포커스, tray 종료 중 창 재표시로 인한 library.lock 유지 문제였다. 제품 수정은 root/해당 소유자가 수행했다.
3. 수정 후 전체: **70 통과/0 실패/1 제외**, 23초. `local-validation/windows-plan-audit/windows-ui-plan-audit-fixed.trx`와 `ui-tests-fixed.log`. 제외는 `NativeInferenceDropTests`의 실제 추론 환경 테스트이며 root가 Windows native 검증을 담당한다. Avalonia 테스트 private API 사용 경고 AVA3001은 기존 테스트 seam 관련이다.
4. 추가 독립 검사: `--filter 'FullyQualifiedName~SettingsConnectionStateTests|FullyQualifiedName~Notes_at_minimum'` 결과 **5 통과/1 실패**. Google UI 5건은 미설정/구성됨/합성 refresh 보유 상태, 요청 취소/오류, 상태/작업 버튼 복구를 검증했다. 메모리 vault와 제어된 transport만 사용했으며 로그인/브라우저/외부 연결은 수행하지 않았다. DPAPI/실제 OAuth 증거를 대신하지 않는다.
5. 남은 확정 회귀: `NoteResultsTests.Notes_at_minimum_window_size_keep_a_readable_scroll_viewport`는 980×540에서 `Viewport.Height == 0`으로 실패한다. 같은 합성 노트가 1280×820에서는 제목/목록/출처로 보인다. 실패 테스트는 기준을 낮추거나 skip하지 않고 보존했다. root에 레이아웃 수정을 전달했다.
6. root가 Windows PC에서 실행한 별도 네이티브 검사: **71/71 통과, 제외 0**, 보고된 실행 시간 98.454초. 전달된 `local-validation/windows-plan-audit/native-plan.trx`를 직접 읽어 total/executed/passed 71, failed/error/notExecuted 0을 확인했다. root 보고상 실제 M4A drop → background engine → 전사/노트 → 중첩 폴더 ZIP을 포함한다. 이 담당자가 해당 PC 작업을 직접 수행한 것은 아니며 추가 Google UI 5건과 최소 창 노트 회귀는 이 네이티브 스냅샷 이후다. ETag backend 변경 후 최종 빠른 UI 재검증을 별도로 실행한다.
7. ETag backend 변경 후 최종 빠른 UI 전체: **75 통과/1 실패/1 제외, 총 77건, 24초**. `windows-ui-plan-audit-final.trx`, `ui-tests-final.log`. 유일한 실패는 위 최소 창 노트 높이 0이며 Google UI 5건 및 기존 70건은 모두 통과했다. 당시 남은 레이아웃 문제는 아래 8번 수정 검증으로 해소됐다.
8. 최소 창 수정 후 전체 재검증: **76 통과/0 실패/1 제외, 총 77건, 26초**. 최신 전체 근거는 `windows-ui-plan-audit-viewport-fixed.trx`, `ui-tests-viewport-fixed.log`다. root가 상세를 외부 ScrollViewer로 감싸고 본문 높이를 최소 600으로 확보했다. 기존 `Viewport.Height >= 60` 기준을 그대로 유지한 단일 회귀도 통과했다(`minimum-notes-fixed.trx`). 이후 작은 창에서 하단 복사 버튼을 `BringIntoView`로 실제 스크롤해 좌표가 창 안에 위치하는지 및 원문 복사까지 검사하도록 강화했고 **1/1 통과**했다(`minimum-notes-footer-fixed.trx`, 487 ms).

수정 후 실제 화면 1장: `notes-980x540-fixed.png`. 같은 실제 Avalonia/Skia 실행기의 `--minimum-notes-only` 경로에서 저장했고 **viewport 386×174**, 제목/목록 표시를 직접 확인했다. 외부 스크롤로 상단 제목이 화면 밖으로 이동하는 정상 상태이며 목록·라이브러리는 유지된다. 실행기는 `await using` teardown까지 **exit 0**으로 끝났다. 기존 8장을 다시 생성하거나 덮어쓰지 않았다. 새 화면 SHA-256: `cb66f2bdc1227095e4794f98c7f279c82e30c717fd700ffb7fd3a24f8576fcd7`.

## 실제 Avalonia 캡처

실행기: `local-validation/windows-plan-audit-render/Program.cs`, 프로젝트 `Render.csproj` (로컬 산출물, Git 제외). `AppBuilder.Configure<VemoApplication>().UseSkia().UseHeadless(new() { UseHeadlessDrawing = false })`, 실제 MainWindow/SettingsView와 합성 UiFixture 데이터, `CaptureRenderedFrame` 사용. HTML 프록시가 아니다.

```sh
/Users/sangdong/Projects/vemo-publication/.worktrees/notes-drive/build/toolchains/dotnet/dotnet run --project local-validation/windows-plan-audit-render/Render.csproj -- local-validation/windows-plan-audit
```

`local-validation/windows-plan-audit/`에 `main-1280x820.png`, `notes-1280x820.png`, `settings-1280x820.png`, `settings-lower-1280x820.png` 및 대응하는 `*-980x540.png` **8장**을 저장했다. 메인·설정 및 작은 창의 노트/설정 하단을 직접 확인했다. 설정과 라이브러리/목록이 함께 표시되고 작은 창 설정의 저장 버튼은 고정돼 접근 가능했다. 노트의 0 높이 현상은 실제 PNG와 위 회귀 테스트가 일치한다.

승인 `exports/windows-settings.png`, `windows-notes.png`를 직접 열어 구조·토큰·위계를 비교했다. 3열/라벤더 선택/작은 +/노트 제목은 대응하지만 키워드/근거 탐색, 편집용 날짜 입력 상시 노출, 탭/세부 조작의 밀도는 차이가 있다. 글꼴·OS 크롬·Windows DPI는 macOS Skia headless로 인증하지 않는다. 초기 설정 캡처는 파생 SettingsView 스타일 selector 수정 전이므로 최종 스타일 증거로 확대 해석하지 않는다.

The renderer saved all eight frames, then its synchronous HeadlessUnitTestSession teardown left the process waiting. The precise internal wait was not traced. Only this worker's renderer processes were terminated; this is separate from native application shutdown. Changing the local harness to `await using` and running `--lifetime-only` produced the dispatch-complete message and exit 0. Full frame generation was not repeated. SHA-256 values are retained in `local-validation/windows-plan-audit/screenshots.sha256`.

## 인계 및 제한

수정 소유 파일: `docs/WINDOWS_PLAN_AUDIT.md`, `windows/Vemo.App.Tests/MainWindowTests.cs`, `AdditionalActionTests.cs`, 새 `SettingsTestInput.cs`, `NoteResultsTests.cs`, `SettingsConnectionStateTests.cs`. root 소유 `EmbeddedSettingsTests.cs` 및 Google*/Folder*/Playback* 테스트/제품 소스는 수정하지 않았다. publish/설치/인증은 root 소유다. 날짜 ISO는 임의 자정·시간대 변환 없이 유지한다. 제목 재생성/모델 다운로드/트레이 후속 구현은 동시 작업이므로 최종 상태를 root가 갱신한다.

NAS 별도 기록은 작성하지 않았다. 작업자용 근거는 이 문서와 로컬 로그/PNG에 있으며 root가 최종 판단과 NAS 작업 기록을 담당한다.

## Final root verification / installation

Final frozen Windows UI run: 77/77 passed, zero failed/skipped, 91.027 seconds. Root parsed native-final-plan.trx Counters. This supersedes the pending final-run statement above. Native Core: 119 passed / 1 platform skip, including native DPAPI success.

Installed 0.1.5-google-native-white side-by-side; Desktop Vemo.lnk target and icon read back. App/Core hashes match final tested binaries. Running 0.1.4-icon and real library preserved; quit old app and reopen desktop shortcut. Real account OAuth / phone roundtrip remains unverified. OAuth publishes result.json, not ZIP. Other parity gaps remain as listed.

## 후속: 원클릭 Google 연결 / 시안 세부 대조

일반 사용자에게 OAuth JSON 발급을 요구하던 흐름을 제거했다. 개발자가 Desktop 클라이언트를 배포 빌드에 제공하고, 새 설치에서는 Google Drive 연결 → 브라우저 로그인/동의 → 폴더 선택 순서로 진행한다. 개인 계정 토큰은 포함하지 않는다. Google 프로젝트의 공개 게시/테스트 사용자 설정은 개발자의 책임이며 실제 승인은 별도다.

승인 A Native White와 대조해 Segoe UI/Malgun Gothic, 기본/행 글꼴 크기,18px 브랜드,19px 벡터 아이콘과24단위 viewport,1.6 선 두께, 폴더 count/메뉴 간격과32px 메뉴 영역, 하단 설정 톱니/정렬, 상단 가져오기/설정, 탭 아이콘, 검색 아이콘, 재생 아이콘/트랙을 수정했다. 설정은58px 상단/25px 제목/32px 본문 여백, 간결한 연결 카드와 접힌 고급 옵션을 사용한다. 실제 지원하지 않는 시안 기능을 가짜로 활성화하지 않는다. 제목 한 줄은 이전 사용자 지시를 우선했다.

원본 시안은 디자인 토큰과 화면 구조의 기준이다. OS 제목 표시줄, 데이터별 오류/상태, 실제 기능의 추가 컨트롤까지 픽셀 단위로 동일하다고 주장하지 않는다.

후속 검증: 로컬 App80통과/1실제추론옵트인제외, Core135통과/1DPAPI제외, Startup17통과, Playback2통과/1Windows제외, Provision12통과. Windows 배포 구성의 첫 실행 검증에서 신규 연결 버튼 활성화/계정 미연결/토큰 미저장을 통과했다. 최초 네이티브 실행81건 중80통과/1실패: 실패는 미설정 테스트가 배포 리소스를 자동 발견한 테스트 격리 문제였다. 이 실행의 실제 M4A 전사·노트·중첩 ZIP 검사는 통과했다. 미설정 테스트에 명시적 null provider를 주입하고, 동일 제품DLL로 최종 Windows80통과/1실제추론옵트인제외(22.533초)를 확인했다. Windows Skia 화면8장을1280×820/980×540으로 저장했으며 한글 U+AC00 fallback은 Malgun Gothic으로 확인했다. 실제 계정 승인/클라우드 왕복 및 물리 DPI 확인은 미완료다.
