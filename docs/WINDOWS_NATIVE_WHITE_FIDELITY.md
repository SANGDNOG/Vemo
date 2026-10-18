# Windows A Native White 적용 교정

2026-10-18. 사용자 피드백은 기능 추가가 아니라 전달한 A Native White 시안을 그대로 따르라는 요청이었다. 앞선 0.1.8은 전사에 별도 보라색 카드를 만들고 Fluent 밑줄 탭을 유지해 시안과 달랐다. 이 문서는 이번 교정의 범위와 검증 한계를 남긴다.

## 적용 기준

원본 `designs/2026-10-18-android-windows/START_HERE.txt`, `IMPLEMENTATION_HANDOFF.md`, `prototype.css`, `tokens.json`, `exports/windows-detail.png`, `windows-processing.png`, `windows-settings.png`를 대조했다. 원본은 변경하지 않았다. CSS의 실제 A Native White 렌더를 치수 기준으로 삼았다. 예를 들어 tokens의 목록 310과 실제 시안 CSS의300이 달라 시안과 같은300을 사용한다. Windows용 글꼴은 명세의 Segoe UI/맑은 고딕을 유지한다.

## 교정 사항

- Sidebar220 / 목록300 / 나머지상세. 상세 상단58, 제목 x552/y85, 재생 바 x552/y210/높이46(1280client기준).
- 전사는 흰 바탕+윗구분선1, 세로여백19, 본문13/시각10, 시각과본문간격19. 보라색 둥근 카드를 제거했다. 같은 실제 시작시각에 속한 짧은 문장은 문단으로 묶고 표시 끝의 줄바꿈만 제거한다. 저장된 전사·타임라인·ZIP은 변경하지 않는다.
- 전사/노트는 회색 둥근 그룹과 연보라 선택영역으로 표시한다. 하단은 복사/내보내기/결과폴더3버튼, ZIP/이동은 실제 기존 메뉴를 유지한다.
- 상세의 제목→녹음일→보라색 폴더경로→재생기 순서. 엔진 이름과 저장경로 상시문구는 정상화면에서 제거하고 오류는 보존한다.
- 중앙 처리화면과 상단 실제 진행/경과시간. 이미 생성된 전사·노트는 작업 중에도 읽을 수 있으며 새작업중지/재개가 실제 큐 정책과 연결된다.
- 설정의 입력출력→Google→모델 순서,13반경의흰그룹/행구분선/12제목·10보조글자. 실제 OAuth 연결과 모델설정은 유지한다.
- 굵은 한글 제목이 정상굵기로 fallback되는 Windows 실제 텍스트런을 확인했다. 공통 AppBuilder에 명시적 맑은 고딕 fallback을 추가해 `Malgun Gothic:Normal`이던 DemiBold 요청이 `Malgun Gothic:Bold`로 렌더되는 것을 확인했다. 영어는 Segoe UI Semibold 유지.
- 작은 창은 세로여백을 줄이며 오류내용은 제한된 스크롤로 표시한다. 노트본문과 하단복사접근을 유지한다.

## 보존·한계

제목날짜접미사·한줄목록·실제녹음길이·동적오류·OAuth와지원모델은 정적시안의샘플값으로대체하지않았다. Mac/Android 작업트리·Core/라이브러리·계정데이터는 수정하지 않는다. 브라우저의 macOS 글꼴과 Windows 맑은 고딕은 글자폭/줄바꿈/래스터가 다르다. 최종 실제 UI 측정에서 제목시작85, 재생기시작210, 탭시작278, 본문영역시작320, 첫구분선349는 원본 시안과 같았다. 글꼴의 글자폭·줄바꿈과 실제 데이터 차이까지 픽셀단위완전동일이라고 주장하지 않는다.

`local-validation/windows-design-fidelity/`에 원본 브라우저렌더/측정JSON, Windows Skia/headless실제앱렌더, 글꼴텍스트런, 테스트결과를 보관한다. 물리모니터/DPI와사용자수동승인은 별도이다.

## 최종 검증과 설치

Windows 최종 App104통과/실패0/skip0(89.290초), 실제 합성한국어 음성→whisper→llama→ZIP 재검증 포함. 테스트·렌더·배포 App DLL SHA256 `962e0836638eba9a17385d783062e4b9af107a7af637d7983d4e5529eac10eca` 동일. 설치 `%LOCALAPPDATA%\Programs\Vemo\0.1.9-native-white\Vemo.App.exe`, 바탕화면Vemo.lnk target/icon/hash readback확인. 기존0.1.8프로세스유지,완전종료후재실행필요. 이전버전보존. 실제계정OAuth재승인/클라우드왕복은이번UI교정에서다시하지않았다.
