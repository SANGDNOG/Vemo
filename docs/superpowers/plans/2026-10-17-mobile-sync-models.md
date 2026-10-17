# Vemo 모바일 연결 및 모델 선택 구현 계획

> Workers execute only their assigned slice; no recursive delegation. Use the configured Team1 implementation and Team2 test_debug roles. Root integrates and adjudicates.

**Goal:** Android 녹음과 Mac 처리 결과를 선택한 Nextcloud 폴더로 연결하고 모델 선택을 확장한다.
**Architecture:** 기존 Mac 앱을 유지하고 native 모바일 동기화 broker를 추가한다. Android Kotlin 앱은 완료 파일과 버전 명시 JSON을 WebDAV로 교환한다. AI 모델 선택은 Python 엔진의 명시적 카탈로그/정책으로 관리한다.
**Tech Stack:** SwiftUI/Foundation, Python/MLX, Kotlin/Android, HTTPS WebDAV.
**Spec:** `docs/superpowers/specs/2026-10-17-mobile-sync-models.md`

## Constraints / review focus

- 실사용 state와 source 원본을 변경하지 않고 synthetic 검증한다. UUID/SHA256/완료 마커를 교차 검증한다.
- 네트워크 전송은 TLS 검증·streaming·크기 제한·인증 비노출을 적용한다.
- 등록 처리기 불일치, 부분 업로드, 재시작, 삭제된 항목의 재등장, stale 노트를 중점 검증한다.
- 기존 모델 폴더 및 결과 유지. 모델 변경 중 작업을 섞지 않는다.
- 설치 앱과 공개 DMG는 검사 완료 전 교체하지 않는다. Windows·모바일 AI는 별도 단계다.

## Task 1 — Android companion (Team1 implementation_complex)

Files: `android/**` only. Root provides JDK/SDK; worker owns Gradle build and application sources/tests.
Interface: spec의 `processor.json`, `recording.json`, `result.json`을 정확히 사용.
- [ ] 먼저 plain Kotlin 단위 검사로 완료/일시정지/재시도 및 결과 identity 거부 사례를 고정한다.
- [ ] foreground 녹음, SAF 폴더 복사, 로컬 메타데이터, Keystore credentials, WebDAV 전송과 결과 UI를 구현한다.
- [ ] Android unit test/lint/debug APK 빌드; 도구 한계는 보고한다.

## Task 2 — Mac mobile broker (Team1 implementation_deep)

Files: 신규 `macos/Sources/NativeCore/MobileSync*.swift`, 신규 native tests, 필요 시 `ImportBroker.swift`의 metadata-aware import만. AppModel/SettingsView는 root 소유.
Interface: broker는 처리기 등록, 검증된 pending memos, 결과 게시에 대한 actor API를 제공. 저장 위치와 job 매핑은 로컬 state에 보관한다.
- [ ] synthetic audio/JSON으로 partial/conflict/replay/wrong-processor/date validation 검사를 먼저 만든다.
- [ ] 안전한 broker와 기존 import 연계를 구현하고 native unit tests를 실행한다.
- [ ] root에게 실제 API/와이어 매핑을 전달한다.

## Task 3 — Model selection (Team1 implementation_complex)

Files: `src/voice_memo_transcriber/{models,download_models,engine,worker,study_notes}.py`, 신규 model catalog/policy 모듈, 관련 Python tests. Swift UI는 root 소유.
Interface: `model_catalog`/`model_selection` engine status 및 `set_models` command, 명시적 선택 모델 다운로드. 필드/오류는 root에 전달한다.
- [ ] 기존 default·경로 유지와 지원하지 않는 runtime/미설치 선택/진행 중 작업 검사를 먼저 작성한다.
- [ ] 고정 revision의 검증 가능한 MLX 선택 후보와 안전한 migration을 구현한다.
- [ ] 설정 변경은 진행 중 generation과 분리, 완료 결과 보존을 검증한다.

## Task 4 — Root integration

Files: Mac AppModel/SettingsView 및 신규 UI, docs, build setup.
- [ ] 모바일 폴더 선택·등록·scan·기존 import delivery·완료 결과 게시를 연결한다.
- [ ] 전사/노트 모델 선택·현재 기기 정보·추천 근거·다운로드 상태를 UI에 연결한다.
- [ ] Nextcloud 개발 서버 또는 계약 fixture로 Android→Mac→Android 왕복 검사한다.

## Task 5 — Independent validation and handoff

- [ ] Team2 test_debug_complex: 계약 interoperability·실패 사례·회귀 테스트 독립 실행.
- [ ] 필요한 범위 Gemini read-only review 및 수정 후 재검증.
- [ ] 빌드 산출물과 설치 안내 작성, 불필요한 중간 파일만 정리.
- [ ] NAS 근거 기록, Git checkpoint, 남은 실기기/서버/Windows 검증 명시.

## 후속 단계 (이번 연결 milestone과 별도)

Windows native runtime/UI 및 배포 → Windows PC 검증 → Android 기기별 로컬 AI와 결과 작성 권한 확장. 각 단계의 구현/검증 상태를 완료로 오인하지 않도록 별도 기록한다.
