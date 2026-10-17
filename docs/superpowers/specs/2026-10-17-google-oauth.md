# Vemo Google OAuth 연결

사용자는 Google OAuth 등록과 자동 왕복 구현으로 방향을 변경하고 후속 작업을 승인했다. 전용 Cloud 프로젝트 `vemo-511910`을 생성했다. 기존 앱·데이터·Nextcloud·Drive 폴더 기능은 보존한다. 사용자가 프리뷰를 보는 동안 에뮬레이터 설치·재시작을 하지 않는다.

## 목표와 권한

Android 완료 녹음 → Google Drive 앱 폴더 → Mac 전사·노트 → 폰 결과 표시. Windows와 Android 자체 AI는 별도 후속 단계다.

동일 Cloud 프로젝트에 Android `com.vemo.android`와 Desktop OAuth client를 등록한다. Drive scope는 `drive.file`만 사용하고 계정 식별은 Drive `about.user.permissionId`로 얻는다. 전체 Drive 권한이나 서비스 계정은 사용하지 않는다. Desktop은 시스템 브라우저, loopback 임시 포트, PKCE S256, state 검증, 취소·시간 제한을 사용한다. Android는 AuthorizationClient와 UI resolution을 사용한다. Token·code·비밀 URL은 로그에 남기지 않는다. Desktop refresh token은 Keychain, Android access token은 메모리와 SDK 재승인으로 관리한다. Client ID는 공개 설정이며 네이티브 앱 client secret은 기밀 보장 수단이 아니다.

## 연결과 데이터

기존 exact v1 marker/result JSON을 유지한다. Desktop이 앱 전용 root를 생성해 처리기를 등록하고 phone은 해당 root를 명시적으로 선택한다. Account permissionId·Drive root ID·processorId를 binding으로 저장한다. 임의 기존 폴더 자식 접근을 보장하지 않는다. 파일은 Drive ID·parent ID·Vemo metadata로 식별하고 이름만 같다고 사용하지 않는다. 중복 후보·계정 불일치·처리기 충돌은 보존하고 오류를 표시한다.

Audio 먼저, 완료 marker 마지막. 일시정지는 업로드하지 않는다. 원격 삭제는 없다. Lost ACK와 재시도에 안정적인 identity를 사용한다. Desktop의 전용 local cache는 기존 broker로 검증·전달하고 계정/root마다 분리한다. JSON8MiB/audio16GiB, streaming·hash·no symlink·pagination 제한·HTTPS host/redirect 제한·취소를 적용한다. 로컬 저장과 서버 업로드 완료를 구분한다.

## 검증과 배포

### Drive metadata 계약

일반 Drive `properties`를 사용한다. Root folder에는 `vemoProtocol=1`, `vemoKind=root`, `vemoProcessor=<uuid>`를 생성 시 고정한다. 하위 항목에는 `vemoProtocol=1`, `vemoKey=sha256(UTF8(root-relative POSIX path))`를 붙인다. 전체 path를 property에 넣지 않는다(Drive property 길이 제한). 폴더 hierarchy와 leaf name은 기존 파일 계약과 같다. Parent ID·MIME·metadata가 모두 맞아야 사용한다. Root는 Desktop에서 생성하고 Android에서는 목록 중 선택한다. 동일 이름 후보가 복수면 자동 선택하지 않는다.

OAuth state/code 오류·중복·취소·만료, refresh 오류, Drive 중복/부모/계정/processor 불일치, 재시도·401·403·부분 업로드를 합성 검사한다. 동일 Cloud 프로젝트의 양 플랫폼 drive.file 공유 접근은 실제 계정으로 확인하기 전 가정으로 표시한다. 실서버 시험은 전용 synthetic 폴더와 녹음만 사용하며 기존 사용자 파일을 수집하지 않는다.

기존 설치 Mac build23은 유지한다. 외부 테스트 상태부터 등록하고 정식 공개 전에 브랜드·홈페이지·개인정보처리방침·release 서명을 준비한다. 결제 계정 연결과 추가 Cloud 서비스 생성은 하지 않는다.
