import re
import unicodedata
from .dates import validate_day

def sanitize_topic(value):
    value=unicodedata.normalize('NFC',str(value or ''))
    value=re.sub(r'\s*[—–-]?\s*\d{4}-\d{2}-\d{2}\s*$','',value)
    value=''.join(' ' if c in '/\\:*?"<>|' or unicodedata.category(c).startswith('C') else c for c in value)
    value=' '.join(value.split()).strip(' .—–-\'`#')[:40].rstrip()
    return value or '음성 녹음'

def display_title(topic, day):
    return sanitize_topic(topic)+' — '+validate_day(day)

def token_chunks(tokens, limit=3072):
    if limit < 1:
        raise ValueError('invalid token limit')
    return [tokens[i:i+limit] for i in range(0,len(tokens),limit)] or [[]]

def hierarchical_topic(tokens, hint, final, encode, limit=3072):
    """Every input token participates; reductions are bounded and greedy at the caller."""
    chunks=token_chunks(tokens,limit)
    if len(chunks)==1:
        return sanitize_topic(final(chunks[0],64))
    hints=[hint(chunk,96) for chunk in chunks]
    for _ in range(16):
        reduced=encode('\n'.join(hints))
        if len(reduced)<=limit:
            return sanitize_topic(final(reduced,64))
        hints=[hint(chunk,96) for chunk in token_chunks(reduced,limit)]
    raise RuntimeError('title reduction did not converge')
