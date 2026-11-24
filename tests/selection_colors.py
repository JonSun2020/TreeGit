"""Check selection appearance, or capture public screenshots from a normal package.

Both modes use the exact owned Windows CI application and fictional repository.
The default regression requires integration diagnostics; --gallery captures the
normal portable program without enabling test interfaces in the release.
"""
import base64
import ctypes
from ctypes import wintypes
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
repo = root / 'Demo'
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


def capture(name, active=False):
    # Park the CI pointer on the owned caption so tooltips cannot cover code.
    if gallery and pid:
        api = ctypes.WinDLL('user32', use_last_error=True)
        api.GetForegroundWindow.restype = wintypes.HWND
        api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        api.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        hwnd, owner, rect = api.GetForegroundWindow(), wintypes.DWORD(), wintypes.RECT()
        api.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and api.GetWindowRect(hwnd, ctypes.byref(rect)):
            api.SetCursorPos((rect.left + rect.right) // 2, rect.top + 8)
    time.sleep(.3)
    (out / (name + '.png')).write_bytes(tool('ui_screenshot', **({'window': 'active'} if active else {})))


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
    if gallery:
        (repo / 'messages.cfg').write_bytes('status = 正在读取\n'.encode()+'mode = 手动\n'.encode('gbk'))
    git('add', '.'); git('commit', '-m', '初始化示例参数')
    git('branch', 'feature/side')
    (repo / 'settings.json').write_text('{\n  "interval": 120,\n  "samples": 16\n}\n', encoding='utf-8')
    (repo / 'readme.txt').write_text('TreeGit interface demonstration\nUpdated settings\n', encoding='utf-8')
    if gallery:
        (repo / 'messages.cfg').write_bytes('status = 读取完成\n'.encode()+'mode = 自动\n'.encode('gbk'))
    git('add', '.'); git('commit', '-m', '调整示例参数')
    target = git('rev-parse', 'HEAD')
    git('branch', 'release/demo', target)
    remote = git('commit-tree', git('rev-parse', 'HEAD^{tree}'), '-p', target, '-m', '远程待拉取的提交')
    git('update-ref', 'refs/remotes/origin/main', remote)
    git('remote', 'add', 'origin', 'https://example.invalid/demo.git' if gallery else str(repo))
    git('config', 'branch.main.remote', 'origin')
    git('config', 'branch.main.merge', 'refs/heads/main')
    (repo / 'readme.txt').write_text('TreeGit interface demonstration\nUpdated settings\nLocal next step\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', '本地待推送的提交')
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')]
    (repo / '.git/sourcegit.uistates').write_text(json.dumps({
        'IsLocalBranchesExpandedInSideBar': True, 'IsRemotesExpandedInSideBar': True,
        'ExpandedBranchNodesInSideBar': ['refs/remotes/origin']
    }), encoding='utf-8')
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
    # The narrow desktop has a horizontal viewport. Fit the four text columns
    # so this check still tests the foreground and opacity of every column.
    if not gallery:
        tool('ui_history_columns', repo=str(repo), graph=60, description=240, date=160, author=100, commit=100)
    for theme, foreground in [('Light', '#ff123e34'), ('Dark', '#ffffffff'), ('Blue', '#ffffffff')]:
        tool('ui_window', theme=theme)
        select(target)
        scope = state()['commitBranchHighlights']
        levels = {r['reference']: r['level'] for r in scope['local'] + scope['remote']}
        check(theme + ' local tracking branch and its containing remote share the primary background', levels == {'refs/heads/main': 2, 'refs/remotes/origin/main': 2})
        until(lambda: 'settings.json' in (state()['detailChanges'] or []))
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
    until(lambda: 'settings.json' in (state()['detailChanges'] or []))
    tool('ui_diff', repo=str(repo), action='select', source='history', file='settings.json')
    capture('containing-branches')
    if gallery:
        tool('ui_search', repo=str(repo), showBranches=True)
        capture('search-branches-popup')
    check('viewing selections and search preserves HEAD index and working files', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
    if gallery:
        # Set up each public example explicitly inside this disposable repository.
        tool('ui_search', repo=str(repo), select=target, open=True)
        until(lambda: 'messages.cfg' in (state()['detailChanges'] or []))
        tool('ui_diff', repo=str(repo), action='select', source='history', file='messages.cfg')
        tool('ui_command', command='external_diff')
        until(lambda: any('messages.cfg' in d['title'] for d in tool('ui_state')['dialogs']))
        capture('text-comparison', active=True)
        tool('ui_command', command='close_dialog')
        (repo / 'firmware.bin').write_bytes(bytes(range(256)) * 8)
        git('add', '.'); git('commit', '-m', '添加示例固件')
        image = bytearray((repo / 'firmware.bin').read_bytes())
        image[32:48] = bytes(range(240, 256)); image[96:104] = b'TreeGit!'
        (repo / 'firmware.bin').write_bytes(image)
        git('commit', '-am', '更新示例固件字节')
        binary_head = git('rev-parse', 'HEAD')
        tool('ui_refresh', repo=str(repo))
        until(lambda: not state()['graphLoading'] and any(b['head'] == binary_head for b in state()['loadedBranches']))
        select(binary_head)
        until(lambda: 'firmware.bin' in (state()['detailChanges'] or []))
        tool('ui_diff', repo=str(repo), action='select', source='history', file='firmware.bin')
        until(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
        tool('ui_diff', repo=str(repo), action='window')
        until(lambda: any(d['title'] == 'TreeGit · 二进制对比' for d in tool('ui_state')['dialogs']))
        until(lambda: (d := tool('ui_diff', repo=str(repo), action='state')).get('status') == 'ready'
              and d.get('oldSize') == 2048 and d.get('newSize') == 2048 and d.get('differentBytes') == 24)
        capture('binary-diff', active=True)
        tool('ui_command', command='close_dialog')
        check('public binary example renders the built-in comparison', True)

        # Show the complete commit page with fictional identity and draft only.
        (repo / 'readme.txt').write_text('TreeGit interface demonstration\nReady for the next change\n', encoding='utf-8')
        git('add', 'readme.txt')
        tool('ui_refresh', repo=str(repo))
        tool('ui_working_copy', repo=str(repo))
        until(lambda: 'readme.txt' in (state().get('workingCopyState') or {}).get('staged', []))
        tool('ui_working_copy', repo=str(repo), message='完善示例参数\n\n说明修改目的，检查暂存内容后提交。')
        tool('ui_diff', repo=str(repo), action='select', source='staged', file='readme.txt')
        tool('ui_select_view', repo=str(repo), view='commit')
        until(lambda: (state().get('workingCopyState') or {}).get('composition', {}).get('identity') == 'TreeGit Demo <demo@example.invalid>')
        until(lambda: state()['workingCopyState']['composition']['visible'] and state()['workingCopyState']['composition']['commitEnabled'])
        capture('commit-page')
        check('public commit page shows the identity and enabled staged draft without committing', git('rev-parse', 'HEAD') == binary_head)
        tool('ui_window', theme='Blue')
        tool('ui_command', repo=str(repo), command='pull_dialog')
        until(lambda: tool('ui_state')['activePage']['pendingDialog']=='Pull')
        capture('pull-options')
        tool('ui_command', repo=str(repo), command='cancel_popup')
        tool('ui_window', theme='Light')

        repo = root / 'Merge Demo'
        repo.mkdir()
        git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit Demo'); git('config', 'user.email', 'demo@example.invalid')
        git('config', 'core.autocrlf', 'false'); git('config', 'merge.conflictStyle', 'diff3')
        lines = ['sample_rate = 100;\n'] + ['context_' + str(i) + ' = 0;\n' for i in range(8)] + ['report_interval = 20;\n']
        (repo / 'settings.cfg').write_text(''.join(lines), encoding='utf-8')
        git('add', '.'); git('commit', '-m', '初始采样配置')
        git('checkout', '-b', 'feature/demo')
        (repo / 'settings.cfg').write_text(''.join(lines).replace('sample_rate = 100', 'sample_rate = 200').replace('report_interval = 20', 'report_interval = 40'), encoding='utf-8')
        git('commit', '-am', '远端示例配置')
        git('checkout', 'main')
        (repo / 'settings.cfg').write_text(''.join(lines).replace('sample_rate = 100', 'sample_rate = 160').replace('report_interval = 20', 'report_interval = 30'), encoding='utf-8')
        git('commit', '-am', '本地示例配置')
        merge = subprocess.run(['git', '-C', str(repo), 'merge', '--no-edit', 'feature/demo'], env=env, capture_output=True)
        check('public merge example has actual independent conflicts', merge.returncode == 1 and bool(git('ls-files', '--unmerged')))
        tool('ui_open_repository', repo=str(repo)); until(lambda: state()['changes'] == 1)
        tool('ui_command', repo=str(repo), command='merge_conflicts', file='settings.cfg')
        tool('ui_merge_result', repo=str(repo), file='settings.cfg', action='resolve', region=0, choice='left')
        current = tool('ui_merge_result', repo=str(repo), file='settings.cfg', action='read')
        check('public merge example shows resolved and unresolved blocks', current['unsolved'] == 1)
        capture('merge-editor', active=True)
        tool('ui_merge_result', repo=str(repo), file='settings.cfg', action='resolve', region=1, choice='right')
        saved = tool('ui_merge_result', repo=str(repo), file='settings.cfg', action='save')['saved']
        if not saved:
            raise AssertionError('Public merge save failed: ' + json.dumps(tool('ui_merge_result', repo=str(repo), file='settings.cfg', action='read'), ensure_ascii=False))
        check('public merge example saves and stages through the real editor', saved and not git('ls-files', '--unmerged'))
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
