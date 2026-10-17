# Vemo unattended functional milestone implementation plan

> Workers execute only assigned slices without recursive delegation. Follow configured Team1 implementation / Team2 test_debug roles; root owns acceptance and records. User explicitly chose autonomous execution and functional scope; no additional design approval loop.

**Goal:** Finish actual Android/Mac inference roundtrip and produce a functional Windows preview with installation/test evidence.
**Architecture:** Preserve Kotlin/Swift/MLX implementations, use a .NET Windows core with local inference CLI adapters and a thin existing-style shell. Share mobile v1 format, not private database files.
**Tech Stack:** .NET 10/Avalonia, whisper.cpp, llama.cpp, Swift/Python MLX, Android API36, QEMU if official media available.
**Spec:** docs/superpowers/specs/2026-10-18-windows-functional-preview.md

## Constraints and review focus

- Functional work only; preserve completed design source and approved icon, user recordings and credentials.
- No silent backend/account fallback, cloud auth expansion, fake inference or fabricated Windows/GPU tests.
- Root-only actual cloud/device operations. Each worker has exclusive file ownership.
- Recovery after interrupted write/process, stale result publication, unsafe remote paths, current selection versus in-flight model, and low-memory execution need explicit tests.

## Task 1 — Real Mac/Android processing (root + Team2 scoped harness)

Files: local-validation/overnight27; minimal production fixes only after reproduction.
- [ ] Generate known Korean/English test speech; finalize/upload through Android production contracts in dedicated5554.
- [ ] Pull with production Mac Google/broker; invoke actual offline Engine/worker using installed verified models; title/notes/three-file outputs.
- [ ] Publish production result and verify Android exact date/identity/content and persistence. Record timings/content limitations.
- [ ] Build/install functional Mac update only with current public icon/reopen fixes and stable cert; verify preserved state.

## Task 2 — Windows implementation (Team1 implementation_complex)

Files: windows/** exclusively. Existing source/Android/macOS/assets/docs owned by root.
- [ ] Core tests first for import/date/storage/model snapshot, nested folder move and trash/purge, three-file ZIP collision behavior and mobile request/result validation.
- [ ] Implement durable local queue, settings/models, filesystem mobile adapter and local CLI inference with cancellation/errors.
- [ ] Thin Avalonia shell: import/list/detail/settings/actions and current progress; no design overhaul.
- [ ] Run tests, publish self-contained win-x64/win-arm64; give exact commands and limitations.

## Task 3 — Runtime and Windows environment (root)

- [ ] Install project-local .NET and pinned official inference/runtime artifacts; inspect release metadata/hashes and notices.
- [ ] Prepare official Windows VM if possible without purchased license or modifying shared services; otherwise bound attempts and document exact blocker while completing executable builds and core tests.
- [ ] Smoke test actual models where executable runtime available; preserve GPU verification as pending unless observed.

## Task 4 — Independent review and finishing (Team2 / verifier + root)

- [ ] Independently test Windows core and adverse inputs; inspect privacy, cancellation, recovery, source preservation and model accuracy claims.
- [ ] Resolve actionable findings, rerun only affected checks; install final artifacts, checkpoint clean commits.
- [ ] Update usage/limitations, retain reproduction data, clean temporary caches and publish reviewed exact-route documentation to mounted NAS with checksum readback.
