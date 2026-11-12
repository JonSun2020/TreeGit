"""Recheck historical TreeGit fixes against an exact source in an owned CI desktop."""
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.name == 'posix'
repo, out = (Path(value).resolve() for value in sys.argv[1:3])
assert repo.is_dir() and not out.exists()
out.mkdir(parents=True)
sys.path.insert(0, str(repo / 'tests'))
from qa_cleanup import remove_fixture

exe = repo / 'src/bin/Debug/net10.0/SourceGit'
assert exe.is_file()
scratch = out / 'scratch'
scratch.mkdir()
marker = 'treegit-historical-regressions-' + str(os.getpid())
env = {**os.environ, 'TREEGIT_REGRESSION_RUN': marker, 'APPIMAGE': str(exe),
       'GIT_CONFIG_GLOBAL': str(scratch / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1',
       'GIT_TERMINAL_PROMPT': '0', 'TMPDIR': str(scratch), 'TEMP': str(scratch), 'TMP': str(scratch)}
wrapper = out / 'python-wrapper.py'
wrapper.write_text("import subprocess,runpy,sys\nfrom pathlib import Path\nsubprocess.CREATE_NO_WINDOW=0\nsys.argv=sys.argv[1:]\nsys.path.insert(0,str(Path(sys.argv[0]).resolve().parent))\nrunpy.run_path(sys.argv[0],run_name='__main__')\n")
steps = []
manager = None


def owned():
    found = []
    for proc in Path('/proc').iterdir():
        try:
            if proc.name.isdigit() and (proc / 'exe').resolve() == exe and (
                    'TREEGIT_REGRESSION_RUN=' + marker).encode() in (proc / 'environ').read_bytes().split(b'\0'):
                found.append(int(proc.name))
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return found


def close_owned():
    for pid in owned():
        os.kill(pid, signal.SIGTERM)
    deadline = time.monotonic() + 10
    while owned() and time.monotonic() < deadline:
        time.sleep(.1)
    assert not owned(), 'An owned test application did not exit'


def clean(path):
    if path.exists():
        assert path.is_relative_to(out) and path != out and not path.is_symlink()
        remove_fixture(path, path.parent, path.name)


def reserve():
    assert shutil.disk_usage(repo).free >= 10 * 2**30
    available = next(int(line.split()[1]) * 1024 for line in Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemAvailable:'))
    assert available >= 4 * 2**30
    size = sum(p.stat().st_size for p in scratch.rglob('*') if p.is_file() and not p.is_symlink())
    assert size < 2 * 2**30


def run(name, command):
    reserve()
    close_owned()
    profile = exe.parent / 'data'
    assert profile.parent == exe.parent and not profile.is_symlink()
    if profile.exists():
        shutil.rmtree(profile)
    profile.mkdir()
    (profile / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Blue',
        'GitInstallPath': shutil.which('git'), 'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False}))
    print('RUN ' + name, flush=True)
    start = time.monotonic()
    result = None
    try:
        with (out / (name + '.log')).open('w') as log:
            result = subprocess.run(command, cwd=repo, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=600)
        assert result.returncode == 0, name + ': ' + (out / (name + '.log')).read_text()[-2500:]
    finally:
        close_owned()
        clean(out / name / 'repository')
        for child in list(scratch.iterdir()):
            if child.is_dir():
                clean(child)
            elif child.is_file() and not child.is_symlink():
                child.unlink()
        steps.append({'name': name, 'exitCode': result.returncode if result else None,
                      'seconds': round(time.monotonic() - start, 2), 'ownedProcessesExited': not owned()})
        (out / 'summary.json').write_text(json.dumps({'source': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
            'steps': steps, 'ownedProcessesExited': not owned()}, indent=2))
    print('PASS ' + name, flush=True)


try:
    reserve()
    manager = subprocess.Popen(['openbox', '--sm-disable'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env)
    time.sleep(.5)
    for name, flag in [('upstream-models', '--integration-probe'), ('ui-models', '--integration-ui-consistency')]:
        fixture = scratch / name
        run(name, [str(exe), flag, str(fixture), str(out / (name + '.json'))])
    for name, script, options in [
        ('ui-consistency', 'ui_consistency_cloud.py', []),
        ('tracking-priority', 'branch_highlight.py', []),
        ('selection-and-conflict-recovery', 'interaction_safety.py', []),
        ('search-query-recovery', 'search_query_recovery.py', []),
        ('search-sha-recovery', 'search_query_recovery.py', ['--sha']),
        ('query-failure', 'query_failure.py', []),
        ('stashes', 'stashes_sidebar.py', []),
        ('sync-and-settings', 'sync_settings.py', []),
    ]:
        run(name, [sys.executable, str(wrapper), 'tests/' + script, str(exe), str(out / name), *options])
finally:
    close_owned()
    if manager:
        manager.terminate()
        manager.wait(timeout=10)
    clean(scratch)
    wrapper.unlink(missing_ok=True)
