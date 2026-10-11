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
    # An explicit native timezone takes priority; otherwise the configured zone.
    return instant.astimezone(ZoneInfo(original_timezone or fallback)).date().isoformat()

def apple_epoch_iso(seconds):
    return (dt.datetime(2001,1,1,tzinfo=dt.timezone.utc)+dt.timedelta(seconds=float(seconds))).isoformat()
