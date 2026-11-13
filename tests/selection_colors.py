"""Check selection appearance, or capture public screenshots from a normal package.

Both modes use the exact owned Windows CI application and fictional repository.
The default regression requires integration diagnostics; --gallery captures the
normal portable program without enabling test interfaces in the release.
"""
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
import traceback

from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, out = (Path(a).resolve() for a in sys.argv[1:3])
gallery = '--gallery' in sys.argv
out.mkdir(parents=True, exist_ok=True)
profile = IsolatedProfile(exe, out)
parent = Path(tempfile.gettempdir()).resolve()
root = new_fixture(parent, 'TreeGit selection colors ')
repo = root / 'TreeGit Demo'
repo.mkdir()
env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
process = None
pid = None
checks = []
failure = None
messages = queue.Queue()
sequence = 0


def git(*args):
    r = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8', timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
    assert r.returncode == 0, r.stderr
    return r.stdout.strip()


def check(name, passed):
    assert passed, name
    checks.append(name)
    print('PASS ' + name, flush=True)


def tool(name, **arguments):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': 'tools/call', 'params': {'name': name, 'arguments': arguments}}) + '\n')
    process.stdin.flush()
    response = json.loads(messages.get(timeout=45))
    assert 'error' not in response, response
    r = response['result']
    assert not r.get('isError'), r
    return base64.b64decode(r['content'][0]['data']) if name == 'ui_screenshot' else json.loads(r['content'][0]['text'])


def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']) == repo)


def until(fn):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        value = fn()
        if value:
            return value
        time.sleep(.1)
    raise AssertionError('Selection appearance did not settle')


def select(sha):
    tool('ui_select_commit', repo=str(repo), revision=sha)
    until(lambda: state()['detailCommit'] == sha and not state()['detailBranchesLoading'])


def capture(name):
    time.sleep(.3)
    (out / (name + '.png')).write_bytes(tool('ui_screenshot'))


def text_rows(sha, selected):
    rows = [r for r in tool('ui_layout')['historySelectionText'] if r['sha'] == sha]
    return rows if len(rows) >= 4 and all(r['selected'] == selected and not r['merged'] for r in rows) else None


try:
    git('init', '-b', 'main')
    git('config', 'user.name', 'TreeGit Demo')
    git('config', 'user.email', 'demo@example.invalid')
    git('config', 'core.autocrlf', 'false')
    (repo / 'settings.json').write_text('{\n  "interval": 100,\n  "samples": 8\n}\n', encoding='utf-8')
    (repo / 'readme.txt').write_text('TreeGit interface demonstration\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', '初始化示例参数')
    git('branch', 'feature/side')
    (repo / 'settings.json').write_text('{\n  "interval": 120,\n  "samples": 16\n}\n', encoding='utf-8')
    (repo / 'readme.txt').write_text('TreeGit interface demonstration\nUpdated settings\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', '调整示例参数')
    target = git('rev-parse', 'HEAD')
    remote = git('commit-tree', git('rev-parse', 'HEAD^{tree}'), '-p', target, '-m', '远程待拉取的提交')
    git('update-ref', 'refs/remotes/origin/main', remote)
    git('remote', 'add', 'origin', str(repo))
    git('config', 'branch.main.remote', 'origin')
    git('config', 'branch.main.merge', 'refs/heads/main')
    (repo / 'readme.txt').write_text('TreeGit interface demonstration\nUpdated settings\nLocal next step\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', '本地待推送的提交')
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')]
    (profile.data / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light', 'Zoom': 1, 'EnableAutoFetch': False,
        'Check4UpdatesOnStartup': False, 'Workspaces': [{'Name': '示例', 'IsActive': True, 'RestoreOnStartup': True, 'Repositories': [repo.as_posix()]}]}, ensure_ascii=False), encoding='utf-8')
    process = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=(out / 'stderr.log').open('w'), text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': 0, 'method': 'initialize', 'params': {'protocolVersion': '2025-11-25'}}) + '\n')
    process.stdin.flush()
    assert 'result' in json.loads(messages.get(timeout=45))
    pid = tool('ui_state')['processId']
    tool('ui_open_repository', repo=str(repo))
    # Fit the hosted Windows desktop; oversized windows can be clipped by its
    # working area before the compositor finishes a resize.
    tool('ui_window', width=1040, height=736, state='normal', zoom=1)
    time.sleep(.6)
    tool('ui_graph', repo=str(repo), mode='all', showRemotes=True)
    until(lambda: not state()['graphLoading'] and state()['historyRows'])
    for theme, foreground in [('Light', '#ff123e34'), ('Dark', '#ffffffff'), ('Blue', '#ffffffff')]:
        tool('ui_window', theme=theme)
        select(target)
        scope = state()['commitBranchHighlights']
        levels = {r['reference']: r['level'] for r in scope['local'] + scope['remote']}
        check(theme + ' local tracking branch is primary and remote containing branch is secondary', levels == {'refs/heads/main': 2, 'refs/remotes/origin/main': 1})
        tool('ui_diff', repo=str(repo), action='select', source='history', file='settings.json')
        capture('theme-' + theme.lower())
        if theme == 'Light':
            capture('commit-branch-highlights')
        select(remote)
        if not gallery:
            rows = until(lambda: text_rows(remote, True))
            check(theme + ' selected unmerged text is fully opaque in every visible column', all(r['opacity'] == 1 for r in rows))
            check(theme + ' selected unmerged text has the correct theme foreground', all({'white': '#ffffffff'}.get(r['foreground'].lower(), r['foreground'].lower()) == foreground for r in rows))
        capture('remote-selected-' + theme.lower())
        select(target)
        if not gallery:
            rows = until(lambda: text_rows(remote, False))
            check(theme + ' deselected unmerged text restores its fade', all(r['opacity'] == .65 for r in rows))
    tool('ui_window', theme='Light')
    tool('ui_select_view', repo=str(repo), view='search')
    tool('ui_search', repo=str(repo), method='sha', query=target)
    until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
    tool('ui_search', repo=str(repo), select=target)
    until(lambda: state()['searchSelected'] == target and not state()['detailBranchesLoading'])
    tool('ui_diff', repo=str(repo), action='select', source='history', file='settings.json')
    capture('containing-branches')
    check('viewing selections and search preserves HEAD index and working files', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
except BaseException:
    failure = traceback.format_exc()
    if process and pid:
        try: capture('failure')
        except Exception: pass
    raise
finally:
    if process:
        process.stdin.close()
        process.wait(timeout=15)
    if pid:
        close_owned_gui(exe, pid)
    remove_fixture(root, parent, 'TreeGit selection colors ')
    profile.close()
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'failure': failure,
        'mode': 'normal-portable-gallery' if gallery else 'integration-selection-regression', 'fixtureRemoved': not root.exists(),
        'screenshots': sorted(p.name for p in out.glob('*.png')), 'fictionalDataOnly': True}, ensure_ascii=False, indent=2), encoding='utf-8')
