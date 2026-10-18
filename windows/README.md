# Vemo Windows functional preview

.NET 10 core and an Avalonia shell applying the approved A Native White design and icon. Existing local processing remains a functional preview. See [design scope and verification](../docs/design/NATIVE_WHITE.md) for supported behavior and remaining visual checks.

최종 실제 실행 범위와 알려진 제한은 [기능 프리뷰 검증 기록](../docs/FUNCTIONAL_PREVIEW.md)을 참고하세요. 아래 빌드 절차와 과거 실패 분석을 실제 Windows 실행 결과로 해석하지 마세요.

## 빠른 시작

x64(일반 Intel/AMD PC) 또는 ARM64 빌드의 전체 폴더를 풀고 `Vemo.App.exe`를 실행합니다. .NET 설치는 필요하지 않습니다. 모델과 음성 런타임은 아래 설치 도구로 별도 받으며, 개인 Google 인증정보나 모델 가중치는 배포 파일에 포함하지 않습니다.

1. 먼저 [Microsoft Visual C++ v14 재배포 패키지](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 설치합니다. x64 PC는 [공식 x64 설치 파일](https://aka.ms/vc14/vc_redist.x64.exe), ARM64 PC는 공식 문서의 ARM64 패키지를 선택하세요. 별도 음성·노트 런타임이 사용하는 구성 요소이며, Vemo의 .NET 포함과 별개입니다. 이어서 `Vemo.Provision.exe --destination C:\VemoModels --arch x64 --whisper turbo --notes 9b --download-ffmpeg`로 CPU용 기본 모델을 준비합니다. 크기와 SHA-256이 일치해야 설치됩니다. 27B 모델은 메모리와 시간이 더 필요합니다. ARM64는 `--arch arm64`와 별도 ARM64 FFmpeg 경로를 지정합니다.
2. **설정 → 프리셋 설정 가져오기…**에서 생성된 `C:\VemoModels\settings.json`을 고르고 ZIP 저장 폴더를 지정한 뒤 **설정 저장**를 누릅니다. Nextcloud·Drive 데스크톱 동기화 폴더도 ZIP 위치로 선택할 수 있습니다.
3. 완료된 녹음을 가져옵니다. 원본은 복사하며, 녹음일을 모르면 `date-unknown`으로 표시합니다. 알고 있는 경우 시간대가 포함된 ISO 날짜를 직접 입력합니다. 전사·노트가 끝나면 폴더 구조에 맞춰 녹음별 ZIP이 만들어집니다. ZIP에는 녹음·전사·노트 3개만 들어갑니다.
4. Ctrl/Shift 다중 선택, 폴더·하위 폴더 이동, 휴지통·복원·Vemo 소유 사본 영구 삭제를 지원합니다. 제목은 상세 화면에서 편집합니다. 원본과 이미 내보낸 ZIP은 유지합니다.

Windows는 **Google Drive 직접 OAuth 연결**과 로컬 동기화 폴더 방식을 지원합니다. 배포 앱에서 설정 → Google Drive 연결로 로그인합니다. 사용자가 OAuth JSON을 준비할 필요는 없습니다. 실제 계정/폰 왕복 검증 상태는 [연결 가이드](../docs/WINDOWS_GOOGLE_SETUP.md)를 참고하세요. 자동 제목 생성, 14,000자 초과 전사문의 노트 생성, 폰 자체 AI, NVIDIA 실제 실행과 Windows GUI 수동 검증도 완료되지 않았습니다. macOS와 기능이 모두 같다고 보지 마세요.

## Build and test

From the repository root (the locally provisioned SDK is shown):

```sh
build/toolchains/dotnet/dotnet test windows/Vemo.Core.Tests
build/toolchains/dotnet/dotnet run --project windows/Vemo.Validation -- windows/artifacts/validation-macos.json
build/toolchains/dotnet/dotnet publish windows/Vemo.App -c Release -r win-x64 --self-contained true -o windows/artifacts/win-x64
build/toolchains/dotnet/dotnet publish windows/Vemo.App -c Release -r win-arm64 --self-contained true -o windows/artifacts/win-arm64
build/toolchains/dotnet/dotnet publish windows/Vemo.Validation -c Release -r win-x64 --self-contained true -o windows/artifacts/validation-win-x64
```

For a reproducible publication containing both app architectures, both native console validators, ZIP bundles, per-file SHA-256 manifest and build/test logs:

```sh
python3 windows/scripts/publish.py --output windows/artifacts/publication-YYYYMMDD-HHMM
```

The output must be a new/empty directory under `windows/artifacts`; existing publications are preserved. The script verifies PE architecture and self-contained runtime files. It does not bundle inference runtimes/models, sign binaries, or claim Windows execution. It needs Python 3.9+ and the project-local .NET SDK (override with `--dotnet`).

On a native Windows VM, copy/extract the publication and run:

```powershell
powershell -NoProfile -File .\run-acceptance.ps1 -BundleRoot C:\VemoPublication -Runtime win-x64 -ReportDirectory C:\VemoReports
```

The wrapper verifies validator dependency hashes against `manifest.json`, runs native core acceptance, and preserves JSON/stdout/stderr reports. Add `-Settings C:\settings.json -AudioFile C:\finished.wav` to run the real local pipeline afterward. The isolated symlink tests need symlink creation permission; the script never elevates or changes host configuration. A Windows failure is reported without fallback to another runtime. The manifest provides corruption detection, not an authenticated code-signing identity. The scripts do not launch the GUI or modify shared services.

Copy the **whole** matching publish directory to Windows and launch `Vemo.App.exe`. These are self-contained, unsigned portable builds, not installers. Keep DLLs/native dependencies next to the executable. In a Windows VM, run `Vemo.Validation.exe C:\validation\acceptance.json`; it returns nonzero on failure and writes the actual OS/architecture/runtime, individual fixture results and limitations. Symlink tests require permission to create symlinks (e.g. an elevated evaluation VM). The console runner discovers core tests, including inline theory cases and integrity/shutdown checks. The Unix native-process test is explicitly skipped in the Windows console report; a native Windows self-child cancellation/nonzero-exit check is added to the Windows report. Actual native outcomes are recorded in the functional preview document. Fixtures use synthetic bytes/text and never claim model inference.

For a real local pipeline test without the UI:

```sh
build/toolchains/dotnet/dotnet run --project windows/Vemo.Validation -- --infer settings.json finished-audio.wav windows/artifacts/real-inference.json
```

Or run `Vemo.Validation.exe --infer settings.json finished-audio.wav report.json` on Windows. `settings.json` uses the `AppSettings` JSON contract (PascalCase; enum strings supported):

```json
{
  "Models": {
    "WhisperProfile": "small",
    "WhisperExecutable": "C:\\Vemo\\whisper\\whisper-cli.exe",
    "WhisperModel": "C:\\Vemo\\models\\ggml-small.bin",
    "LlamaExecutable": "C:\\Vemo\\llama\\llama-cli.exe",
    "LlamaModel": "C:\\Vemo\\models\\Qwen3.5-9B-Q4_K_M.gguf",
    "NotesProfile": "Qwen3.5 9B GGUF",
    "FfmpegExecutable": "C:\\Vemo\\ffmpeg\\ffmpeg.exe",
    "Compute": "CPU",
    "TimeoutMinutes": 120
  }
}
```

This mode uses the real CLI adapter, copies audio into a unique isolated state directory beside the report, and preserves ASR/notes/ZIP/errors and elapsed time. It does not download anything. A failed runtime/model/schema is a failed report, not a successful synthetic substitution. Keep inference reports private if input audio is private.

## Using the app

1. Open Settings. Choose separate local export and (optionally) mobile-sync folders. Select native whisper.cpp, llama.cpp and FFmpeg executables and downloaded model files. CPU is the default. Model profile labels do not prove a file matches a profile; verify downloads against `model-catalogue.json`.
2. Import **finished** audio files. The source is copied, never moved. Local file imports have an unknown recording date rather than an invented file-mtime date; supply an ISO timestamp with an explicit timezone if known. Mobile dates are immutable and retain the original timestamp/offset. Titles can be edited inline in the detail field.
3. Processing is queued durably. Pause stops starting new jobs. Select queued/running jobs and Cancel to cancel them. Retry snapshot keeps the job's original model/settings; Retry current models explicitly replaces its selection. Completed output survives settings changes/restarts. Missing runtime/model files are recoverable failures. Notes failure retains ASR so retry can reuse it.
4. Create folders/subfolders under the selected folder. Move folder changes its parent; Move selected moves recordings. Ctrl/Shift selects multiple recordings. Trash hides jobs; Restore returns them to their stored folder. **Purge owned copies immediately and permanently removes only selected inactive trashed items under Vemo's owned state root.** Source files and exported ZIPs survive. Purged mobile identities remain as tombstones so scanning does not re-import them.
5. Completed jobs automatically export three entries: `original.<ext>`, `transcript.txt`, `notes.md`. ZIP directories mirror the library folders; names use title and civil recording date (or `date-unknown`), with collision suffixes and no hashes. Existing archives are never overwritten or removed when recordings move, rename or are purged. Export errors are separate from successful inference and can be retried after choosing/fixing output storage.

Local state: `%LOCALAPPDATA%\Vemo\WindowsPreview`. Only one app/core owner may open a state root at a time. `library.json` is atomically replaced after durable writes; interrupted processing returns to the queue on restart. Do not hand-edit state while the app is open. Back up the whole state directory while closed. Sources/exports are outside it; restoring a state backup can restore old queue/tombstone state, so review before resuming sync.

## Mobile v1 folder adapter

Settings registers a processor identity via `processor.json` without replacing an existing processor owner. Every 15 seconds the app polls:

```
recordings/<lowercase-UUID>/<sha256>/recording.json
recordings/<lowercase-UUID>/<sha256>/audio.m4a
results/<lowercase-UUID>/<sha256>/result.json
```

Only completed `desktop` requests for this processor are imported. `record_only` and incomplete requests are skipped. Schema/duplicate keys, path identity, dates, original hash/size and processor are validated independently; malformed siblings do not prevent valid requests. Completed local jobs publish the eight-field v1 result after rechecking the request identity. Existing different results are preserved with an explicit conflict. A locally published result is **not** a cloud-upload receipt. Direct Google OAuth is now available through the separate Google adapter; see docs/WINDOWS_GOOGLE_SETUP.md.

## Core interfaces and validation boundaries

- `LibraryStore(root)`: exclusive state, import, models/settings snapshots, folders/move, title/date editing, trash/restore/purge, artifact reads, collision-safe ZIP export.
- `JobRunner(store, IInferenceEngine)`: one queued job per invocation; `RunNextAsync`, `CancelActive`, explicit stages/errors and ASR reuse. Tests inject tiny deterministic inference fixtures.
- `LocalCliInferenceEngine`: native processes without a shell, FFmpeg decoding to mono 16 kHz PCM, Korean-first whisper.cpp, numbered-source topic JSON through llama.cpp. llama b11714 uses single-turn model Jinja template, reasoning disabled, offline mode and bounded generation; JSON is strictly validated after generation.
- `ProcessRunner`: argument-list invocation, redirected/closed stdin, bounded stdout/stderr (4 MiB each), per-process timeout, process-tree cancellation. Model/executable file locations are captured per job; replacing files at those locations is outside the snapshot guarantee.
- `MobileFolderAdapter`: `Register`, `ScanAsync`, `Publish`. Returns per-request issues; keeps identity/date/source separate from editable display title.

Interactive Windows GUI and NVIDIA execution remain unverified. Native Windows Server 2022 core acceptance, Whisper small CPU inference, and a 15-second process startup observation are documented in the functional preview record; they do not certify Windows 11, ARM64 execution, or the complete Windows notes pipeline. Notes currently reject transcripts exceeding 14,000 source characters rather than silently truncating; long-recording chunked synthesis is not implemented. Reference/schema checks establish structural source indices, not factual entailment or summary quality. CPU is supported in the adapter; CUDA requires explicit runtime/device confirmation, and 27B cannot be assumed to fit 16 GB VRAM. RAM estimates in Settings are estimates. Export filename idempotency uses a durable receipt, not continuous tamper verification of an externally modified archive. Filesystem safeguards reject symlinks/reparse points at operation boundaries; they do not claim protection against an adversarial process replacing directories between checks. There is no media-completion probe on local import; the user must select finished recordings. No installer, signing certificate, update system, or cloud credentials are supplied.

The catalogue contains root-verified revision/LFS SHA metadata; downloaded artifacts and licenses must be verified by the release owner before redistribution. No binary/model downloads are performed by the app. Build output under `windows/artifacts` is ignored by Git.

## CPU preset provisioner

`Vemo.Provision` is a separate self-contained console setup tool; it does not modify the reviewed production app/core or download weights during inference. Publish scripts include `provision-win-x64` and `provision-win-arm64`. Select a preset once:

```powershell
.\provision-win-x64\Vemo.Provision.exe --destination C:\VemoPreset --arch x64 --whisper small --notes 9b --ffmpeg C:\Tools\ffmpeg.exe
```

Use `--whisper turbo`, `--notes 27b`, or `--arch arm64` as appropriate. The preset downloads only selected CPU runtimes/models with pinned size/SHA-256, extracts complete runtime trees, and writes `settings.json` plus `provision.json`. A failed/cancelled download removes its unique partial file; completed verified cache entries are reused. A bad pre-existing cache entry is preserved and rejected, never silently replaced. Use a fresh/empty destination; partial installations are retained for diagnosis. A non-default cache can be supplied with `--cache C:\VerifiedArtifacts`; add `--offline` to forbid network downloads.

Select existing installations with `--existing-whisper <exe>`, `--existing-llama <exe>`, `--existing-asr-model <file>`, and `--existing-notes-model <file>`. Model files must match the chosen preset pin. Existing executable architecture is checked and its observed hash is recorded as unpinned. No executable is launched by setup. Runtimes and models stay at the exact generated paths; do not move the preset/cache afterward.

For x64, select the pinned LGPL BtbN n8.1.3 archive with `--download-ffmpeg`. Alternatively supply `--ffmpeg <existing native exe>` or explicitly use `--wav-only`. ARM64 requires an existing native executable or WAV-only mode. WAV-only writes a `test-wav.ps1` direct whisper smoke test for finished 16 kHz mono PCM WAV; it is **not a full app pipeline**, since the current app still requires FFmpeg. Setup does not claim runtime/inference success. Pass generated `settings.json` to the validator's `--infer` mode for actual validation. In the existing Settings window, use **프리셋 설정 가져오기…** and choose the generated settings.json. Import fills the visible local paths/model selection/output fields for review; **설정 저장** applies them. It does not execute a runtime, import mobile registration/queue state, or accept CUDA confirmation. Unknown JSON properties, script/command-line executable paths, missing files and non-CPU presets are rejected. Imported .exe paths remain user-selected executables, not a code-signing trust claim.

See `THIRD-PARTY-NOTICES.md`. Models are downloaded on user selection, never redistributed in the publication. The x64 FFmpeg archive pin comes from root-verified fixed-release metadata. Downloaded bytes still require setup verification. No CUDA availability is implied.

`--download-ffmpeg` is reserved for the root-approved Windows x64 FFmpeg archive pin. Setup refuses it before any runtime/model download when the catalogue lacks complete URL/size/SHA metadata. It cannot be combined with `--ffmpeg` or `--wav-only`, and is rejected for ARM64: no x64 emulation is silently selected. A root-approved pin must be copied into `runtime-catalogue.json` before rebuilding the provisioner. LGPL build licensing/notices apply to the selected BtbN archive; retain its complete extracted license tree.

### Pinned llama response channel and grounding checks

The observed b11714 CLI prints a loading message, banner, truncated prompt echo and `Exiting...` on stdout even with display suppression. The adapter now uses official `--output-file` and accepts only the exact single-turn `User:\n<submitted prompt>\n\nAssistant:\n<JSON>` format written by [b11714 cli-context.cpp](https://github.com/ggml-org/llama.cpp/blob/b11714/tools/cli/cli-context.cpp) (prompt submission removes one final newline; output-file writes the complete prompt, unlike stdout's truncated echo). Windows CRLF is normalized. The entire assistant payload must parse as a single strict JSON object. No first/last-brace scan, arbitrary banner stripping, or stdout fallback is used. A mismatched wrapper/runtime fails explicitly and preserves ASR; the final adapter completed a real CPU model run on macOS; native Windows execution is recorded separately in the validation document.

Conditions and examples must match a quote in their cited source lines, ignoring whitespace only, or be empty arrays. The persisted text uses the exact original source span. This rejects model-invented/paraphrased examples rather than presenting them as source material. It does not establish entailment of free-form topic summaries or repair inaccurate Whisper Korean keywords. The actual small-model ASR/9B run exposed Korean transcription errors and unsupported inferred detail; quality remains an observed limitation, not resolved by schema validation.

### Window shutdown and new-install ASR default

Closing now stops new UI actions and queue steps, cancels the shared lifetime and active inference, closes owned dialogs, and awaits the tracked background task plus registered manual work before disposing the store. Already-running synchronous filesystem work is drained rather than abandoned. This is covered by non-UI work-lifetime tests; native Windows GUI shutdown still needs root validation.

New model selections default to Whisper large-v3-turbo. Existing saved selections and in-flight snapshots are unchanged. The reason is root's single Korean synthetic-speech recording comparison: small misrecognized multiple technical terms; turbo was mostly correct except the final sentence. The reported 18.7-second CPU turbo run and source duration are observations of that recording/machine, not a general speed/accuracy benchmark or a measured error rate.

The final real Mac CPU CLI pipeline with turbo ASR + 9B notes completed in 55.955 seconds and produced five topics and a verified three-entry ZIP. The prompt groups related definitions, examples and scope instead of producing one topic per source sentence. Source transcription errors remained in the resulting notes. Root measured approximately 2.42% normalized CER for turbo versus 8.87% for small on one synthetic-speech recording, not a general accuracy benchmark. TypeSafe was unavailable (no API key); no TypeSafe semantic check ran. Free-form summary entailment and long-recording synthesis remain limitations.

### Separate grouped-notes whitespace fix

The earlier failed response and regression evidence are retained locally. Root's later grouped-notes run failed after 50.98 seconds because the model wrote `100 개` / `20 개` where the source had `100개` / `20개`. The source-backed condition/example validator now ignores whitespace only during matching and persists the exact matching source span, including the source's original spacing. It does not normalize numbers, negation, punctuation, or wording, and does not guess corrections. A captured actual-response regression fixture verifies this mapping and canonical JSON persistence without rerunning models. The prompt additionally asks to retain explicit unresolved decisions/dates (the prior four-topic output omitted the undecided launch date); the final real-model run retained that unresolved date. No new refs schema or repair re-ask loop was introduced.
