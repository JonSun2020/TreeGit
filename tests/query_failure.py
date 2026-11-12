"""Preserve a loaded repository when a real Git configuration read fails, in CI only."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui

assert os.environ.get('CI') == 'true', 'Run only against the owned CI application'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = new_fixture(out, 'TreeGit query failure audit ')
repo = root / 'repo'
repo.mkdir()
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root / 'global.gitconfig'), GIT_CONFIG_SYSTEM=str(root / 'system.gitconfig'))
(root / 'global.gitconfig').write_text('[user]\nname = Failure QA\nemail = qa@example.invalid\n', encoding='utf-8')
(root / 'system.gitconfig').write_text('', encoding='utf-8')


def git(*args):
    r = subprocess.run([shutil.which('git'), '-C', str(repo), *args], env=env, capture_output=True, timeout=15)
    assert r.returncode == 0, r.stderr.decode(errors='replace')
    return r.stdout.decode().strip()


git('init', '-b', 'main')
(repo / 'file.txt').write_text('base\n', encoding='utf-8')
git('add', '.')
git('commit', '-m', 'Initial')
git('branch', 'feature/retained')
(repo / 'file.txt').write_text('second\n', encoding='utf-8')
git('commit', '-am', 'Second')
p = subprocess.Popen([str(exe), '--mcp', '--read-only'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=(out / 'stderr.log').open('w'), text=True, encoding='utf-8', env=env,
                     creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
responses = queue.Queue()
threading.Thread(target=lambda: [responses.put(s) for s in p.stdout], daemon=True).start()
sequence = 0


def rpc(method, params):
    global sequence
    sequence += 1
    p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params}) + '\n')
    p.stdin.flush()
    result = json.loads(responses.get(timeout=30))
    assert 'error' not in result, result
    return result['result']


def tool(name, args=None):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    return json.loads(result['content'][0]['text'])


def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)


def until(predicate):
    end = time.monotonic() + 20
    while time.monotonic() < end:
        value = predicate()
        if value:
            return value
        time.sleep(.1)
    raise AssertionError('Repository did not finish the requested refresh')


report = {}
pid = None
config = repo / '.git/config'
original_config = config.read_bytes()
try:
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    desktop = tool('ui_state')
    pid = desktop['processId']
    if os.name == 'posix':
        assert Path(f'/proc/{pid}/exe').resolve() == exe, 'Unexpected GUI identity'
    tool('ui_open_repository', {'repo': str(repo)})
    tool('ui_graph', {'repo': str(repo), 'mode': 'all', 'showRemotes': True})
    head = git('rev-parse', 'HEAD')
    tool('ui_select_commit', {'repo': str(repo), 'revision': head})
    until(lambda: state()['workingCopyStatusKnown'] and not state()['graphLoading'] and len(state()['loadedBranches']) == 2)
    before = state()
    before_git = head, git('ls-files', '--stage'), git('status', '--porcelain=v1')
    if '--popup' in sys.argv[3:]:
        initial = git('rev-list', '--max-parents=0', 'HEAD')
        tool('ui_select_commit', {'repo': str(repo), 'revision': initial})
        until(lambda: not state()['detailBranchesLoading'] and state()['detailBranches'] and state()['detailBranches']['extraCount'] == 1)
        tool('ui_command', {'repo': str(repo), 'command': 'commit_branches'})
        until(lambda: state()['detailBranchPopup'])
        tool('ui_refresh', {'repo': str(repo)})
        until(lambda: not state()['graphLoading'] and not state()['detailBranchesLoading'])
        assert state()['detailBranchPopup'], 'Refreshing the same commit closed its containing-branches popup'
        report['popupSurvivedRefresh'] = True
        tool('ui_select_commit', {'repo': str(repo), 'revision': head})
        until(lambda: not state()['detailBranchesLoading'] and not state()['detailBranchPopup'])
        before = state()
    if '--search' in sys.argv[3:]:
        tool('ui_select_view', {'repo': str(repo), 'view': 'search'})
        tool('ui_search', {'repo': str(repo), 'method': 'message', 'query': 'Initial'})
        until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
        initial = state()['searchResults'][0]['sha']
        tool('ui_search', {'repo': str(repo), 'select': initial})
        tool('ui_search', {'repo': str(repo), 'method': 'message', 'query': '['})
        until(lambda: not state()['searchQuerying'])
        after_search = state()
        report['invalidSearch'] = after_search
        assert after_search['searchResultCount'] == 1 and after_search['searchResults'][0]['sha'] == initial, 'Failed search discarded the previous results'
        assert after_search['searchSelected'] == initial, 'Failed search discarded the selected result'
        assert after_search['searchFailed'], 'Failed search was reported as a valid empty result'
        report['failedSearchPreserved'] = True
        tool('ui_search', {'repo': str(repo), 'method': 'message', 'query': 'Second'})
        until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1 and state()['searchResults'][0]['sha'] == head)
        assert not state()['searchFailed'], 'A successful retry did not clear the search failure'
        for turn in range(20):
            tool('ui_search', {'repo': str(repo), 'method': 'message', 'query': 'Initial' if turn % 2 else 'Second'})
        tool('ui_search', {'repo': str(repo), 'method': 'sha', 'query': head})
        until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1 and state()['searchResults'][0]['sha'] == head)
        time.sleep(.3)
        assert state()['searchSelected'] == head and not state()['searchFailed'], 'An outdated search replaced the latest request'
        tool('ui_select_view', {'repo': str(repo), 'view': 'history'})
        tool('ui_select_commit', {'repo': str(repo), 'revision': head})
        before = state()
    config.write_text('[broken\n', encoding='utf-8')
    tool('ui_refresh', {'repo': str(repo)})
    until(lambda: not state()['workingCopyStatusKnown'])
    time.sleep(.5)
    after = state()
    report['before'] = before
    report['failedRefresh'] = after
    assert after['loadedBranches'] == before['loadedBranches'], 'Failed refresh discarded loaded branch references'
    assert [c['sha'] for c in after['graphCommits']] == [c['sha'] for c in before['graphCommits']], 'Failed refresh discarded history'
    assert after['historySelectedCommits'] == before['historySelectedCommits'], 'Failed refresh discarded the selected commit'
    assert after['repositoryRefreshFailed'], 'Stale repository data was reported as current'
    report['preserved'] = True
except BaseException as error:
    report['failure'] = repr(error)
    raise
finally:
    config.write_bytes(original_config)
    try:
        tool('ui_refresh', {'repo': str(repo)})
        until(lambda: state()['workingCopyStatusKnown'] and not state()['graphLoading'])
        until(lambda: not state()['repositoryRefreshFailed'])
        report['restoredGitUnchanged'] = before_git == (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain=v1')) if 'before_git' in locals() else None
        for tab in tool('ui_tabs', {'action': 'list'})['tabs']:
            if tab.get('repo') and Path(tab['repo']).resolve() == repo:
                tool('ui_tabs', {'action': 'close', 'index': tab['index']})
                break
    finally:
        p.stdin.close()
        try:
            p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            p.kill()
            p.wait()
        if pid:
            close_owned_gui(exe, pid)
        remove_fixture(root, out, 'TreeGit query failure audit ')
        report['fixtureRemoved'] = not root.exists()
        report['ownedApplicationExited'] = True
        (out / 'results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
