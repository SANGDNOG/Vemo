# Third-party components

Vemo's own source and vector icon are MIT licensed. Bundled and downloaded components retain their own licenses.

## Bundled runtime

- CPython 3.12.15 from [python-build-standalone release 20261009](https://github.com/astral-sh/python-build-standalone/releases/tag/20261009), Python Software Foundation license. Included under `Contents/Resources/runtime/lib/python3.12/LICENSE.txt`.
- License texts for standalone Python's embedded libraries (including OpenSSL, SQLite, zlib, libffi, Tcl and compression libraries) are retained unchanged under `Contents/Resources/runtime/licenses/python-standalone/licenses/`. Their source archive and verified checksum are recorded in `third-party/python-standalone/PROVENANCE.json`.
- [MLX](https://github.com/ml-explore/mlx) and [mlx-lm](https://github.com/ml-explore/mlx-lm), MIT.
- [mlx-qwen3-asr](https://github.com/moona3k/mlx-qwen3-asr), Apache-2.0.
- Other Python dependencies are listed in `requirements-runtime.lock` and bundled `requirements-resolved.txt`. Distribution license files are retained under `Contents/Resources/runtime/lib/python3.12/site-packages/*.dist-info/` (including `licenses/` where supplied), including the scientific libraries' third-party notices.
- Audio decoding uses Apple's AVFoundation system framework. The DMG does not redistribute FFmpeg. The development-only fallback may use a separately installed FFmpeg.

## Models downloaded separately

No model weights are in the DMG. Vemo downloads the pinned Qwen3-ASR-1.7B, Qwen3-ForcedAligner-0.6B and Qwen3-4B-Instruct-2507 models on request. Model licenses are available in the upstream repositories; quantized community weights retain the originating model's license. See [ASR](https://huggingface.co/Qwen/Qwen3-ASR-1.7B), [ForcedAligner](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B), [title model](https://huggingface.co/mlx-community/Qwen3-4B-Instruct-2507-4bit), and optional [detailed notes model](https://huggingface.co/mlx-community/Qwen3.5-27B-4bit) ([Qwen3.5-27B upstream](https://huggingface.co/Qwen/Qwen3.5-27B), Apache-2.0). The optional notes revision is `45797d2985a12c55e6473686e9ea91b95e959553` and is downloaded separately from the transcription models.

The release provenance records exact standalone Python and engine wheel checksums.

## Optional speaker analysis

- [sherpa-onnx 1.13.8](https://github.com/k2-fsa/sherpa-onnx/tree/v1.13.8), Apache-2.0; ONNX Runtime retains its bundled license notices.
- Hash-pinned pyannote segmentation-3.0 ONNX (MIT, CNRS) and 3D-Speaker eres2net base embeddings (Apache-2.0) are downloaded separately on request. Model provenance and full license texts are in [MODEL_NOTICES.txt](src/voice_memo_transcriber/MODEL_NOTICES.txt), also retained alongside installed models.
