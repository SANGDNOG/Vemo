"""Bounded local notes units with exact source evidence and durable checkpoints."""
import hashlib
import json
import re
import unicodedata
from bisect import bisect_right

CHUNK_CHARS=1800
SOURCE_TOKENS=3000
# Exact citations can repeat across summary, highlights and keyword cards.
# Keep a hard bound while allowing those validated JSON objects to close.
OUTPUT_TOKENS=3072

def source_digest(source):
    return hashlib.sha256(source.encode('utf-8')).hexdigest()

def source_chunks(source,limit=CHUNK_CHARS,token_count=None):
    """Greedy sentence boundaries retain every character, including separators.

    The worker uses token_count inside each durable unit to bound model calls;
    queue progress remains deterministic without loading a tokenizer in Engine.
    """
    if not isinstance(source,str) or not source.strip():raise ValueError('empty_notes_source')
    if type(limit) is not int or limit<1:raise ValueError('invalid_notes_chunk_limit')
    boundaries=[m.end() for m in re.finditer(r'(?<=[.!?。！？])[ \t\r\n]+|[\r\n]+',source)]
    chunks=[];start=0
    while start<len(source):
        end=min(start+limit,len(source))
        if token_count is not None:
            while token_count(source[start:end])>SOURCE_TOKENS:
                if end-start<=1:raise ValueError('notes_token_budget_exceeded')
                end=start+(end-start)//2
        if end<len(source):
            index=bisect_right(boundaries,end)-1
            if index>=0 and boundaries[index]>start+(end-start)//2:end=boundaries[index]
        chunk=source[start:end]
        # Tokenizers can merge across a boundary; check the chosen sentence cut.
        if token_count is not None and token_count(chunk)>SOURCE_TOKENS:
            end=start+(end-start)//2;chunk=source[start:end]
            while token_count(chunk)>SOURCE_TOKENS:
                if end-start<=1:raise ValueError('notes_token_budget_exceeded')
                end=start+(end-start)//2;chunk=source[start:end]
        chunks.append(chunk);start=end
    return chunks

def source_passages(source):
    """Stable numbered evidence; retain every source character without model copying."""
    return source_chunks(source,limit=400)

def decode_notes_lines(raw,source,item_limit=8):
    """The model supplies keyword | summary | source IDs; Python owns JSON syntax."""
    if not isinstance(raw,str):raise ValueError('invalid_notes_lines')
    lines=raw.strip().splitlines()
    if not 1<=len(lines)<=item_limit:raise ValueError('invalid_notes_lines')
    passages=source_passages(source)
    result={'summary':[],'highlights':[],'topics':[]}
    for line in lines:
        fields=line.split('|')
        if len(fields)!=3:raise ValueError('invalid_notes_lines')
        keyword,summary,references=[field.strip() for field in fields]
        if not keyword or len(keyword)>80 or not summary or len(summary)>1000:
            raise ValueError('invalid_notes_items')
        if not re.fullmatch(r'[0-9]+(?:\s*,\s*[0-9]+)*',references):
            raise ValueError('invalid_notes_references')
        ids=[int(value) for value in references.split(',')]
        if not 1<=len(ids)<=8 or any(not 1<=value<=len(passages) for value in ids):
            raise ValueError('invalid_notes_references')
        quotes=[passages[value-1] for value in dict.fromkeys(ids)]
        result['summary'].append({'text':summary,'quotes':quotes})
        result['topics'].append({'keyword':keyword,'summary':summary,'quotes':quotes})
        result['highlights'].append({'quote':quotes[0]})
    return validate_notes(result,source,item_limit=item_limit)

def decode_notes_json(raw):
    try:return json.loads(raw)
    except json.JSONDecodeError as error:
        # Some complete model responses misorder only the final closing brackets.
        # Normalize that bounded suffix without changing any key, value or quote.
        # Truncation, malformed strings, interior errors and extra text still fail.
        suffix=raw[error.pos:]
        if (error.msg!="Expecting ',' delimiter" or not raw.startswith('{')
                or not raw.endswith('}') or not 0<len(suffix)<=16
                or suffix[0] not in ']}' or not re.fullmatch(r'[\s\]}]+',suffix)):
            raise
        stack=[];quoted=False;escaped=False
        for char in raw[:error.pos]:
            if quoted:
                if escaped:escaped=False
                elif char=='\\':escaped=True
                elif char=='"':quoted=False
            elif char=='"':quoted=True
            elif char in '[{':stack.append(char)
            elif char in ']}':
                if not stack or {'[':']','{':'}'}[stack[-1]]!=char:raise error
                stack.pop()
        if quoted or escaped or not stack or stack[0]!='{' or len(stack)>16:raise error
        closing=''.join({'[':']','{':'}'}[char] for char in reversed(stack))
        if any(suffix.count(char)<closing.count(char) for char in ']}'):raise error
        return json.loads(raw[:error.pos]+closing)

