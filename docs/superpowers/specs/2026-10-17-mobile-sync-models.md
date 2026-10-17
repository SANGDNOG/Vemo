# Vemo 모바일 연결과 기기별 모델 선택

사용자는 Android Vemo에서 녹음하고 선택한 Nextcloud 저장소를 통해 데스크톱과 전사·노트를 주고받기를 승인했다. Mac과 Windows 모두 모델을 선택한다. 첫 검증 PC는 9800X3D/64GB/RTX5070Ti 16GB, Android는 최신 안정 OS 에뮬레이터(실기기는 Galaxy S22 Ultra, 아직 준비 안 됨)다. 승인된 개발 순서는 공통 규칙과 모델 선택, Android 녹음/PC 처리/결과 열람, Android 자체 AI 처리다.

## 이번 연결 단계

- 기존 Mac SwiftUI 앱, 데이터, 안정 서명과 MLX 엔진을 보존한다. 기존 분리 worktree에서 작업하며 설치 앱을 임의 종료하지 않는다.
- Android 네이티브 Kotlin 앱: 보라색 포인트, 녹음 목록/상세/설정, 마이크 권한, foreground 녹음, 일시정지·재개·완료, 앱 내부 내구성 저장, 사용자가 선택한 SAF 폴더로 복사, Nextcloud HTTPS WebDAV 업로드와 결과 내려받기.
- 녹음만/PC 자동 처리 모드를 제공한다. 폰 자체 AI는 후속 단계이며 동작하지 않는 버튼을 동작하는 기능처럼 제공하지 않는다.
- 폰에서 완료한 녹음만 업로드한다. 일시정지는 완료가 아니다. 앱/업로드 중단 시 로컬 원본을 유지하고 재시도한다. 권한 취소·용량 부족·서버 인증 오류는 각각 확인 가능한 상태로 남긴다.
- 사용자가 고른 폴더는 SAF의 기기 폴더다. 공급자 URI도 시스템이 허용할 수 있으나 클라우드 연결은 별도 설정이다. 녹음은 앱 내부에 먼저 안전하게 저장한다.
- Mac에는 별도 모바일 동기화 폴더 선택과 연결 상태를 추가한다. Nextcloud 데스크톱 앱이 동기화하는 폴더를 선택하며 기존 음성 메모/Drive 입력 기능과 병행한다. 서버 도착 상태를 로컬 파일 저장 상태와 혼동하지 않는다.

## 공통 동기화 계약 v1

클라우드는 SQLite/실행 중 파일 대신 완료한 파일과 작은 JSON을 보관한다. 모든 UUID는 소문자 표준 UUID, SHA256은 소문자 64자리 hex다. 폴더명/제목을 경로 또는 명령어로 사용하지 않는다.

`processor.json`: `{schema_version:1,processor_id:<uuid>,display_name:<string>}`. 한 root에 지정 처리기 1대. 기존 다른 처리기의 파일을 자동 덮어쓰지 않는다. 폰이 이를 읽고 아래 요청에 명시한다. Mac용 ID는 로컬에 지속 저장한다.

`recordings/<recording_id>/<audio_sha256>/audio.m4a`와 `recording.json`. JSON:

```json
{"schema_version":1,"recording_id":"uuid","audio_sha256":"64hex","audio_bytes":123,"audio_file":"audio.m4a","recorded_at":"2026-10-17T17:00:00+09:00","title":"녹음","mode":"desktop","target_processor_id":"uuid","completed":true}
```

`mode`는 `record_only` 또는 `desktop`; 전자는 target=null 허용, PC 자동 처리 안 함. 오디오는 최종 저장·해시 확정 후 먼저 PUT하고 JSON을 마지막 PUT한다. PUT 완료 전 원격 완료 표시 안 함. 동일 경로의 재시도는 동일 콘텐츠만 허용하며 충돌은 보존/표시한다. 데스크톱은 JSON 도착만 신뢰하지 않고 크기·SHA256·오디오 디코딩을 확인한다. 앱의 일시정지 파일과 부분 업로드는 수집 안 함. 한 항목 오류는 다른 항목을 막지 않는다.

