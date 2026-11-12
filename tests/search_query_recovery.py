"""An auxiliary ancestry-query failure must not publish misleading search rows."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui

assert os.environ.get('CI') == 'true' and os.name == 'posix'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
parent = Path(tempfile.gettempdir()).resolve()
root = new_fixture(parent, 'TreeGit search query ')
repo = root / 'repo'
repo.mkdir()
env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
real_git = shutil.which('git')
marker = root / 'fail-ancestry'
observed = root / 'failure-observed'
wrapper = root / 'git'
preferences = exe.parent / 'data/preference.json'
assert preferences.is_file() and not preferences.is_symlink()
original_preferences = preferences.read_bytes()
process = None
pid = None
sequence = 0
replies = queue.Queue()
checks = []
failure = None
evidence = {}
method = 'sha' if '--sha' in sys.argv[3:] else 'message'


def git(*args):
    result = subprocess.run([real_git, '-C', str(repo), *args], env=env, capture_output=True, timeout=20)
    assert result.returncode == 0, result.stderr
    return result.stdout.decode('utf-8').strip()


def until(predicate):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.05)
    raise AssertionError('Search did not settle')


def rpc(method, args):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence,
                                    'method': method, 'params': args}) + '\n')
    process.stdin.flush()
    reply = json.loads(replies.get(timeout=45))
    assert reply.get('id') == sequence and 'error' not in reply, reply
    return reply['result']


def tool(name, args=None):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    return json.loads(result['content'][0]['text'])


def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']) == repo)


def check(label, condition):
    assert condition, label
    checks.append(label)
    print('PASS', label, flush=True)


try:
    git('init', '-b', 'main')
    git('config', 'user.name', 'Search failure QA')
    git('config', 'user.email', 'search-failure@example.invalid')
    (repo / 'file.txt').write_text('base\n', encoding='utf-8')
    git('add', '.')
    git('commit', '-m', 'Initial')
    initial = git('rev-parse', 'HEAD')
    (repo / 'file.txt').write_text('second\n', encoding='utf-8')
    git('commit', '-am', 'Second')
    head = git('rev-parse', 'HEAD')
    before_git = head, git('ls-files', '--stage'), git('status', '--porcelain')
    wrapper.write_text('#!' + sys.executable + '\n' +
        'import os,sys\nfrom pathlib import Path\n' +
        'args=sys.argv[1:]\n' +
        'target = ("merge-base" in args and "--is-ancestor" in args) if ' + repr(method == 'sha') + ' else ("--format=%H" in args and any(a.startswith("--since=@") for a in args))\n' +
        'if Path(' + repr(str(marker)) + ').exists() and target:\n' +
        ' Path(' + repr(str(observed)) + ').write_text("ancestry query failed")\n' +
        ' print("fatal: owned ancestry query failure",file=sys.stderr)\n sys.exit(9)\n' +
        'os.execv(' + repr(real_git) + ',[' + repr(real_git) + ',*args])\n', encoding='utf-8')
    wrapper.chmod(0o755)
    prefs = json.loads(original_preferences.decode('utf-8-sig'))
    prefs.update(GitInstallPath=str(wrapper), EnableAutoFetch=False, Check4UpdatesOnStartup=False)
    preferences.write_text(json.dumps(prefs), encoding='utf-8')
    process = subprocess.Popen([str(exe), '--mcp', '--read-only'], env=env, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=(out / 'stderr.log').open('w'),
                               text=True, encoding='utf-8')
    threading.Thread(target=lambda: [replies.put(line) for line in process.stdout], daemon=True).start()
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    pid = tool('ui_state')['processId']
    assert Path('/proc/' + str(pid) + '/exe').resolve() == exe
    tool('ui_open_repository', {'repo': str(repo)})
    tool('ui_select_view', {'repo': str(repo), 'view': 'search'})
    tool('ui_search', {'repo': str(repo), 'method': method, 'query': initial if method == 'sha' else 'Initial'})
    until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
    tool('ui_search', {'repo': str(repo), 'select': initial})
    marker.write_text('owned fault')
    tool('ui_search', {'repo': str(repo), 'method': method, 'query': head if method == 'sha' else 'Second'})
    until(lambda: not state()['searchQuerying'])
    failed = state()
    evidence.update(injectedQueryObserved=observed.exists(), failedSearch=failed)
    check('the failure targets the auxiliary ancestry query', observed.exists())
    check('an ancestry failure preserves the previous results and selected commit',
          failed['searchFailed'] and failed['searchResultCount'] == 1 and
          failed['searchResults'][0]['sha'] == initial and failed['searchSelected'] == initial)
    marker.unlink()
    tool('ui_search', {'repo': str(repo), 'method': method, 'query': head if method == 'sha' else 'Second'})
    until(lambda: not state()['searchQuerying'] and not state()['searchFailed'] and
          state()['searchResultCount'] == 1 and state()['searchResults'][0]['sha'] == head)
    check('explicit retry clears the failure and publishes the correct search results', not state()['searchFailed'])
    check('the failed read and retry never change Git state',
          before_git == (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')))
except BaseException:
    failure = traceback.format_exc()
    raise
finally:
    if process:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
    if pid:
        close_owned_gui(exe, pid)
    preferences.write_bytes(original_preferences)
    remove_fixture(root, parent, 'TreeGit search query ')
    (out / 'results.json').write_text(json.dumps({'method': method, 'passed': len(checks), 'checks': checks,
        'failure': failure, 'evidence': evidence, 'fixtureRemoved': not root.exists(),
        'preferencesRestored': preferences.read_bytes() == original_preferences},
        ensure_ascii=False, indent=2), encoding='utf-8')
