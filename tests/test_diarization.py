"""Pure contract tests: no runtime import, inference, model download or live store."""
import copy
import hashlib
import uuid
import pytest
from voice_memo_transcriber.diarization import build_document, validate_document, rename_speaker

AUDIO = 'a' * 64
TEXT = '안녕 세상'
SEGMENTS = [{'start_s': 0, 'end_s': 2, 'text': '안녕 '}, {'start_s': 2, 'end_s': 4, 'text': '세상'}]

def document():
    return build_document(AUDIO, TEXT, SEGMENTS, [
        {'start_s': 2, 'end_s': 4, 'speaker': 9},
        {'start_s': 0, 'end_s': 2, 'speaker': 7}])

def test_first_onset_ids_and_exact_hash():
    doc = document()
    assert doc['speakers'] == [{'id': 'speaker_1', 'name': '화자 1'}, {'id': 'speaker_2', 'name': '화자 2'}]
    assert [s['speaker_id'] for s in doc['segments']] == ['speaker_1', 'speaker_2']
    assert doc['transcript_sha256'] == hashlib.sha256(TEXT.encode()).hexdigest()
    uuid.UUID(doc['generation'])
    assert validate_document(doc, AUDIO, TEXT) == doc

@pytest.mark.parametrize('mutation', [
    lambda d: d.update(audio_sha256='b'*64),
    lambda d: d.update(transcript_sha256='b'*64),
    lambda d: d['turns'][0].update(end_s=float('nan')),
    lambda d: d['segments'][0].update(speaker_id='missing'),
    lambda d: d['speakers'].append(copy.copy(d['speakers'][0])),
    lambda d: d['segments'][0].update(text='fabricated'),
    lambda d: d['turns'][0].update(end_s=3601),
    lambda d: d['speakers'][0].update(name='bad\nname'),
])
def test_reject_invalid_or_stale_sidecar(mutation):
    doc = document(); mutation(doc)
    with pytest.raises(ValueError): validate_document(doc, AUDIO, TEXT)

def test_overlap_and_exact_half_remain_unassigned():
    doc = build_document(AUDIO, TEXT, SEGMENTS, [
        {'start_s': 0, 'end_s': 2, 'speaker': 0},
        {'start_s': 1.9, 'end_s': 2.1, 'speaker': 1},
        {'start_s': 3, 'end_s': 4, 'speaker': 0}])
    assert all(s['speaker_id'] is None for s in doc['segments'])

def test_names_edit_without_mutating_input_and_rerun_resets_names():
    old = document()
    renamed = rename_speaker(old, 'speaker_1', '  민수  ', AUDIO, TEXT)
    assert renamed['speakers'][0]['name'] == '민수'
    assert old['speakers'][0]['name'] == '화자 1'
    assert renamed['generation'] == old['generation']
    assert document()['speakers'][0]['name'] == '화자 1'

def test_incomplete_alignment_never_fabricates_boundaries():
    with pytest.raises(ValueError): build_document(AUDIO, TEXT, SEGMENTS[:1], [])

@pytest.mark.parametrize('generation', [uuid.uuid4().hex, str(uuid.uuid4()).upper()])
def test_generation_is_canonical_uuid(generation):
    doc = document(); doc['generation'] = generation
    with pytest.raises(ValueError): validate_document(doc, AUDIO, TEXT)

def test_duplicate_json_keys_rejected():
    from voice_memo_transcriber.diarization import decode_document
    with pytest.raises(ValueError): decode_document(b'{"schema_version":1,"schema_version":1}')
    with pytest.raises(ValueError): decode_document(b'{"nested":{"name":"a","name":"b"}}')

def test_unsorted_segment_boundaries_rejected():
    doc = document(); doc['segments'].reverse()
    with pytest.raises(ValueError): validate_document(doc, AUDIO, TEXT)

