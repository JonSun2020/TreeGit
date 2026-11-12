"""Wait for the GUI-subsystem model probe and reject missing or stale results."""
import json
import os
from pathlib import Path
import subprocess
import sys
from qa_cleanup import remove_fixture

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, fixture, report = (Path(arg).resolve() for arg in sys.argv[1:4])
assert exe.is_file() and not fixture.exists() and not report.exists()
report.parent.mkdir(parents=True, exist_ok=True)
try:
    result = subprocess.run([str(exe), '--integration-ui-consistency', str(fixture), str(report)],
                            capture_output=True, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, (result.returncode, result.stderr.decode('utf-8', 'replace'))
    assert report.is_file(), 'The model probe exited without producing its required results'
    data = json.loads(report.read_text(encoding='utf-8-sig'))
    assert not data.get('error') and data['passed'] >= 63 and len(data['checks']) == data['passed'], data
    print('PASS verified UI-model results', data['passed'], flush=True)
finally:
    remove_fixture(fixture, fixture.parent, fixture.name)
