# Public preview validation

Current preview: `v0.2.0-preview.4`. Checks performed on an Apple Silicon M5 Pro, 24 GB RAM, macOS 27.0.1. This is evidence from one Mac, not certification of every supported system. The first sections describe the preserved 0.1 release; 0.2 checks follow below.

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


## 0.2 preview.2 UI and manual processing checks (2026-10-11)

- Headers now share one native compact toolbar. CUA inspection of the real demo app confirmed a single sidebar toggle above the library at its trailing edge, vertically aligned with the traffic lights; hide/show recovery worked. An initial placement attempt produced duplicate toggles and was corrected before release.
- The empty notes icon, explanation and `+` generation button form one centered group. A vertical clipping defect observed during real screen transitions was corrected by removing the inner vertical fixed-size constraint. Updated transcript/notes/empty-notes images use synthetic fixtures only.
- Actual clicks on blank padding of the detail tab, folder row, keyword chip, highlight quote and export menu activated their intended actions. Icon menus displayed without an added disclosure chevron. This checks representative native controls, not every display/OS combination.
- The new ProcessingChecks runner compiled unchanged AppModel methods with real native WireTypes and Validation: **118 assertions passed, 0 failed**. The old generateNotes negative control compiled and exited 1 with **12 expected failures** (106 other assertions passed). Product source was never mutated by the negative control.
- Checks cover pause-first manual enqueue/retry/notes/backfill, payloads, distinct request IDs, selection/pause capture, eligibility, transport errors, unchanged power policy and demo isolation. Sequential writes within an action are verified; engine acknowledgement and atomic ordering against unrelated tasks are outside this harness.
- The native executable suite passed **223 assertions** during this follow-up. The incidental long-notes fix changes Python notes decoding and the bounded output allowance; its final regression results follow below. Native-core/audio code did not change, so runtime27 remains prior evidence. Full XCTest remains unavailable on this Command Line Tools installation.
- Installed build5 and build6 retain the fixed local designated requirement. A fresh read-only source diagnostic at `2026-10-11T12:42:08Z` reported `supported_schema` and `schema_verified=true`; no Full Disk Access re-registration was used. This is specific to this Mac's local certificate, not a promise for the ad-hoc public package.
- The user explicitly allowed battery processing. The real engine resumed with `paused=false`, `ac_only=false`: an existing long recording committed all 11 ASR units and moved into alignment, and saved its final publication; a second queued recording also completed transcription, alignment, notes and local publication. Completed recordings were not re-enqueued; nine unprocessed historical recordings were left available for explicit selection.
- Two long recordings returned `notes_failed` with no generated notes; their successful transcripts and local publications remained preserved. For the resumed notes-only job, the last two failed tasks were both index 0 and had no result file or checkpoint. Their overall job status was completed, which must not be interpreted as successful note generation. Local first-unit reproductions subsequently identified output truncation and malformed final closing brackets, as described below.
- The build5 candidate passed strict deep signing, DMG verification and read-only mount checks. It was superseded by build6 after the notes failure was diagnosed; final build6 package evidence follows below.

### Long-notes failure reproduction and correction

The first failed real notes unit reproduced JSONDecodeError locally with network access denied. Metadata-only inspection showed 1534 output tokens against the old 1536 cap and an unterminated string six characters from the end. Raising the bounded cap to 3072 produced a complete 2047-token response, but strict JSON decoding still failed at a five-character final closing-bracket suffix. The raw private response was inspected only locally and is excluded from public images and work records.

The correction allows up to 3072 output tokens and normalizes only a malformed terminal closing-bracket suffix of at most 16 characters when the parser reports the demonstrated delimiter error. It scans the prefix with proper string/escape tracking and requires matching internal brackets and every required closing delimiter already present in the suffix; strict JSON parsing and existing schema/exact-source citation checks still apply. Truncated strings, missing terminal object closure, interior errors and extra trailing data continue to fail. This does not invent or rewrite note content. Worst-case decoding work per call can increase; the limit remains finite.

