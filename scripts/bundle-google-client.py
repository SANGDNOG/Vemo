#!/usr/bin/env python3
"""Normalize an explicitly supplied public installed desktop OAuth client for packaging."""
import json
from pathlib import Path
import sys


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError()
        result[key] = value
    return result


def normalize(source):
    with source.open('rb') as stream:
        data = stream.read(65537)
    if len(data) > 65536:
        raise ValueError()
    value = json.loads(data, object_pairs_hook=unique_object)
    if not isinstance(value, dict) or set(value) != {'installed'}:
        raise ValueError()
    installed = value['installed']
    allowed = {'client_id', 'client_secret', 'project_id', 'auth_uri', 'token_uri',
               'auth_provider_x509_cert_url', 'redirect_uris'}
    if not isinstance(installed, dict) or not set(installed).issubset(allowed):
        raise ValueError()
    client = installed.get('client_id')
    suffix = '.apps.googleusercontent.com'
    if (not isinstance(client, str) or not client.endswith(suffix) or len(client) <= len(suffix)
            or len(client) > 1024 or any(c.isspace() or ord(c) < 32 or 127 <= ord(c) <= 159 for c in client)):
        raise ValueError()
    result = {'client_id': client}
    if 'client_secret' in installed:
        secret = installed['client_secret']
        if not isinstance(secret, str) or len(secret.encode('utf-8')) > 4096:
            raise ValueError()
        result['client_secret'] = secret
    return json.dumps({'installed': result}, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


if __name__ == '__main__':
    try:
        if len(sys.argv) != 3:
            raise ValueError()
        normalized = normalize(Path(sys.argv[1]))
        # Never replace a resource silently; the build always uses a fresh destination.
        with Path(sys.argv[2]).open('xb') as stream:
            stream.write(normalized)
    except (OSError, ValueError, TypeError, UnicodeError):
        sys.exit('Google desktop OAuth configuration rejected; expected a bounded installed-client JSON file.')
