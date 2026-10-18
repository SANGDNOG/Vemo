"""Isolated sherpa-onnx 1.13.8 operation. Imports ML only after resource checks."""
import os
import math
import re
import subprocess
import sys
import wave
from pathlib import Path
from .diarization import build_document, timing, normalized, speaker_count
from .diarization_io import sources, read_owned
from .diarization_models import checked_models, install

MESSAGES = {
    'diarization_runtime_missing': '화자 분석 구성 요소가 없습니다. 화자 분석이 포함된 최신 Vemo 앱으로 업데이트해 주세요.',
    'diarization_models_missing': '화자 모델을 준비하지 못했습니다. 인터넷 연결과 저장 공간을 확인한 뒤 화자 모델 준비를 다시 눌러 주세요.',
    'diarization_memory_low': '화자 분석에는 사용 가능한 메모리 4 GiB 이상이 필요합니다. 다른 앱을 닫은 뒤 다시 시도하세요.',
    'diarization_duration_limit': '화자 분석은 3600초 이하 녹음만 지원합니다. 녹음을 분할해 별도 화자로 처리하지 않습니다.',
    'diarization_alignment_required': '화자 표시에 원문과 일치하는 시간 정렬이 필요합니다. 전사를 다시 처리해 주세요.',
    'diarization_too_many_speakers': '자동 분석에서 화자가 20명을 초과했습니다. 화자 수를 지정해 다시 분석해 주세요. 기존 결과는 보존했습니다.',
    'diarization_failed': '화자 분석을 완료하지 못했습니다. 기존 전사·노트·화자 결과를 보존했습니다. 런타임과 모델을 확인하세요.',
}


class DiarizationError(ValueError):
    pass


# Hash-pinned pyannote metadata: receptive_field_size=991, shift=270,
# sample_rate=16000. sherpa 1.13.8 ComputeSpeakerCount retains floor(N/270)
# as the final frame; ComputeResult adds half the receptive field to its
# timestamp. Thus an active final frame can exceed N/16000 by at most
# 991/32000 seconds. Two float32 ULPs at the 3600 s duration limit
# add at most 0.48828125 ms for the multiply/add timestamp calculation.
FRAME_END_TOLERANCE = 991 / 32000 + 2 * 2**-12


def normalize_turn_bounds(start, end, duration):
    if (not all(math.isfinite(v) for v in (start, end, duration))
            or not 0 < duration <= 3600 or not 0 <= start <= end
            or start >= duration or end > duration + FRAME_END_TOLERANCE):
        raise DiarizationError('diarization_failed')
    return start, min(end, duration)


def check_auto_clusters(turns, num_clusters):
    if num_clusters == -1 and len({t['speaker'] for t in turns if type(t.get('speaker')) is int and t['speaker'] >= 0}) > 20:
        raise DiarizationError('diarization_too_many_speakers')


def available_memory():
    # Conservative reclaimable pages; never count compressed or swap space.
    try:
        if sys.platform == 'darwin':
            raw = subprocess.check_output(['/usr/bin/vm_stat'], timeout=3, stderr=subprocess.DEVNULL).decode()
            page_size = int(re.search(r'page size of (\d+) bytes', raw).group(1))
            values = [int(re.search(r'^' + key + r':\s+(\d+)', raw, re.M).group(1)) for key in ('Pages free', 'Pages inactive', 'Pages speculative')]
            return page_size * sum(values)
        return os.sysconf('SC_AVPHYS_PAGES') * os.sysconf('SC_PAGE_SIZE')
    except (OSError, ValueError, AttributeError, subprocess.SubprocessError): return 0


def guard(duration, memory=None):
    if not 0 < duration <= 3600: raise DiarizationError('diarization_duration_limit')
    if (available_memory() if memory is None else memory) < 4*1024**3: raise DiarizationError('diarization_memory_low')


def run(request):
    num_clusters = speaker_count(request.get('num_speakers'))
    root = Path(request['state_root']).absolute()
    # Validate WAV header/duration before allocating the recording or loading models.
    from .media import validate_owned
    pcm = validate_owned(root, request['pcm_path'])
    with wave.open(str(pcm), 'rb') as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != (1, 2, 16000, 'NONE'):
            raise DiarizationError('diarization_failed')
        frames = source.getnframes(); duration = frames/16000
    guard(duration)
    audio_hash, transcript = sources(root, request['audio_path'], request['artifact_path'])
    segments = transcript['segments']
    if len(segments) > 100000 or transcript.get('alignment_status') not in ('completed', 'success') or normalized(''.join(s['text'] for s in segments)) != normalized(transcript['text']):
        raise DiarizationError('diarization_alignment_required')
    previous = -1
    for s in segments:
        start, end = timing(s)
        if start < previous or end > duration: raise DiarizationError('diarization_alignment_required')
        previous = start
    try:
        from importlib.metadata import version
        if version('sherpa-onnx') != '1.13.8': raise ImportError('wrong version')
        import sherpa_onnx as sherpa
        import numpy as np
    except (ImportError, OSError): raise DiarizationError('diarization_runtime_missing') from None
    try:
        try: segmentation, embedding = checked_models(request['model_root'])
        except (OSError, ValueError):
            # Only reached by the user's explicit analysis command, inside the
            # cancellable helper and the shared ASR/notes execution slot.
            install(request['model_root'])
            segmentation, embedding = checked_models(request['model_root'])
    except (OSError, ValueError): raise DiarizationError('diarization_models_missing') from None
    guard(duration)
    config = sherpa.OfflineSpeakerDiarizationConfig(
        segmentation=sherpa.OfflineSpeakerSegmentationModelConfig(
            pyannote=sherpa.OfflineSpeakerSegmentationPyannoteModelConfig(model=str(segmentation)), num_threads=2, provider='cpu'),
        embedding=sherpa.SpeakerEmbeddingExtractorConfig(model=str(embedding), num_threads=2, provider='cpu'),
        clustering=sherpa.FastClusteringConfig(num_clusters=num_clusters, threshold=0.5),
        min_duration_on=0.3, min_duration_off=0.5)
    if not config.validate(): raise DiarizationError('diarization_models_missing')
    diarizer = sherpa.OfflineSpeakerDiarization(config)
    if diarizer.sample_rate != 16000: raise DiarizationError('diarization_failed')
    # Stable descriptor-relative read also rejects a replaced/symlinked PCM.
    import io
    with wave.open(io.BytesIO(read_owned(root, pcm, maximum=3600*16000*2+1024*1024)), 'rb') as source:
        if source.getnframes() != frames: raise DiarizationError('diarization_failed')
        samples = np.frombuffer(source.readframes(frames), dtype='<i2').astype(np.float32)/32768.0
    if len(samples) != frames: raise DiarizationError('diarization_failed')
    # Full-recording IDs: no independent chunk clustering. Stage-only progress is
    # honest; the runtime callback covers segmentation, not total clustering time.
    raw = diarizer.process(samples).sort_by_start_time()
    turns = []
    for turn in raw:
        start, end = normalize_turn_bounds(float(turn.start), float(turn.end), duration)
        turns.append(dict(start_s=start, end_s=end, speaker=int(turn.speaker)))
    check_auto_clusters(turns, num_clusters)
    return {'speakers': build_document(audio_hash, transcript['text'], segments, turns)}