Two new synthetic regressions failed before their respective corrections, then passed. The focused notes suite passed **10 tests**. The actual complete first-unit response then passed strict schema/source validation, retaining two summaries and three keyword cards; unsupported highlights were filtered rather than accepted. This is evidence for that response and the narrow repair, not a semantic-accuracy benchmark or proof that all later model responses are valid.


Final Python suite: **76 tests passed, no skips** using the pinned Python runtime, project source and the actual native decoder. Independent review identified a too-permissive terminal-repair case with missing closers; a closer-count guard and rejection regression were added before this final run.

Installed build6 preserved Full Disk Access in a fresh diagnostic at `2026-10-11T13:13:46Z`, with `supported_schema`, `schema_verified=true`. Battery processing remained enabled and global pause remained off. Real regeneration persisted the previously failing first note unit (1/11 checkpoint), but both complete long-note runs subsequently failed in other units. Full long-recording note generation is therefore **still unresolved**; the narrow truncation/terminal-format correction is not a claim that those recordings now complete. Their ASR, alignment and published transcripts remain preserved, and the nine historical unprocessed recordings were not started.


Final public build6 passed strict deep signing, DMG verification/read-only mount, Applications link and installation-notice checks. Every bundled engine source module matched the final project source. Bundled engine/MLX imports and the synthetic terminal-repair/missing-closure rejection checks passed outside the project with system-only PATH.

Artifact: `Vemo-0.2.0-preview.2-arm64.dmg`, app version `0.2.0`, build `6`, public ID `io.github.sangdnog.vemo`. Size: **290199679 bytes**. SHA-256: `53163590d3b4f7591d3cd0129cd8ca60c29f3ff14440496cf6eb29b694cdf5e3`. Developer ID/notarization, another physical Mac and real Drive/iPhone delivery remain unverified. Prior releases are preserved.


## 0.2 preview.3 column-boundary correction (2026-10-11)

The user clarified that the remaining mismatch was the width/boundary of the toolbar's sidebar section versus the library pane below. Earlier control/inset checks did not establish this alignment. An initial build7 adjusted title and button insets instead; it was installed temporarily, then rejected by the user and superseded. It was never released publicly.

A temporary synthetic-only native geometry probe showed a 290pt sidebar and a 1pt divider, while AppKit's detail titlebar background began at x=288pt and its 5pt blocking region overlapped the divider. The final change hides only the toolbar background with the supported SwiftUI API, allowing the full-height split panes to supply a continuous boundary. Toolbar controls remain native. The probe and its attachment were removed before packaging; no private AppKit view manipulation is shipped.

A fresh profile also reproduced a 144pt initial sidebar despite the declared 250–420pt column range. An explicit sidebar frame minimum now enforces 250pt. A fresh final demo showed 250pt with an intact library header; the existing installed profile retained 264pt. CUA screenshots of both final views showed a continuous boundary from the toolbar through the library. Installed hide/show restored the sidebar and its original width. These checks ran at the default 1040×720pt window size; the final build was not checked on another display or physical Mac. The earlier narrow-window check applied to the superseded build7 and is not final-build evidence.

Independent source review found no blocking API/layout issue and confirmed probe removal; it did not independently run the UI. The final product patch changes only LibraryView layout. Python, NativeCore and processing behavior are unchanged from preview.2, whose 76-test Python and 223-assertion native results remain prior evidence; these suites were not repeated for this layout-only correction.

Local/public build8 and strict deep signatures passed. The local designated requirement exactly matches the prior installed app. A fresh installed diagnostic at `2026-10-11T13:59:02Z` returned `supported_schema`, `schema_verified=true`, without Full Disk Access re-registration. Policy remained `paused=false`, `ac_only=false`, with 5 completed jobs and 9 historical unprocessed jobs. The two unresolved long-note failures remain; this update does not retry or correct them.

The DMG passed verification and read-only mount checks, version/build/public-ID checks, binary equality with the public app, Applications link/notices, all 14 bundled engine source modules matching project source, and bundled engine/MLX imports outside the project with system-only PATH. The image was ejected.

