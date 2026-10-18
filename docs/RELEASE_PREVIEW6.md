# Vemo v0.2.1-preview.6

## 다운로드와 실행

[GitHub 릴리스](https://github.com/SANGDNOG/Vemo/releases/tag/v0.2.1-preview.6)에서 플랫폼에 맞는 파일을 받으세요. 모델과 개인 녹음은 포함하지 않습니다. SHA256SUMS.txt로 다운로드 파일을 확인할 수 있습니다.

- **Mac Apple Silicon:** `Vemo-0.2.1-preview.6-mac-arm64.dmg`. Vemo.app을 Applications로 옮기세요. macOS 26.2 이상이며 공증되지 않은 프리뷰입니다. 차단되면 시스템 설정 → 개인정보 보호 및 보안에서 해당 앱의 열기를 허용하세요. 음성 메모 직접 연동은 전체 디스크 접근이 필요하며 파일 가져오기도 가능합니다. 개인용으로 설치된 개발판과 공개판의 앱 식별자는 다릅니다.
- **Windows x64:** `Vemo-0.1.20-preview-win-x64.zip`. 전체 압축을 풀고 `Vemo.App.exe`를 실행하세요. .NET 런타임은 포함되어 있습니다. 엔진과 모델은 앱 설정에서 준비하며 지원되는 NVIDIA GPU는 자동 선택합니다. 서명 없는 프리뷰이며 Windows ARM64 배포는 이번 릴리스에 포함하지 않습니다.
- **Android:** `Vemo-0.1.19-preview-android.apk`. Android 8 이상에 직접 설치하는 개발용 서명 APK입니다. 검증 환경은 Android API36 에뮬레이터이며 Galaxy 실기기 검증은 남아 있습니다. 기존 앱과 서명이 다르면 덮어설치할 수 없습니다. 이 경우 녹음을 백업하기 전에 기존 앱을 지우지 마세요.

## Google Drive

앱 설정에서 Google 로그인 후 같은 처리기의 연결 폴더를 선택합니다. 일반 사용자가 OAuth JSON을 발급할 필요는 없습니다. 배포 앱에는 네이티브 앱용 클라이언트 구성만 포함하고 사용자 로그인 토큰은 포함하지 않습니다. Google 프로젝트의 테스트 사용자 제한 때문에 모든 Google 계정의 로그인을 보장하지 않습니다. 로그인할 수 없으면 로컬 파일 가져오기 또는 지원되는 동기화 폴더 방식을 이용하세요. 관련 설정은 [Windows Google 안내](WINDOWS_GOOGLE_SETUP.md)를 참고하세요.

## 화자 분리

전사 완료 후 화자 분석을 실행하고 이름을 편집할 수 있습니다. Mac·Windows에서 로컬 CPU2스레드로 처리하고 Android에는 결과와 수정 이름을 전달합니다. 60분 이하, 시작 시 가용 메모리4GiB 이상을 지원하며 모델 약46MB를 별도로 다운로드합니다. 전사·노트와 동시 실행하지 않습니다. 인원수를 알면 1~20명 중 지정하세요. 자동 추정은 실험 기능이며 긴 강의에서 과분리될 수 있습니다. 모호한 문장은 미지정으로 남깁니다. 재분석하면 이름이 초기화됩니다.

## 검증과 한계

Mac 실제 화면에서 분석·한글 이름 수정·재시작 유지 확인. Windows 실제 helper→Drive→Android 파일 수신과 수정 이름11개검증 통과, 기존 전사/노트/원음/ZIP8개검증 통과. Windows Core437/App128+1선택제외/Provision24, Android89, Python화자48, SwiftNativeChecks579 통과. Windows 설치 앱의 화자 조작 화면과 Android 화자 표시의 시각 검증은 남아 있습니다. 다른 실제 Mac/Windows와 Galaxy 실기기 검증, 화자 정답 기준 정확도 평가도 미완료입니다.

긴 전사 반복은 rolling context를 비활성화하고 원음 재전사로 개선했습니다. 개선된 반복 횟수는 완전한 전사 정확도를 보장하지 않습니다. 원본·기존 결과를 보존하며 ZIP은 오디오·전사·요약노트3개 파일을 유지합니다.
