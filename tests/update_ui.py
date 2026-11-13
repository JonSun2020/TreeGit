"""A GUI exit code alone cannot prove that the update-window checks ran."""
import json
import os
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path.cwd() / 'tests'))
from qa_cleanup import remove_fixture

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, archive, output = (Path(arg).resolve() for arg in sys.argv[1:4])
assert exe.is_file() and archive.is_file() and not output.exists()
try:
    result = subprocess.run([str(exe), '--integration-update-ui', str(archive), str(output)],
                            capture_output=True, timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
    report = output / 'results.json'
    diagnostic = json.loads(report.read_text(encoding='utf-8-sig')) if report.is_file() else {}
    assert result.returncode == 0, (result.returncode, diagnostic.get('failure'), result.stderr.decode('utf-8', 'replace'))
    assert report.is_file(), 'The update-window application exited without producing its required check results'
    data = json.loads(report.read_text(encoding='utf-8-sig'))
    assert data['failure'] is None and data['passed'] >= 20 and len(data['checks']) == data['passed'], data
    assert any('unsavable preference' in check for check in data['checks'])
    assert any('offline checking' in check for check in data['checks'])
    print('PASS verified update-window results', data['passed'], flush=True)
finally:
    remove_fixture(output / 'installation', output, 'installation')
