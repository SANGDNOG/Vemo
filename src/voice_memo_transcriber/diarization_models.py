"""Pinned ONNX data only. Never executes code from model repositories.

Pins: segmentation Hugging Face LFS metadata at the pinned commit below;
embedding SHA256 computed from the official sherpa-onnx release on 2026-10-18.
See MODEL_NOTICES.txt packaged alongside this module for licenses/provenance.
"""
from pathlib import Path
from .diarization_io import read_owned

MODELS = {
    'segmentation.onnx': {
        'url': 'https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx',
        'sha256': '220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079',
        'bytes': 5992913, 'license': 'MIT (Copyright 2022 CNRS)'},
    'embedding.onnx': {
        'url': 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx',
        'sha256': '1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b',
        'bytes': 39593761, 'license': 'Apache-2.0 (3D-Speaker, Alibaba)'},
}


def checked_models(model_root):
    root = Path(model_root).absolute(); directory = root / 'diarization'
    for name, pin in MODELS.items():
        if read_owned(root, directory / name, maximum=pin['bytes'], digest=True) != pin['sha256']:
            raise ValueError('diarization_model_hash_mismatch')
    return directory / 'segmentation.onnx', directory / 'embedding.onnx'


def install(model_root):
    import hashlib
    import importlib.resources
    import os
    import urllib.request
    import uuid
    from .store import owned_directory
    root = Path(model_root).absolute()
    root.mkdir(parents=True, exist_ok=True)
    with owned_directory(root) as parent:
        try: os.mkdir('diarization', 0o700, dir_fd=parent)
        except FileExistsError: pass
        os.fsync(parent)
    with owned_directory(root, ('diarization',)) as parent:
        for name, pin in MODELS.items():
            try:
                if read_owned(root, root/'diarization'/name, maximum=pin['bytes'], digest=True) == pin['sha256']: continue
            except (OSError, ValueError): pass
            temporary = '.' + uuid.uuid4().hex + '.tmp'
            try:
                fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=parent)
                digest = hashlib.sha256(); size = 0
                with os.fdopen(fd, 'wb') as out, urllib.request.urlopen(pin['url'], timeout=60) as response:
                    for block in iter(lambda: response.read(1024*1024), b''):
                        size += len(block)
                        if size > pin['bytes']: raise ValueError('diarization_model_size_mismatch')
                        out.write(block); digest.update(block)
                    if size != pin['bytes'] or digest.hexdigest() != pin['sha256']: raise ValueError('diarization_model_hash_mismatch')
                    out.flush(); os.fsync(out.fileno())
                os.replace(temporary, name, src_dir_fd=parent, dst_dir_fd=parent); os.fsync(parent)
            finally:
                try: os.unlink(temporary, dir_fd=parent)
                except FileNotFoundError: pass
        notices = importlib.resources.files('voice_memo_transcriber').joinpath('MODEL_NOTICES.txt').read_bytes()
        fd = os.open('MODEL_NOTICES.txt', os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=parent) if not os.path.exists(root/'diarization'/'MODEL_NOTICES.txt') else None
        if fd is not None:
            with os.fdopen(fd, 'wb') as out: out.write(notices); out.flush(); os.fsync(out.fileno())
            os.fsync(parent)


def main():
    import argparse
    parser = argparse.ArgumentParser(description='Install optional, hash-pinned local speaker models (no runtime installation).')
    parser.add_argument('--model-root', required=True)
    args = parser.parse_args()
    install(args.model_root)
    print('Optional speaker models verified. Install requirements-diarization.lock in the Vemo runtime separately.')


if __name__ == '__main__': main()
