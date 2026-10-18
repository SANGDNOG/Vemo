# Google OAuth 구현 계획

승인된 후속 작업을 기존 worktree에서 이어간다. Root가 설계·등록·통합을 맡고 지정 Team1/Team2 역할을 재사용한다. 작업자는 아래 파일 범위만 수정하며 재귀 위임하지 않는다.

**목표:** Android와 Mac에서 같은 Google 계정의 Vemo 전용 폴더를 통해 녹음과 처리 결과를 주고받는다.
**설계:** `docs/superpowers/specs/2026-10-17-google-oauth.md`

## Task 1: Android (implementation_deep)

소유: `android/**`. AuthorizationClient 기반 연결/해제/재승인 UI, Drive REST CloudStorage, 계정·root 선택, 기존 Nextcloud 선택 유지. Token UI resolution은 foreground에서만, 동기화 스레드에서 사용자 화면을 강제로 열지 않는다. 단위 mock HTTP로 목록/계정/root/재시도/충돌/만료를 검사하고 build/lint를 실행한다. 사용자 프리뷰 중 emulator 설치·조작 금지.

## Task 2: Mac OAuth와 Drive mirror (implementation_deep)

소유: 신규 NativeCore `Google*.swift`, 해당 신규 tests만. 기존 MobileSyncBroker와 AppModel은 root 소유. PKCE/loopback/Keychain, 고정 Google endpoints, Drive root 생성/목록/연결, streaming 다운로드와 결과 게시를 구현한다. injectable transport/credentials로 네트워크·토큰 없이 검사 가능하게 만든다. Actor API를 먼저 root와 공유한다. client configuration은 외부 공개 설정을 받으며 사용자 token은 repo에 넣지 않는다.

## Task 3: Root 통합

소유: AppModel/SettingsView 및 신규 Google 설정 view, 문서/공개 client 설정, 등록 화면. Google cache를 기존 mobile broker에 연결하고 pull→기존 import/process→result push 순서를 보장한다. 계정/root가 바뀌면 이전 경로와 job binding을 보존한다. 동기화 실패 상태와 재시도를 표시한다. 기존 앱 자동 교체 금지.

## Task 4: 독립 검증 (test_debug_deep)

위험 초점: OAuth state/nonce/redirect/token 유출, 계정 변경/처리기 충돌, 같은 이름 파일/lost ACK, 취소/재시작, cache 파일 symlink/크기/해시. Mock 성공과 실제 계정 성공을 분리한다. 기존 Mac/Python 회귀는 영향 범위에 따라 실행하며 변화 없는 전체 검사를 반복하지 않는다.

## Task 5: 실제 등록·검증과 저장

Cloud project `vemo-511910`에서 Drive API 및 동의 화면/Android+Desktop client를 준비한다. 로그인과 동의는 사용자에게 맡긴다. 필요한 사용자 입력이 없어도 mock/code 작업을 진행한다. 실제 양 플랫폼 연결은 별도 테스트 폴더·합성 파일로만 검사한다. 소스 checkpoint, 프리뷰 산출물, NAS 작업 기록과 남은 Windows/모바일 AI 사항을 남긴다.

Windows는 POSIX 파일/프로세스/pipe/오디오 의존성과 MLX backend를 분리해야 한다. 실제 PC 없이 Windows 실행 성공을 주장하지 않는다. Android 자체 AI는 Galaxy 메모리/발열/한국어 품질에 맞는 별도 검증이 필요하다.