`results/<recording_id>/<audio_sha256>/result.json`:

```json
{"schema_version":1,"recording_id":"uuid","audio_sha256":"64hex","processor_id":"uuid","title":"생성 제목 — 2026-10-17","recorded_at":"2026-10-17T17:00:00+09:00","transcript":"전사 원문","notes_markdown":"상세 노트"}
```

JSON은 UTF-8, 최대 8MiB. 폰은 자신의 완료 녹음 ID·오디오 해시·지정 처리기와 일치하는 결과만 표시한다. 데스크톱은 전사와 노트가 완료된 뒤 원자적으로 결과 파일을 게시한다. 폰 UI는 결과를 수신하기 전까지 대기 상태를 표시한다. 재시도/재시작에서 중복 가져오기를 막는 로컬 매핑을 유지하고, 지운 항목을 자동 복구하지 않는다. 원본 녹음 날짜/시간대를 보존한다. v1에서는 클라우드 전체 삭제와 양방향 제목/폴더 동시편집을 제공하지 않는다. ZIP은 동기화와 분리해 기존 3개 파일 규칙과 폴더 경로를 유지한다.

## 모델 선택

- 전사/노트 용도를 구분한 중앙 카탈로그: 고정 revision, backend, 기기 지원, 예상 다운로드/메모리, 품질 검증 상태. 모델 후보와 실제 제공 모델을 구분한다.
- 기존 설치 모델과 경로를 기본값으로 이관하여 재다운로드하지 않는다. 작업에 사용한 모델 선택을 고정해 설정 변경이 진행 중 작업의 중간 모델을 바꾸지 않게 한다.
- Mac에서는 현재 지원 MLX 모델의 검증 가능한 대안을 제공한다. 미설치 모델을 선택해도 추론 중 자동 다운로드하지 않으며 명시적 다운로드/준비 상태를 표시한다. 기존 결과는 자동 재생성하지 않는다.
- RAM과 GPU VRAM을 구분한다. 자동 추천은 보수적이며 추정치임을 표시한다. Windows/Android backend는 실제 구현·검증 전 지원된다고 표시하지 않는다. 27B 노트는 5070Ti 16GB의 긴 문맥 GPU 단독 실행을 보장하지 않는다.

## 이후 단계와 검증 경계

Windows는 native 배포를 목표로 Python POSIX 잠금/프로세스와 MLX backend를 분리하고 CUDA/CPU backend 및 UI를 연결한다. WSL SSH는 엔진 검증, Parsec은 실제 Windows 앱 검증에 쓴다. Android 자체 ASR/노트는 모바일 runtime과 한국어 품질을 별도로 검증한다. 이번 단계에서 두 기능의 완성을 주장하지 않는다.

실제 개인 녹음/서버에 접근하지 않는 synthetic 테스트를 기본으로 한다. 사용자 Nextcloud 테스트 폴더 동의와 앱 내 로그인 이전에는 테스트 서버/로컬 폴더만 사용한다. credentials는 Android Keystore 기반 보호 저장소에 보관하고 로그/코드/클라우드에 넣지 않는다. HTTPS 인증서를 검증하고 cross-origin redirect로 인증을 유출하지 않는다. 큰 오디오는 streaming 전송한다. 원격 입력은 크기·경로·형식 검증을 거친다.

필수 검증: 녹음 pause/resume/stop; offline 완료와 재시도; 잘린 업로드; 오디오 해시 불일치; 잘못된 날짜/UUID/경로; stale 결과; 다른 처리기; 같은 파일 재검색; 기존 Mac 모델/작업/노트 회귀; Android build/lint/unit 및 가능한 에뮬레이터 검사. 실기기·실제 서버·Windows 미검증은 분리해 기록한다.
