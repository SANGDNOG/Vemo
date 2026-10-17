# Third-party notices

## Dependencies redistributed with the Windows preview

Keep this file and the complete `licenses/` directory with the portable distribution. The app, provisioner and console validator include self-contained .NET runtimes and their published dependencies. The original publication bytes are retained; notices inside individual publish folders are supplemented by this distribution-level inventory.

The [license inventory](licenses/index.json) maps 32 NuGet dependency entries (including metadata-only packages conservatively) to 23 unmodified license/notice texts. It records package versions, public nuspec metadata and hashes, source repository commits where declared, exact license source URLs or NuGet package members, and license SHA-256 values. The inventory covers the published `.deps.json` files, both .NET runtime packs, and the xUnit VS adapter copied by package build assets even though it is absent from `.deps.json`. These are license-source records, not a software bill of materials for separately downloaded inference runtimes/models.

| Redistributed dependency | Version | Included license/notice text |
| --- | --- | --- |
| .NET runtime, x64 | 10.0.13 | [MIT license](licenses/microsoft.netcore.app.runtime.win-x64/10.0.13/LICENSE.TXT), [third-party notices](licenses/microsoft.netcore.app.runtime.win-x64/10.0.13/THIRD-PARTY-NOTICES.TXT) |
| .NET runtime, ARM64 | 10.0.13 | [MIT license](licenses/microsoft.netcore.app.runtime.win-arm64/10.0.13/LICENSE.TXT), [third-party notices](licenses/microsoft.netcore.app.runtime.win-arm64/10.0.13/THIRD-PARTY-NOTICES.TXT) |
| Avalonia and its Desktop, FreeDesktop, AtSpi, HarfBuzz, Native, Remote.Protocol, Skia, Themes.Fluent, Win32 and X11 packages | 12.1.4 | [MIT license](licenses/avalonia-12.1.4/licence.md), from the nuspec repository commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31` |
| Avalonia ANGLE Windows natives | 2.1.27548.20260419 | [Package license](licenses/avalonia.angle.windows.natives/2.1.27548.20260419/LICENSE) |
| SkiaSharp / Windows native assets | 3.119.4 | [Managed license](licenses/skiasharp/3.119.4/LICENSE.txt), [native license](licenses/skiasharp.nativeassets.win32/3.119.4/LICENSE.txt), [native third-party notices](licenses/skiasharp.nativeassets.win32/3.119.4/THIRD-PARTY-NOTICES.txt) |
| HarfBuzzSharp / Windows native assets | 8.3.1.3 | [Managed license](licenses/harfbuzzsharp/8.3.1.3/LICENSE.txt), [native license](licenses/harfbuzzsharp.nativeassets.win32/8.3.1.3/LICENSE.txt), [native third-party notices](licenses/harfbuzzsharp.nativeassets.win32/8.3.1.3/THIRD-PARTY-NOTICES.txt) |
| MicroCom.Runtime | 0.11.6 | [MIT license](licenses/microcom.runtime-0.11.6/LICENSE), from nuspec commit `76785efcafd91b5902fd19dd11145f6dd655b7b4` |
| Tmds.DBus.Protocol | 0.94.1 | [MIT license](licenses/tmds.dbus.protocol-0.94.1/COPYING), from nuspec commit `b4a7fed0b878f74cb54f7cca84d2889af4e596ba` |
| Newtonsoft.Json | 13.0.3 | [MIT license](licenses/newtonsoft.json/13.0.3/LICENSE.md) |
| Microsoft.NET.Test.Sdk, TestPlatform.ObjectModel, TestPlatform.TestHost, CodeCoverage | 17.14.1 | [MIT license](licenses/vstest-17.14.1/LICENSE), [TestHost notices](licenses/microsoft.testplatform.testhost/17.14.1/ThirdPartyNotices.txt), [CodeCoverage notices](licenses/microsoft.codecoverage/17.14.1/ThirdPartyNotices.txt), [additional coverage notices](licenses/microsoft.codecoverage/17.14.1/build/netstandard2.0/ThirdPartyNotices.txt) |
| xUnit, assert, core, extensibility.core, extensibility.execution | 2.9.3 | [Copyright/license notice and included MIT code notice](licenses/xunit-2.9.3/license.txt), [full Apache 2.0 license](licenses/apache-2.0/LICENSE.txt) |
| xunit.abstractions | 2.0.3 | [Declared project license snapshot](licenses/xunit.abstractions-2.0.3/license.txt), [full Apache 2.0 license](licenses/apache-2.0/LICENSE.txt) |
| xunit.runner.visualstudio | 3.1.4 | [Copyright/license notice and included MIT code notice](licenses/xunit.runner.visualstudio-3.1.4/License.txt), [full Apache 2.0 license](licenses/apache-2.0/LICENSE.txt) |

Embedded package licenses/notices were copied byte-for-byte. Missing embedded licenses were retrieved only from explicit public license URLs, pinned to the package's declared repository commit. The older `xunit.abstractions` nuspec declares `xunit/xunit/master/license.txt` without a source commit: its declared license URL was resolved to immutable license snapshot `c54cc52ffb275c81afed022521870193bbca6c39`. That is a license snapshot pin, not a claim about the package's source commit. The full Apache 2.0 text comes from the Apache Software Foundation's license URL. Exact URLs and content hashes are in the inventory.

## Separately selected inference runtimes and models

This publication does not redistribute model weights or inference-runtime archives. `Vemo.Provision` downloads a user's selected artifacts directly from the upstream pinned HTTPS URLs and verifies the pinned size/SHA-256. Keep all dependency and license files from each extracted archive together. Hash verification is not code signing or a license grant.

- whisper.cpp: ggml-org/whisper.cpp release **b5454**, CPU Windows x64/ARM64 archives. The v1.9.5 release had no attached binary assets when verified. Upstream source: https://github.com/ggml-org/whisper.cpp (MIT project license). Runtime archive hashes are recorded in `runtime-catalogue.json`; third-party component terms remain applicable.
- llama.cpp: ggml-org/llama.cpp release **b11714**, CPU Windows x64/ARM64 archives. Upstream source: https://github.com/ggml-org/llama.cpp (MIT project license). Preserve `LICENSE-LLVM-OpenMP` and other notices included in upstream archives.
- Whisper models: ggerganov/whisper.cpp revision **5359861c739e955e79d9a303bcbc70fb988958b1**, `ggml-small.bin` / `ggml-large-v3-turbo.bin`. Model repository: https://huggingface.co/ggerganov/whisper.cpp . Review the repository's model card and upstream OpenAI Whisper license before use or redistribution.
- Qwen3.5 GGUF models: unsloth/Qwen3.5-9B-GGUF revision **3885219b6810b007914f3a7950a8d1b469d598a5**, and unsloth/Qwen3.5-27B-GGUF revision **3221f178a6b842d04f1fb42f1c413534adcc0a6a**, Q4_K_M files. Review the exact repositories' model cards/licenses and base-model terms before use; this preview does not make an independent license-compliance claim.
- FFmpeg: optional x64 LGPL **n8.1.3-18-g46d899d7a0**, BtbN/FFmpeg-Builds fixed release **autobuild-2026-10-16-13-04**, selected with `--download-ffmpeg`. The provider is linked from ffmpeg.org. SHA-256 **186cb8509a86ca2eb6146870661b69a224ccd486f384f6a5f441fa775f79cc04**, 170,544,161 bytes. Complete URL and pin are in `runtime-catalogue.json`. Preserve every extracted license/source/build-information file. No FFmpeg binary is redistributed by this publication. No native ARM64 FFmpeg pin is available: choose an existing matching-architecture native executable or WAV-only smoke testing. No x64 emulation is silently selected.
- Avalonia and .NET dependencies are redistributed with the portable builds; their collected licenses/notices are listed above. Portable app builds are unsigned; publisher identity remains a release-owner responsibility.

The full URLs, byte sizes, and SHA-256 values live in `runtime-catalogue.json` and `model-catalogue.json`. Existing user-selected native executables are explicitly recorded as **unpinned** with their observed SHA-256; matching architecture does not prove runtime provenance or compatibility. CPU selection makes no measured performance/quality claim. No CUDA runtime is provisioned.