def test_resource_guards_are_optional_and_bounded():
    from voice_memo_transcriber.diarization_runtime import guard, DiarizationError
    guard(3600, 4*1024**3)
    for duration, memory, message in [(3600.1, 4*1024**3, 'duration'), (0, 4*1024**3, 'duration'), (1, 4*1024**3-1, 'memory')]:
        with pytest.raises(DiarizationError, match=message): guard(duration, memory)


def test_unknown_turn_overlap_prevents_known_assignment():
    doc = build_document(AUDIO, 'hello', [{'start_s': 0, 'end_s': 2, 'text': 'hello'}], [
        {'start_s': 0, 'end_s': 2, 'speaker': 0}, {'start_s': 1, 'end_s': 2, 'speaker': -1}])
    assert doc['segments'][0]['speaker_id'] is None
    assert len(doc['turns']) == 1


def test_source_extras_are_not_exported_and_document_extras_rejected():
    doc = build_document(AUDIO, 'hello', [{'start_s': 0, 'end_s': 2, 'text': 'hello', 'confidence': 0.9}],
                         [{'start_s': 0, 'end_s': 2, 'speaker': 0}])
    assert set(doc['segments'][0]) == {'start_s', 'end_s', 'text', 'speaker_id'}
    for target in (doc, doc['speakers'][0], doc['turns'][0], doc['segments'][0]):
        target['extra'] = True
        with pytest.raises(ValueError): validate_document(doc, AUDIO, 'hello')
        del target['extra']


def test_known_speaker_count_contract():
    from voice_memo_transcriber.diarization import speaker_count
    assert speaker_count() == -1
    assert speaker_count(None) == -1
    for value in range(1, 21): assert speaker_count(value) == value
    for value in (0, -1, 21, True, False, 2.0, '2'):
        with pytest.raises(ValueError): speaker_count(value)


def test_automatic_cluster_limit_requires_explicit_count():
    from voice_memo_transcriber.diarization_runtime import check_auto_clusters, DiarizationError
    turns = [{'speaker': i} for i in range(20)]
    check_auto_clusters(turns + [{'speaker': -1}, {'speaker': 0}], -1)
    with pytest.raises(DiarizationError, match='diarization_too_many_speakers'):
        check_auto_clusters(turns + [{'speaker': 20}], -1)
    check_auto_clusters(turns, 20)


def test_pinned_sherpa_frame_end_is_clipped_only_within_receptive_field_bound():
    from voice_memo_transcriber.diarization_runtime import normalize_turn_bounds, FRAME_END_TOLERANCE
    duration = 700348 / 16000
    start = 0.03096874989569187
    assert normalize_turn_bounds(start, 43.787845611572266, duration) == (start, duration)
    assert normalize_turn_bounds(start, duration, duration) == (start, duration)
    assert normalize_turn_bounds(start, 40.0, duration) == (start, 40.0)
    assert normalize_turn_bounds(0, duration + FRAME_END_TOLERANCE, duration) == (0, duration)
    # Clipping also preserves the public schema's hard 3600-second ceiling.
    assert normalize_turn_bounds(3599, 3600.016, 3600) == (3599, 3600)


@pytest.mark.parametrize('start,end,duration', [
    (0, 43.82, 43.77175), (0, 43.77175 + 991/32000 + 0.001, 43.77175),
    (-0.001, 1, 2), (2.001, 2.01, 2), (2, 2.01, 2), (1, 0.9, 2),
    (float('nan'), 1, 2), (0, float('nan'), 2), (0, float('inf'), 2),
    (float('-inf'), 1, 2), (0, 1, float('nan')), (0, 1, float('inf')),
    (0, 0, 0), (0, 1, 3601),
])
def test_frame_end_normalization_rejects_invalid_or_materially_outside_bounds(start, end, duration):
    from voice_memo_transcriber.diarization_runtime import normalize_turn_bounds, DiarizationError
    with pytest.raises(DiarizationError, match='diarization_failed'):
        normalize_turn_bounds(start, end, duration)
