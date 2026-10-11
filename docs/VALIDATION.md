# Public preview validation

Current preview: `v0.2.0-preview.1`. Checks performed on an Apple Silicon M5 Pro, 24 GB RAM, macOS 27.0.1. This is evidence from one Mac, not certification of every supported system. The first sections describe the preserved 0.1 release; 0.2 checks follow below.

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

The three initial README images were captured from the actual SwiftUI app in a separate demo bundle. The source fixture is `DemoContent.swift`; no personal audio, transcript or Apple database was used. Displayed progress is static explanatory data, not a timing measurement.

## Limits

- The bundled MLX native libraries require macOS **26.2+** (verified from Mach-O load commands). The app metadata and installation requirements use that minimum; older OS execution was not tested.
- No Developer ID signature or Apple notarization. Initial Gatekeeper approval and post-update Full Disk Access re-registration may be needed.
- Direct Apple Voice Memos database support is pinned to the observed macOS 27.0.1 schema. Manual import is the fallback for other schemas.
- Physical second-Mac installation, fresh iPhone recording arrival, iPhone Files result arrival, login launch and sleep/resume are not yet verified.
- No reference-transcript WER, diarization or cross-model benchmark. Korean-priority ASR may need further evaluation on English-heavy recordings.
- AVFoundation codec support applies. The native helper writes standard RIFF WAV, with a 4 GiB PCM limit.
- Recorded-date conversion uses Asia/Seoul when the source has no original timezone. Files lacking a trusted date require user entry.


## 0.2 local development checks (2026-10-11)

The 0.2 preview adds notes and provider-independent synchronized folders. The earlier 0.1 release remains available unchanged.

- Native UI/source/publication: **135 assertions passed**, including fabricated citations, source hash mismatch, repeated/overlapping quote timing, legacy Job decoding, dataless/iCloud readiness, folder stability and a six-file note-inclusive publication/replay. Case-insensitive aliases to not-yet-created output folders are rejected.
- Native runtime/audio: **27 assertions passed**. Existing audio handling remains covered.
- Deterministic selection regression copies the actual `loadSelection` method into a controlled read-boundary harness: both successful and failed obsolete reads preserve the current selection, and clearing the current artifact clears the prior transcript. It was observed failing before correction, then passed.
- New notes/Drive images (`notes.jpg`, `drive-settings.jpg`) were captured from the actual separate demo app with synthetic fixtures. Keyword filtering and citation-to-transcript navigation were inspected through the native UI. They are explanatory data, not model accuracy results.
- Real Drive-provider upload, account login, offline-pinning behavior and iPhone arrival have not been validated end-to-end. Generic local-file transport and availability decisions are covered; Vemo's completed output receipt confirms local durability only.
- The generated summaries are model judgments. Matching quotes are byte/source checks, not proof that every summary interpretation is supported or that every important subject is included.


## 0.2 UI and library organization checks (2026-10-11)

- Final native suite: **223 assertions passed** with `swift run --package-path macos NativeChecks --broker-checks`. This includes 81 new logical-folder checks and 7 folder/search queries. Folder names, restart, exact byte preservation for corrupt/unsupported state, failed writes, delete-to-unfiled, and concurrent changes through the shared store actor are covered.
- An independent Team2 review ran **15 focused checks**, compiling unchanged AppModel methods against real store/query types with controlled boundaries. Folder/search misses clear audio and artifacts; invalid rename/delete preserves published metadata; startup readiness and stale revision protection pass. The harness tests the quit flush method; it does not replace actual NSApplication shutdown testing.
- A fresh release build and strict deep signature verification passed for local and public packages. The local package uses the same designated requirement as the installed personal app.
- The actual separate demo UI was used to create, move, filter, rename and delete logical folders. Deleting a populated folder preserved all 3 synthetic recordings, transcript and notes, and returned their assignments to unfiled. New screenshots use only these fixtures.
- Icon purple #6258EE is used as a restrained action/selection accent. Press/hover/tab animations use 0.18 seconds; source review verifies reduce-motion disables animation and scale. Dark appearance and reduced-motion visual presentation have not been exercised in a separate OS setting session.
- `swift test --package-path macos` fails on this Command Line Tools installation because the XCTest module is absent. The executable NativeChecks suite above passed; no full XCTest success is claimed.
- Organization is local metadata at `stateRoot/native/library.json`; input audio, Apple Voice Memos and published Drive files are not moved. One actor per state root serializes writes in the shared app model. Concurrent separate Vemo processes writing the same state root are not supported.

The installed build 3 retained Full Disk Access (`supported_schema` in a fresh native diagnostic). The public DMG passed checksum verification, read-only mount/content validation, bundled-runtime presence, public ID/build checks, and strict deep signing. A queued short synthetic recording completed notes and publication through the installed UI/engine; the original global pause policy was restored and the app closed for the user's requested stop.

A control-state issue was observed during that installed-app check: successful pause/resume commands changed engine state without immediately notifying the UI. Work was saved at the user's stop request and resumed from that checkpoint. The final build 4 fixes the missing notification as described below.

## 0.2 final policy and package checks (2026-10-11)

- Final Python suite: **74 tests passed, no skips**, with pinned bundled dependencies and the actual native audio helper. Three new regression tests cover immediate policy notification, current state on replay, and rejection without policy changes. The tests were observed failing before the fix, then passing.
- Successful pause/resume/set_policy now emits committed current `engine_status` before the acknowledgement. An independent Team2 real-service check passed **152 assertions, 21 commands and 37 ordered events across two sessions** with synthetic empty state. The same harness failed against an isolated pre-fix source copy as expected. Running it with the final public package's interpreter and engine also passed.
- Installed build 4 showed **처리 재개 → 새 작업 일시 중지 → 처리 재개** immediately without reconnecting. Read-only queue checks agreed with both visible states. The original paused/AC-only policy was restored; historical recordings remained unprocessed.
- The installed app retained the same designated requirement. A fresh native diagnostic at `2026-10-11T11:49:03Z` reported `supported_schema`, `schema_verified=true`, without Full Disk Access re-registration. This applies to this Mac's fixed local certificate; the public preview still uses ad-hoc signing.
- Final local/public release builds and strict deep signatures passed. The DMG passed `hdiutil verify`, SHA-256 verification, read-only mount/content checks, public ID/version/build checks, source-to-bundled-engine comparison and bundled engine loading outside the project with system-only PATH. The mounted volume was ejected.
- Signed-package local note inference on synthetic content previously completed in **8.08 seconds** with exact-source quotes, outside the source tree and with network access denied to the worker. This is a smoke result, not a quality or performance benchmark. The final policy-only fix does not change note inference.

Final artifact: `Vemo-0.2.0-preview.1-arm64.dmg`, app version `0.2.0`, build `4`, public bundle ID `io.github.sangdnog.vemo`. SHA-256: `eefed4f42ec06c5bbde451a2dc3827ea09f3168d7ebfc04a7181df0609b82cb6`. The preview is not Developer ID signed or notarized; second-Mac installation and real Drive/iPhone delivery remain unverified.
