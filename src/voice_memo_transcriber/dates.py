import datetime as dt
import re
from zoneinfo import ZoneInfo

def validate_day(value):
    if not isinstance(value,str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}',value):
        raise ValueError('recording_date must be YYYY-MM-DD')
    return dt.date.fromisoformat(value).isoformat()

def recording_day(value, original_timezone, fallback='Asia/Seoul'):
    if value is None:
        return None
    if not isinstance(value,str):
        raise ValueError('recorded_at must be timezone-aware ISO8601')
    instant = dt.datetime.fromisoformat(value.replace('Z','+00:00'))
    if instant.tzinfo is None or instant.utcoffset() is None:
        raise ValueError('recorded_at requires a timezone')
    # Mobile recordings know their capture offset without necessarily knowing an
    # IANA location. Preserve that offset rather than inventing a geographical zone.
    name = original_timezone or fallback
    if name.startswith(('UTC+', 'UTC-')):
        match = re.fullmatch(r'UTC([+-])([0-9]{2}):([0-9]{2})', name)
        if not match: raise ValueError('invalid_fixed_timezone')
        hours, minutes = int(match[2]), int(match[3])
        if hours > 14 or minutes >= 60 or (hours == 14 and minutes):
            raise ValueError('invalid_fixed_timezone')
        offset = (hours * 60 + minutes) * (-1 if match[1] == '-' else 1)
        zone = dt.timezone(dt.timedelta(minutes=offset))
    else:
        zone = ZoneInfo(name)
    return instant.astimezone(zone).date().isoformat()

def apple_epoch_iso(seconds):
    return (dt.datetime(2001,1,1,tzinfo=dt.timezone.utc)+dt.timedelta(seconds=float(seconds))).isoformat()
