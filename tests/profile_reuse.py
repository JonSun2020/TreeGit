"""An active application prevents profile replacement; closed profiles restore exactly."""
import json
import os
import subprocess
import sys
from pathlib import Path
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows', 'Profile tests require an owned CI runner'

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
original = exe.parent / 'data' / 'preference.json'
before = original.read_bytes() if original.exists() else None
backups = set(exe.parent.glob('TreeGit saved profile *'))
checks = []

# A bridge with no input remains idle and never launches a GUI or a Git mutation.
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     creationflags=subprocess.CREATE_NO_WINDOW)
try:
    assert p.poll() is None
    try:
        IsolatedProfile(exe, out)
    except subprocess.CalledProcessError:
        checks.append('active application rejects profile replacement')
    else:
        raise AssertionError('An active application allowed profile replacement')
finally:
    p.stdin.close()
    try: p.wait(timeout=15)
    except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=10)
assert set(exe.parent.glob('TreeGit saved profile *')) == backups
assert (original.read_bytes() if original.exists() else None) == before
checks.append('rejected replacement leaves the original profile untouched')

profile = IsolatedProfile(exe, out)
try:
    first = profile.reset()
    (first / 'data' / 'preference.json').write_text('{"Locale":"zh_CN"}', encoding='utf-8')
    (first / 'data' / 'only-this-case.txt').write_text('temporary case', encoding='utf-8')
    second = profile.reset()
    assert second == first == exe.parent and not (second / 'data' / 'only-this-case.txt').exists()
    checks.append('successive cases reuse the same executable with fresh profile data')
finally:
    profile.close()
assert (original.read_bytes() if original.exists() else None) == before
assert set(exe.parent.glob('TreeGit saved profile *')) == backups
checks.append('original preferences return and temporary profile data is removed')
(out / 'profile-safety-results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks}, indent=2), encoding='utf-8')
print('PASS isolated portable profiles', len(checks), flush=True)
