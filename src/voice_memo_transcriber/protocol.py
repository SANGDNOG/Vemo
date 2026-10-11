import json
LIMIT=8*1024*1024
KINDS={'hello','snapshot','discover','import','enqueue','backfill','snapshot_ready','pause','resume','retry','retitle','set_recording_date','publish_ack','shutdown','download_models','set_policy'}

def parse_command(line):
    if len(line)>LIMIT:
        raise ValueError('line_too_large')
    def bad_constant(value):
        raise ValueError('nonfinite_json')
    obj=json.loads(line,parse_constant=bad_constant)
    if not isinstance(obj,dict): raise ValueError('invalid_command')
    if type(obj.get('protocol_version')) is not int or obj['protocol_version']!=1:
        raise ValueError('unsupported_protocol')
    if obj.get('kind') not in KINDS: raise ValueError('unknown_command')
    if not isinstance(obj.get('request_id'),str) or not 0<len(obj['request_id'])<=256:
        raise ValueError('invalid_request_id')
    if not isinstance(obj.get('payload'),dict): raise ValueError('invalid_payload')
    return obj