def validate_notes(raw,source,item_limit=None):
    if isinstance(raw,str):
        raw=raw.strip()
        if raw.startswith('```json\n') and raw.endswith('\n```'):raw=raw[8:-4]
        raw=decode_notes_json(raw)
    if not isinstance(raw,dict):raise ValueError('invalid_notes_json')
    digest=source_digest(source)
    if 'source_sha256' in raw and raw['source_sha256']!=digest:raise ValueError('notes_source_mismatch')
    if 'schema_version' in raw and (type(raw['schema_version']) is not int or raw['schema_version']!=1):raise ValueError('invalid_notes_schema')
    result={'schema_version':1,'source_sha256':digest,'summary':[],'highlights':[],'topics':[]}
    def text(value,limit):return isinstance(value,str) and bool(value.strip()) and len(value)<=limit
    def quote(value):return text(value,600) and value in source
    for kind in ('summary','highlights','topics'):
        items=raw.get(kind)
        if not isinstance(items,list) or (item_limit is not None and len(items)>item_limit):raise ValueError('invalid_notes_items')
        for item in items:
            if not isinstance(item,dict):continue
            if kind=='highlights':
                if quote(item.get('quote')):result[kind].append({'quote':item['quote']})
                continue
            quotes=item.get('quotes')
            if not isinstance(quotes,list) or len(quotes)>8:continue
            quotes=list(dict.fromkeys(q for q in quotes if quote(q)))
            field='text' if kind=='summary' else 'summary'
            if not quotes or not text(item.get(field),1000):continue
            if kind=='topics' and not text(item.get('keyword'),80):continue
            value={field:item[field],'quotes':quotes}
            if kind=='topics':value['keyword']=item['keyword']
            result[kind].append(value)
    if not any(result[k] for k in ('summary','highlights','topics')):raise ValueError('empty_notes_generation')
    return result

def finalize_notes(raw,source):
    """Keep first-seen order and spelling; combine evidence in bounded cards."""
    notes=validate_notes(raw,source)
    def key(text):return unicodedata.normalize('NFC',text).casefold().strip()
    def evidence(first,second):return list(dict.fromkeys(first+second))[:8]
    summaries={};topics={};highlights={}
    for item in notes['summary']:
        identity=key(item['text'])
        if identity in summaries:summaries[identity]['quotes']=evidence(summaries[identity]['quotes'],item['quotes'])
        else:summaries[identity]=dict(item)
    for item in notes['highlights']:highlights.setdefault(item['quote'],item)
    for item in notes['topics']:
        identity=key(item['keyword'])
        if identity not in topics:topics[identity]=dict(item);continue
        prior=topics[identity]
        if key(item['summary']) not in {key(t) for t in prior['summary'].split('\n')}:
            prior['summary']=(prior['summary']+'\n'+item['summary'])[:1000].rstrip()
        prior['quotes']=evidence(prior['quotes'],item['quotes'])
    notes.update(summary=list(summaries.values()),highlights=list(highlights.values()),topics=list(topics.values()))
    return validate_notes(notes,source)

def notes_unit(source,state,produce):
    """One bounded chunk per process; no prefix truncation or model overlap."""
    chunks=source_chunks(source);digest=source_digest(source)
    if state is None:
        index=0;acc={'schema_version':1,'source_sha256':digest,'summary':[],'highlights':[],'topics':[]}
    else:
        if not isinstance(state,dict) or state.get('source_sha256')!=digest or state.get('total_units')!=len(chunks):raise ValueError('invalid_notes_checkpoint')
        index=state.get('index')
        if type(index) is not int or not 0<index<len(chunks):raise ValueError('invalid_notes_checkpoint')
        acc=validate_notes(state.get('notes'),source)
    unit=validate_notes(produce(chunks[index],OUTPUT_TOKENS),chunks[index],item_limit=64)
    for kind in ('summary','highlights','topics'):acc[kind].extend(unit[kind])
    index+=1
    if index==len(chunks):return {'notes':finalize_notes(acc,source),'completed_units':index,'total_units':len(chunks)}
    return {'notes_checkpoint':{'source_sha256':digest,'index':index,'total_units':len(chunks),'notes':acc},'completed_units':index,'total_units':len(chunks)}

def notes_markdown(title,notes):
    lines=['# '+title,'','AI가 생성한 노트 · 인용은 원문과 대조했습니다.','','## 핵심 요약','']
    for item in notes['summary']:
        lines.extend(['- '+item['text']]+['  > '+q.replace('\n','\n  > ') for q in item['quotes']])
    lines.extend(['','## 중요한 원문',''])
    for item in notes['highlights']:lines.extend(['> '+item['quote'].replace('\n','\n> '),''])
    lines.extend(['## 키워드별 정리',''])
    for item in notes['topics']:
        lines.extend(['### '+item['keyword'],'',item['summary'],'']+['> '+q.replace('\n','\n> ') for q in item['quotes']]+[''])
    return '\n'.join(lines)+'\n'
