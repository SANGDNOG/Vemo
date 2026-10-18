#!/usr/bin/env python3
"""Synthetic-only packaging boundary checks; never reads developer credentials."""
import json
from pathlib import Path
import subprocess
import tempfile

helper = Path(__file__).resolve().parents[3] / 'scripts/bundle-google-client.py'
with tempfile.TemporaryDirectory() as scratch:
    root = Path(scratch)
    source, output = root / 'input.json', root / 'GoogleOAuthClient.json'
    installed = {'client_id': 'synthetic.apps.googleusercontent.com', 'client_secret': 'synthetic-public',
                 'project_id': 'discard', 'token_uri': 'https://evil.invalid', 'redirect_uris': ['http://localhost']}
    cases = [({'installed': installed}, True), ({'web': installed}, False),
             ({'installed': dict(installed, refresh_token='forbidden')}, False),
             ({'installed': installed, 'access_token': 'forbidden'}, False),
             ({'installed': dict(installed, private_key='forbidden')}, False),
             ({'installed': dict(installed, unexpected='forbidden')}, False),
             ({'installed': dict(installed, client_id='wrong')}, False),
             ({'installed': dict(installed, client_secret=None)}, False)]
    for value, valid in cases:
        source.write_text(json.dumps(value))
        result = subprocess.run(['python3', str(helper), str(source), str(output)], capture_output=True)
        assert (result.returncode == 0) == valid, 'normalizer acceptance mismatch'
        assert b'synthetic' not in result.stdout + result.stderr, 'credential values printed'
        if valid:
            assert json.loads(output.read_text()) == {'installed': {'client_id': 'synthetic.apps.googleusercontent.com', 'client_secret': 'synthetic-public'}}
            output.unlink()
        else:
            assert not output.exists(), 'invalid config emitted'
    for content in ['x' * 65537, '{invalid', '{"installed":{"client_id":"synthetic.apps.googleusercontent.com","client_id":"other.apps.googleusercontent.com"}}']:
        source.write_text(content)
        assert subprocess.run(['python3', str(helper), str(source), str(output)], capture_output=True).returncode != 0
        assert not output.exists()
print('Packaging: 11 synthetic checks passed')
