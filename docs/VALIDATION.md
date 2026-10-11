# Public preview validation

Release: `v0.1.0-preview.1`. Checks performed on an Apple Silicon M5 Pro, 24 GB RAM, macOS 27.0.1. This is evidence from one Mac, not certification of every supported system.

## Automated checks

- Python engine: **51 tests passed** using Python 3.12.15 with the pinned dependencies. The MLX-call regression uses a mock; real inference is checked separately below.
- Native UI/protocol/source/publish/playback: **110 assertions passed** with `swift run --package-path macos NativeChecks --broker-checks`.
- New runtime/audio behavior: **27 assertions passed** with `--runtime-audio-checks`: bundled path resolution after moving a bundle into a path with spaces, developer fallback, reuse of legacy state without moving it, stereo/downmix/resampling, duration, WAV/AAC malformed/truncated input and output cleanup. Ten regression cases verify that existing output files, directories, symlinks, hardlinks and dangling links are never overwritten or deleted.
- The Python worker's native-decoder integration uses a real `VemoAudio` process and synthetic WAV; original input and existing committed PCM remain preserved on a failed operation.

## Packaged inference

The signed app's bundled interpreter and native decoder processed newly generated Korean and English system-voice audio. The process ran outside the source directory, with a system-only PATH, explicit local model cache and network-denying inference audit hook.

Input / recognized text:

> 오늘 회의에서는 다음 주 프로젝트 일정과 데이터베이스 설계를 논의했습니다. The API integration starts on Monday.

Both languages were preserved in this short synthetic example. Alignment produced timestamps for both languages; the title model returned a nonempty topic. This is a smoke check, not an accuracy benchmark or a guarantee for real meetings. After inference, `codesign --verify --deep --strict` still passed.

## Documentation images

The three README images were captured from the actual SwiftUI app in a separate demo bundle. The source fixture is `DemoContent.swift`; no personal audio, transcript or Apple database was used. Displayed progress is static explanatory data, not a timing measurement.

## Limits

- The bundled MLX native libraries require macOS **26.2+** (verified from Mach-O load commands). The app metadata and installation requirements use that minimum; older OS execution was not tested.
- No Developer ID signature or Apple notarization. Initial Gatekeeper approval and post-update Full Disk Access re-registration may be needed.
- Direct Apple Voice Memos database support is pinned to the observed macOS 27.0.1 schema. Manual import is the fallback for other schemas.
- Physical second-Mac installation, fresh iPhone recording arrival, iPhone Files result arrival, login launch and sleep/resume are not yet verified.
- No reference-transcript WER, diarization or cross-model benchmark. Korean-priority ASR may need further evaluation on English-heavy recordings.
- AVFoundation codec support applies. The native helper writes standard RIFF WAV, with a 4 GiB PCM limit.
- Recorded-date conversion uses Asia/Seoul when the source has no original timezone. Files lacking a trusted date require user entry.
