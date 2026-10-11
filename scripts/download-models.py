#!/usr/bin/env python3
"""Repo bootstrap entrypoint; the same implementation ships in the wheel."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from voice_memo_transcriber.download_models import PINS,observed_bytes,verified_files,main
if __name__=='__main__':sys.exit(main())
