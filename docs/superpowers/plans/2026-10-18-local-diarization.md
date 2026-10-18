# Local diarization implementation plan

> Workers use the assigned subagent-driven-development task slice only; no recursive agents.

**Goal:** Local speaker separation and editable labels on Mac/Windows, synced display on Android.
**Architecture:** Optional hash-bound sidecar and isolated CPU helper; existing ASR/notes preserved.
**Tech Stack:** sherpa-onnx 1.13.8 Python/C#, Swift, Kotlin.
**Spec:** docs/superpowers/specs/2026-10-18-local-diarization.md

## Global constraints
One inference process, CPU 2 threads, 3600-second limit, 4GiB available memory guard. No Mac builds or inference during source work. Root alone schedules remote Windows builds and controlled integration. No unrelated design edits, credentials in logs, live-store probes or automatic model fallback.

## Review focus
- Stale audio/transcript must reject metadata.
- Rerun/cancel must retain old committed artifacts.
- Ambiguous overlap must not claim an identified speaker.
- Older clients and missing sidecar must still show ASR/notes.
- Model/runtime absence must have actionable failure without blocking transcription.

## Tasks
- [x] Windows helper and core/store/UI integration: assigned Team1 complex; first tests for sidecar hash/ids/times/overlap and preserving old output; root remote red/green then actual model run. Own windows/ only.
- [x] Mac Python helper/protocol/Swift display and name controls: assigned Team1 complex; unit fixtures, model install pins and notices. Own src/, macos/, tests/, Python dependency files only. No Mac execution; root schedules lightweight tests.
- [x] Android optional sidecar fetch/storage/display: assigned after contract implementation stabilizes; own android/ only. Validate hash/reference/malformed data, retain result.json compatibility.
- [ ] Root integration and independent Team2 verification: build/test platforms sequentially, run known long fixture Windows and controlled short Mac recording, test rename/restart/sync, preserve originals and record actual gaps.
- [ ] Root cleanup/checkpoint and NAS bounded evidence packet.

## Integration checkpoint 2026-10-18

Mac build36 installed with original local signing identity. Actual short recording analysis, Korean name edit, restart persistence, and selection change verified. Same-recording reload now preserves editing. NativeChecks579 passed; XCTest unavailable in CLT. Python focused48 passed; engine selection102 passed/1skip/23subtests plus one missing-MLX environment failure, resolved by running worker tests under bundled runtime (7 tests,1skip). Android88 unit tests passed and APK18 installed.

Actual Sherpa output overshot end by16ms(Mac) and29.7ms(Windows). Pinned model receptive-field plus float32 tolerance bounds normalization to31.457ms; larger/nonfinite/invalid bounds rejected. Independent Team2 review found no issue in bounded fix.

Automatic diarization is experimental: a33min lecture produced168clusters in249s; not committed to user job. More than20 automatic clusters is rejected; explicit1-20 count supported. Higher global threshold was rejected because known two-voice fixture collapsed. Exact long-probe peak memory is unknown after Int32 monitoring overflow; >=2.1GiB observed.

Windows0.1.20 build and Android sidecar receipt are tracked in local-validation/diarization/windows-cli-verification. Installed Windows UI path and Android visual confirmation are separate from CLI/API and transfer-integrity tests; do not claim those when unavailable.

## Final checkpoint

Implementation committed bf8fdd4; Mac build36, Windows0.1.20, Android0.1.19(version19) installed. Windows Core437/App128+1skip/Provision24 passed. Android final89tests0fail. Actual installed Windows helper -> conditional Drive sidecar/name update -> Android saved document verified11checks; original result/ZIP8checks pass. Android accepts bounded50ms metadata duration differences with hard3600s cap; observed35.008s PCM versus35s metadata.

Mac UI analyze/rename/restart verified; Windows installed UI and Android visual speaker display remain unverified. Auto quality remains experimental; this is not a full accuracy certification. Worktree clean after feature checkpoint; remote temporary OAuth build input removed and duplicate model download copies cleaned after hash comparison. NAS reviewedrecord/checksum/index verified at /mnt/nas/codex-worklog/records/2026/10/2026-10-18-210018-vemo-asr-drive-speakers.
