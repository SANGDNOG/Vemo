"""Optional recording-scoped speaker metadata. No inference imports in this module."""
import bisect
import copy
import hashlib
import json
import math
import re
import unicodedata
import uuid

LIMIT = 8 * 1024 * 1024
MAX_ITEMS = 100000
MAX_SECONDS = 3600


def speaker_count(value=None):
    """Wire null/absence means automatic; explicit counts are integers 1..20."""
    if value is None: return -1
    if type(value) is not int or not 1 <= value <= 20:
        raise ValueError('invalid_speaker_count')
    return value


def transcript_hash(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def normalized(text):
    # Same presentation normalization as NativeCore.TranscriptDisplay.
    return ''.join(c for c in unicodedata.normalize('NFC', text)
                   if not c.isspace() and not unicodedata.category(c).startswith('P'))


def speaker_name(name):
    if not isinstance(name, str): raise ValueError('invalid_speaker_name')
    name = name.strip()
    if not 1 <= len(name) <= 80 or any(unicodedata.category(c).startswith('C') for c in name):
        raise ValueError('invalid_speaker_name')
    return name


def timing(item):
    if not isinstance(item, dict): raise ValueError('invalid_speaker_timing')
    start, end = item.get('start_s'), item.get('end_s')
    if any(type(n) not in (int, float) or not math.isfinite(n) for n in (start, end)) or not 0 <= start <= end <= MAX_SECONDS:
        raise ValueError('invalid_speaker_timing')
    return start, end


def assignments(segments, turns):
    """Union same-speaker turns, reject any competing overlap (even under 50%)."""
    events = {}
    for turn in turns:
        start, end = timing(turn)
        if end == start: continue
        for point, delta in ((start, 1), (end, -1)):
            events.setdefault(point, []).append((turn['speaker_id'], delta))
    active = {}; spans = []; previous = None
    for point in sorted(events):
        if previous is not None and point > previous and active:
            spans.append((previous, point, next(iter(active)) if len(active) == 1 else None))
        for speaker, delta in events[point]:
            active[speaker] = active.get(speaker, 0) + delta
            if not active[speaker]: del active[speaker]
        previous = point
    ends = [s[1] for s in spans]
    result = []; operations = 0
    for segment in segments:
        start, end = timing(segment); covered = {}; ambiguous = False
        index = bisect.bisect_right(ends, start)
        while index < len(spans) and spans[index][0] < end:
            left, right, speaker = spans[index]
            duration = min(end, right) - max(start, left)
            if duration > 0:
                if speaker is None: ambiguous = True
                else: covered[speaker] = covered.get(speaker, 0) + duration
            index += 1; operations += 1
            if operations > 2000000: raise ValueError('speaker_assignment_complexity_limit')
        result.append(next(iter(covered)) if not ambiguous and len(covered) == 1 and next(iter(covered.values())) > (end-start)/2 else None)
    return result


def validate_document(doc, audio_hash, text):
    if not isinstance(doc, dict) or len(json.dumps(doc, ensure_ascii=False, allow_nan=False).encode('utf-8')) > LIMIT:
        raise ValueError('invalid_speaker_document')
    if type(doc.get('schema_version')) is not int or doc['schema_version'] != 1:
        raise ValueError('invalid_speaker_schema')
    if set(doc) != {'schema_version','audio_sha256','transcript_sha256','generation','speakers','turns','segments'}:
        raise ValueError('invalid_speaker_keys')
    if not isinstance(audio_hash, str) or not re.fullmatch('[0-9a-f]{64}', audio_hash) or doc.get('audio_sha256') != audio_hash or doc.get('transcript_sha256') != transcript_hash(text):
        raise ValueError('stale_speaker_document')
    try:
        if str(uuid.UUID(doc['generation'])) != doc['generation']: raise ValueError('noncanonical_uuid')
    except (ValueError, KeyError, TypeError, AttributeError): raise ValueError('invalid_speaker_generation') from None
    speakers, turns, segments = (doc.get(k) for k in ('speakers', 'turns', 'segments'))
    if any(not isinstance(v, list) or len(v) > MAX_ITEMS for v in (speakers, turns, segments)):
        raise ValueError('invalid_speaker_lists')
    ids = set()
    for speaker in speakers:
        if not isinstance(speaker, dict): raise ValueError('invalid_speaker')
        if set(speaker) != {'id','name'}: raise ValueError('invalid_speaker_keys')
        sid = speaker.get('id')
        if not isinstance(sid, str) or not re.fullmatch(r'speaker_[1-9][0-9]*', sid) or sid in ids or speaker_name(speaker.get('name')) != speaker['name']:
            raise ValueError('invalid_speaker')
        ids.add(sid)
    for items, nullable in ((turns, False), (segments, True)):
        previous = -1
        for item in items:
            start, _ = timing(item)
            if set(item) != ({'start_s','end_s','text','speaker_id'} if nullable else {'start_s','end_s','speaker_id'}):
                raise ValueError('invalid_speaker_keys')
            if start < previous: raise ValueError('unsorted_speaker_timing')
            previous = start
            sid = item.get('speaker_id')
            if not (nullable and sid is None) and (not isinstance(sid, str) or sid not in ids): raise ValueError('invalid_speaker_reference')
    if any(not isinstance(s.get('text'), str) for s in segments) or normalized(''.join(s['text'] for s in segments)) != normalized(text):
        raise ValueError('speaker_transcript_mismatch')
    expected = assignments(segments, turns)
    if any(s['speaker_id'] is not None and s['speaker_id'] != sid for s, sid in zip(segments, expected)):
        raise ValueError('ambiguous_speaker_assignment')
    return doc


def build_document(audio_hash, text, segments, raw_turns):
    if not isinstance(segments, list) or not isinstance(raw_turns, list) or len(segments) > MAX_ITEMS or len(raw_turns) > MAX_ITEMS:
        raise ValueError('invalid_speaker_lists')
    # Full-recording clustering only. IDs are mapped by first onset, never per chunk.
    ordered = sorted(raw_turns, key=lambda t: timing(t))
    mapping = {}; speakers = []; turns = []
    for turn in ordered:
        cluster = turn.get('speaker')
        if type(cluster) is not int or cluster < 0: continue  # unknown stays unassigned
        if cluster not in mapping:
            sid = 'speaker_' + str(len(mapping)+1); mapping[cluster] = sid
            speakers.append({'id': sid, 'name': '화자 ' + str(len(mapping))})
        turns.append({'start_s': turn['start_s'], 'end_s': turn['end_s'], 'speaker_id': mapping[cluster]})
    # Unknown turns are omitted from the public schema, but still prevent a
    # known cluster from claiming speech over the same interval.
    unknown = [timing(t) for t in ordered if type(t.get('speaker')) is not int or t['speaker'] < 0]
    projected = []
    operations = 0
    for s, sid in zip(segments, assignments(segments, turns)):
        start, end = timing(s)
        for left, right in unknown:
            operations += 1
            if operations > 2000000: raise ValueError('speaker_assignment_complexity_limit')
            if left < end and right > start:
                sid = None
                break
        projected.append({'start_s': start, 'end_s': end, 'text': s['text'], 'speaker_id': sid})
    doc = dict(schema_version=1, audio_sha256=audio_hash, transcript_sha256=transcript_hash(text),
               generation=str(uuid.uuid4()), speakers=speakers, turns=turns, segments=projected)
    return validate_document(doc, audio_hash, text)


def rename_speaker(doc, sid, name, audio_hash, text):
    validate_document(doc, audio_hash, text)
    name = speaker_name(name); result = copy.deepcopy(doc)
    speaker = next((s for s in result['speakers'] if s['id'] == sid), None)
    if speaker is None: raise ValueError('unknown_speaker')
    speaker['name'] = name
    return validate_document(result, audio_hash, text)


def decode_document(data):
    if len(data) > LIMIT: raise ValueError('speaker_document_too_large')
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: raise ValueError('duplicate_speaker_json_key')
            result[key] = value
        return result
    def constant(_): raise ValueError('nonfinite_speaker_json')
    return json.loads(data, object_pairs_hook=pairs, parse_constant=constant)
