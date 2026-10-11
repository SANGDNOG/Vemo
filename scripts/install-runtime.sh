#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
runtime_dir="${1:-$HOME/Library/Application Support/VoiceMemoTranscriber/runtime}"
python_path="${VOICE_MEMO_BOOTSTRAP_PYTHON:-/opt/homebrew/bin/python3.12}"
if [[ ! -x "$python_path" ]]; then
  printf '%s\n' 'Homebrew Python 3.12 is required; install it explicitly before bootstrap.' >&2
  exit 1
fi
"$python_path" -c 'import sys; assert sys.version_info[:2] == (3,12), "Python 3.12 required"'
if [[ ! -x "$runtime_dir/bin/python3" ]]; then
  "$python_path" -m venv "$runtime_dir"
fi
"$runtime_dir/bin/python3" -m pip install -r "$project_dir/requirements.lock"
"$runtime_dir/bin/python3" -m pip install --no-deps --force-reinstall "$project_dir"
"$runtime_dir/bin/python3" -c 'import mlx.core, mlx_qwen3_asr, mlx_lm, voice_memo_transcriber'
printf '%s\n' "Runtime ready: $runtime_dir/bin/python3"
