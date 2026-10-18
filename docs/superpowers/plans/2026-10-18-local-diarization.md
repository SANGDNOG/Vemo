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
- [ ] Windows helper and core/store/UI integration: assigned Team1 complex; first tests for sidecar hash/ids/times/overlap and preserving old output; root remote red/green then actual model run. Own windows/ only.
- [ ] Mac Python helper/protocol/Swift display and name controls: assigned Team1 complex; unit fixtures, model install pins and notices. Own src/, macos/, tests/, Python dependency files only. No Mac execution; root schedules lightweight tests.
- [ ] Android optional sidecar fetch/storage/display: assigned after contract implementation stabilizes; own android/ only. Validate hash/reference/malformed data, retain result.json compatibility.
- [ ] Root integration and independent Team2 verification: build/test platforms sequentially, run known long fixture Windows and controlled short Mac recording, test rename/restart/sync, preserve originals and record actual gaps.
- [ ] Root cleanup/checkpoint and NAS bounded evidence packet.