Artifact: `Vemo-0.2.0-preview.3-arm64.dmg`, version `0.2.0`, build `8`, size **288051590 bytes**. SHA-256: `58b8c7e9c769494cdc0d48cc361f04441884c494ffd715ffc31136305ce64231`. The new transcript example uses only synthetic fixtures; other gallery images remain examples from preview.2. Developer ID/notarization, second-Mac installation and real Drive/iPhone delivery remain unverified.

## 0.2 preview.4 nested folders (2026-10-11)

Logical library folders now have optional parents. Creation defaults to the selected folder, the editor changes name and parent in one transaction, and disclosure controls collapse a branch independently of selection. Recording menus and details show full folder paths. The selected view, search and counts include only directly assigned recordings; descendants are shown when their own folder is selected.

- Independent hierarchy checks: **227 assertions passed**, zero failures, exit 0. Full `swift run --package-path macos NativeChecks --broker-checks`: **450 assertions passed**, zero failures, exit 0. A compiled test failed on the old schema-1 default before implementation, then passed after the change.
- Coverage includes stable tree order and collapse, subtree moves preserving assignments, self/descendant/missing-parent rejection, normalized sibling uniqueness, atomic failed-update byte/revision preservation, v1 read-only compatibility and exact migration backups, restart, leaf deletion and blocked parent deletion, and concurrent actor writes.
- AppModel processing regression harness: **118 assertions passed**, zero failures, compile/run exit 0. Python/inference code did not change and the Python suite was not rerun. XCTest remains unavailable in the installed CommandLineTools setup; this is not a claim that `swift test` passed. Existing missing CLT linker search-path warnings remain.
- Actual synthetic UI checks passed for three folder levels, collapse/expand, child creation, descendant-excluding parent picker, subtree reparenting, blocked parent deletion, full-path recording movement, and direct-only count transitions. One initial automation attempt batched parent selection and save too quickly; observing the new picker value before saving produced the expected move. No product defect was established from that attempt.
- An independent source reviewer reported no blocking issues. Visual indentation is capped at six levels to keep sidebar controls usable; stored nesting and full paths remain available beyond that depth. Separate development edge checks handled a 20,000-level graph without recursive traversal failure.
- Same-signed local build 9 was installed without Full Disk Access re-registration. Its designated requirement exactly matched build 8. A fresh diagnostic reported `supported_schema`, `schema_verified=true` at `2026-10-11T14:28:42Z`. Existing library bytes stayed unchanged (two folders, schema 1, revision 2); migration occurs on the first successful edit, rather than on launch. The user-selected policy stayed `paused=false`, `ac_only=false`. Queue counts remained five completed and nine unprocessed, with two long-note failures still unresolved. No backfill or note retries were performed.

Schema 2 writes retain the original schema-1 file as `native/library-v1-backup.json`, published atomically without overwriting an earlier regular backup. Invalid or future data is rejected and retained; backup path obstructions prevent migration. Older apps reject schema 2. A downgrade therefore requires retaining the new metadata and explicitly restoring pre-migration classification; it cannot preserve subsequent folder edits automatically. Folder changes never move or delete audio, transcript, note, or Drive files.

Artifact: `Vemo-0.2.0-preview.4-arm64.dmg`, version `0.2.0`, build `9`, size **290362514 bytes**, SHA-256 `409bcf2c76a9675d678727376385bb94af2ece7a69fcc07d907c05bc20c518d5`. Local/public release builds and strict deep signatures passed. The DMG verified, mounted read-only, matched public binaries and all 14 engine modules, imported its bundled engine/MLX outside the project with system-only PATH, and was ejected. `folders.jpg` is an actual app screenshot containing only synthetic fixtures; the other gallery images are earlier preview examples. Public packages remain ad-hoc signed without Developer ID/notarization. Second-Mac installation and physical Drive/iPhone delivery remain unverified.
