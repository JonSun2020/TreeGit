"""Exercise the real splitter drag handlers and operation/view transitions on a disposable repo.

Runs on a CI desktop, never attaches to a user's installed TreeGit or repositories.
Stores compact geometry and a few representative/failing renders, not duplicate app copies.
"""
import base64
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import time
import zlib

sys.stdout.reconfigure(encoding='utf-8')

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit resize audit ')).resolve()
repo = root / 'long repository title for resize checks'
repo.mkdir()
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root / 'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1')

# Status/diff polling must never write Git's index while a GUI operation owns it.
def git(*args):
    result = subprocess.run(['git', '--no-optional-locks', '-C', str(repo), *args], capture_output=True, text=True, encoding='utf-8', env=env)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()

git('init', '-b', 'main')
git('config', 'user.name', 'TreeGit Resize QA')
git('config', 'user.email', 'resize@example.invalid')
component_source = root / 'component-source'
component_source.mkdir()
def component_git(directory, *args):
    result = subprocess.run(['git', '-C', str(directory), *args], capture_output=True, text=True, encoding='utf-8', env=env)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()
component_git(component_source, 'init', '-b', 'main')
component_git(component_source, 'config', 'user.name', 'Component QA')
component_git(component_source, 'config', 'user.email', 'component@example.invalid')
(component_source / 'sensor.txt').write_text('original\n', encoding='utf-8')
component_git(component_source, 'add', '.')
component_git(component_source, 'commit', '-m', 'Component baseline')
component_before = component_git(component_source, 'rev-parse', 'HEAD')
(component_source / 'sensor.txt').write_text('updated component\n', encoding='utf-8')
component_git(component_source, 'commit', '-am', 'Update component')
component_after = component_git(component_source, 'rev-parse', 'HEAD')
git('-c', 'protocol.file.allow=always', 'submodule', 'add', str(component_source), 'component')
component_git(repo / 'component', 'checkout', component_before)
(repo / 'text.txt').write_text('one\ntwo\nthree\n', encoding='utf-8')
(repo / 'firmware.bin').write_bytes(bytes(range(256)) * 64)
def make_png(color):
    def chunk(name, data):
        return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2, 2, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress((b'\0' + bytes(color) * 2) * 2)) + chunk(b'IEND', b'')
png = make_png((32, 128, 192))
(repo / 'image.png').write_bytes(png)
git('add', '.')
git('commit', '-m', 'Initial baseline')
(repo / 'text.txt').write_text('one\nmodified\nthree\nfour\n', encoding='utf-8')
(repo / 'firmware.bin').write_bytes(bytes(range(255, -1, -1)) * 64)
component_git(repo / 'component', 'checkout', component_after)
git('add', '.')
git('commit', '-m', 'A long subject to verify file and commit details remain readable when the panels become narrow')
head = git('rev-parse', 'HEAD')
initial = git('rev-parse', 'HEAD~1')
git('tag', 'v1.0-with-a-long-label-for-layout-review')
git('branch', 'feature/long-component-name/resize-review', 'HEAD~1')
git('init', '--bare', str(root / 'origin.git'))
git('remote', 'add', 'origin', str(root / 'origin.git'))
git('push', '-u', 'origin', 'main')
(repo / 'text.txt').write_text('stashed\n', encoding='utf-8')
git('stash', 'push', '-m', 'Long stash title with text and binary changes for resizable detail panels')
(repo / 'text.txt').write_text('staged change\n', encoding='utf-8')
git('add', 'text.txt')
staged_blob = git('rev-parse', ':text.txt')
(repo / 'text.txt').write_text('staged change\nunstaged change\n', encoding='utf-8')
(repo / 'firmware.bin').write_bytes(bytes(range(256)) * 64)
(repo / 'image.png').write_bytes(make_png((192, 128, 32)))
folder = repo / 'a long folder name' / 'another nested folder for overflow checks'
folder.mkdir(parents=True)
(folder / 'a very long untracked filename with spaces and unicode 中文.txt').write_text('untracked\n', encoding='utf-8')

data = exe.parent / 'data'
data.mkdir(exist_ok=True)
preferences = {'Locale': 'zh_CN', 'Theme': 'Blue', 'EnableAutoFetch': False, 'Check4UpdatesOnStartup': False}
(data / 'preference.json').write_text(json.dumps(preferences), encoding='utf-8')
errors = []

def start_process():
    process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
    inbox = queue.Queue()
    threading.Thread(target=lambda: [inbox.put(line) for line in process.stdout], daemon=True).start()
    threading.Thread(target=lambda: [errors.append(line[-2000:]) for line in process.stderr], daemon=True).start()
    return process, inbox

p, messages = start_process()
seq = 0
pid = None
checks = []
records = []
pointer_records = []

def stop_session():
    p.stdin.close()
    try: p.wait(timeout=10)
    except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=5)
    if pid:
        code = "$p=Get-Process -Id " + str(pid) + " -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '" + str(exe).replace("'", "''") + "'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell', '-NoProfile', '-Command', code], capture_output=True, check=True)

def rpc(method, params=None):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': seq, 'method': method, 'params': params or {}}) + '\n')
    p.stdin.flush()
    response = json.loads(messages.get(timeout=60))
    assert response.get('id') == seq and 'error' not in response, response
    return response['result']

def tool(name, args=None):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    if name == 'ui_screenshot':
        return base64.b64decode(result['content'][0]['data'])
    return json.loads(result['content'][0]['text'])

def repo_tool(name, **args):
    return tool(name, {'repo': str(repo), **args})

def wait_for(fn, timeout=20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = fn()
        if result:
            return result
        time.sleep(.1)
    raise AssertionError('Timed out waiting for view state')

def state():
    return next(r for r in tool('ui_state')['repositories'] if r['active'])

def check(label, ok, detail=None):
    checks.append({'name': label, 'ok': bool(ok), 'detail': detail})
    print(('PASS ' if ok else 'FAIL ') + label, flush=True)

def click_ready(name, **args):
    wait_for(lambda: any(b['name'] == name and b['enabled'] for b in tool('ui_layout')['buttons']))
    tool('ui_layout', {'action': 'test_click', 'button': name, **args})

def native_drag(window, point, dx=0, dy=0):
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Windows':
        pointer_records.append({'name': 'physical-pointer-unavailable', 'reason': 'Restricted to an isolated GitHub Windows desktop'})
        return False
    api = ctypes.WinDLL('user32', use_last_error=True)
    api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    api.GetForegroundWindow.restype = wintypes.HWND
    api.SetForegroundWindow.argtypes = [wintypes.HWND]
    api.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
    api.mouse_event.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
    handle = window['nativeHandle']
    owner_pid = wintypes.DWORD()
    api.GetWindowThreadProcessId(handle, ctypes.byref(owner_pid))
    assert owner_pid.value == pid, 'Pointer test refused a window from another process'
    api.SetForegroundWindow(handle)
    time.sleep(.15)
    if api.GetForegroundWindow() != handle:
        pointer_records.append({'name': 'physical-pointer-unavailable', 'reason': 'CI desktop cannot activate its own test window'})
        return False
    x, y = point['x'], point['y']
    assert api.SetCursorPos(x, y)
    time.sleep(.08)
    api.mouse_event(2, 0, 0, 0, 0)
    try:
        for step in range(1, 6):
            assert api.SetCursorPos(round(x + dx * step / 5), round(y + dy * step / 5))
            time.sleep(.06)
    finally:
        api.mouse_event(4, 0, 0, 0, 0)
    time.sleep(.2)
    pointer_records.append({'name': 'physical-pointer-drag', 'owner': owner_pid.value, 'x': x, 'y': y, 'dx': dx, 'dy': dy})
    return True

def screenshot(label, active=False):
    (out / (label + '.png')).write_bytes(tool('ui_screenshot', {'window': 'active' if active else 'main'}))

def inspect(label, selected=None, capture=False, active=False):
    time.sleep(.15)
    geometry = tool('ui_layout', {'window': 'active' if active else 'main'})
    status = tool('ui_state')
    records.append({'name': label, 'layout': geometry, 'popups': status.get('layoutProbe', {}).get('popups'), 'dialogs': status['dialogs']})
    w = geometry['window']
    tabs = geometry['bottomTabs']
    if tabs and not active:
        check(label + ': equal tab sizes', len(tabs) == 3 and all(abs(t['bounds']['localWidth'] - 104) < .5 and abs(t['bounds']['localHeight'] - 26) < .5 for t in tabs))
        check(label + ': bottom tabs stay inside viewport', all(t['bounds']['x'] >= 0 and t['bounds']['y'] >= 0 and t['bounds']['x'] + t['bounds']['width'] <= w['width'] + 1 and t['bounds']['y'] + t['bounds']['height'] <= w['height'] + 1 for t in tabs))
        if selected is not None:
            check(label + ': one correct active tab', [t['text'] for t in tabs if t['active']] == [selected])
        for tab in tabs:
            b = tab['bounds']
            for content in tab['content']:
                dx = abs(content['x'] + content['width'] / 2 - b['x'] - b['width'] / 2)
                dy = abs(content['y'] + content['height'] / 2 - b['y'] - b['height'] / 2)
                check(label + ': centered ' + tab['text'], dx < 1.1 and dy < 1.1, {'dx': dx, 'dy': dy})
    probe = status.get('layoutProbe', {})
    check(label + ': staging actions remain in their panels', not probe.get('stageButtonsOutsidePanel'), probe.get('stageButtonsOutsidePanel'))
    if active and status['dialogs']:
        d = status['dialogs'][-1]
        a = d['workArea']
        check(label + ': dialog fits screen', d['x'] >= a['x'] - 1 and d['y'] >= a['y'] - 1 and d['x'] + d['width'] * d['scaling'] <= a['x'] + a['width'] + 1 and d['y'] + d['height'] * d['scaling'] <= a['y'] + a['height'] + 1, d)
    if capture:
        screenshot(label, active)
    return geometry

def sweep(label, selected=None, captures=False, active=False):
    scope = {'window': 'active' if active else 'main'}
    original = inspect(label, selected, capture=captures, active=active)
    visible = [s for s in original['splitters'] if s['visible'] and s['enabled']]
    check(label + ': has draggable panels', bool(visible))
    for s in visible:
        direction = 'x' if s['bounds']['width'] < s['bounds']['height'] else 'y'
        changed = False
        for sign in [-1, 1]:
            tool('ui_layout', {**scope, 'action': 'restore', 'splitter': s['index'], 'columns': s['columns'], 'rows': s['rows']})
            time.sleep(.08)
            tool('ui_layout', {**scope, 'action': 'drag', 'splitter': s['index'], 'dx': sign * 5000 if direction == 'x' else 0, 'dy': sign * 5000 if direction == 'y' else 0})
            case = f"{label}-split{s['index']}-{sign}"
            g = inspect(case, selected, capture=captures, active=active)
            moved = next(x for x in g['splitters'] if x['index'] == s['index'])
            lengths, mins = (moved['actualColumns'], moved['minColumns']) if direction == 'x' else (moved['actualRows'], moved['minRows'])
            old_lengths = s['actualColumns'] if direction == 'x' else s['actualRows']
            changed |= any(abs(a - b) > .1 for a, b in zip(lengths, old_lengths))
            check(case + ': respects pane minimums', all(a >= m - 1 for a, m in zip(lengths, mins)), {'actual': lengths, 'minimums': mins})
        position = s['column'] if direction == 'x' else s['row']
        lengths = s['actualColumns'] if direction == 'x' else s['actualRows']
        mins = s['minColumns'] if direction == 'x' else s['minRows']
        slack = sum(max(0, lengths[i] - mins[i]) for i in [position - 1, position + 1] if 0 <= i < len(lengths))
        check(label + ': splitter ' + str(s['index']) + ' moves when space permits', changed or slack < 1.1, {'slack': slack})
        tool('ui_layout', {**scope, 'action': 'restore', 'splitter': s['index'], 'columns': s['columns'], 'rows': s['rows']})
        time.sleep(.08)
    inspect(label + '-restored', selected, active=active)

def audit_active_sizes(label):
    dialog = tool('ui_state')['dialogs'][-1]
    area = dialog['workArea']
    maximum = (int(area['width'] / dialog['scaling'] - 32), int(area['height'] / dialog['scaling'] - 64))
    for zoom in [1, 2, 2.5]:
        tool('ui_window', {'zoom': zoom})
        for width, height in [(800, 520), maximum]:
            tool('ui_layout', {'action': 'resize', 'window': 'active', 'width': width, 'height': height})
            case = f'{label}-window-{width}-zoom{zoom}'
            g = inspect(case, capture=True, active=True)
            w = g['window']
            fixed = [b for b in g['buttons'] if not b['inScrollArea']]
            # A scaled one-pixel border can round just outside the client rect.
            tolerance = 1.5
            check(case + ': fixed actions are visible', all(b['bounds']['x'] >= -tolerance and b['bounds']['y'] >= -tolerance and b['bounds']['x'] + b['bounds']['width'] <= w['width'] + tolerance and b['bounds']['y'] + b['bounds']['height'] <= w['height'] + tolerance for b in fixed))
            def overlaps(a, b):
                a, b = a['bounds'], b['bounds']
                return min(a['x'] + a['width'], b['x'] + b['width']) - max(a['x'], b['x']) > 1 and min(a['y'] + a['height'], b['y'] + b['height']) - max(a['y'], b['y']) > 1
            check(case + ': fixed actions do not overlap', not any(overlaps(a, b) for i, a in enumerate(fixed) for b in fixed[i+1:]))
            for scroll in g['scrolls']:
                if not scroll['visible'] or not (scroll['name'] or '').endswith('Viewport'): continue
                reached = None
                for _ in range(20):
                    tool('ui_layout', {'action': 'scroll', 'window': 'active', 'scroller': scroll['index'], 'dx': 5000, 'dy': 5000})
                    time.sleep(.1)
                    reached = next(s for s in tool('ui_layout', {'window': 'active'})['scrolls'] if s['index'] == scroll['index'])
                    if reached['offset']['x'] + reached['viewport']['width'] >= reached['extent']['width'] - 1 and reached['offset']['y'] + reached['viewport']['height'] >= reached['extent']['height'] - 1: break
                check(case + ': whole content is reachable', reached['offset']['x'] + reached['viewport']['width'] >= reached['extent']['width'] - 1 and reached['offset']['y'] + reached['viewport']['height'] >= reached['extent']['height'] - 1, reached)
                tool('ui_layout', {'action': 'scroll', 'window': 'active', 'scroller': scroll['index'], 'dx': 0, 'dy': 0})
    tool('ui_window', {'zoom': 1})

try:
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    repo_tool('ui_open_repository')
    pid = tool('ui_state')['processId']
    wait_for(lambda: state()['graphCommits'])
    tool('ui_window', {'state': 'maximized', 'theme': 'Blue', 'zoom': 1})
    repo_tool('ui_select_commit', revision=head)
    wait_for(lambda: state()['detailCommit'] == head)
    initial_layout = tool('ui_layout')
    sidebar = next(s for s in initial_layout['splitters'] if s['owner'] == 'Repository' and s['visible'])
    if native_drag(initial_layout['window'], sidebar['screen'], dx=50):
        moved = next(s for s in tool('ui_layout')['splitters'] if s['index'] == sidebar['index'])
        check('real mouse drag resizes the sidebar', abs(moved['actualColumns'][0] - sidebar['actualColumns'][0]) > 10)
        tool('ui_layout', {'action': 'restore', 'splitter': sidebar['index'], 'columns': sidebar['columns'], 'rows': sidebar['rows']})
    initial_layout = tool('ui_layout')
    graph_header = next(h for h in initial_layout['columnHeaders'] if h['text'] == '图谱')
    if native_drag(initial_layout['window'], graph_header['screen'], dx=40):
        resized_columns = repo_tool('ui_history_columns')
        check('real mouse column drag is saved through the normal header handler', resized_columns['graph'] > graph_header['bounds']['localWidth'] + 10, resized_columns)
        repo_tool('ui_history_columns', reset=True)

    repo_tool('ui_diff', action='select', source='history', file='text.txt')
    tool('ui_layout', {'twoColumnDiff': True})
    sweep('history-text', '日志 / 历史', True)
    tool('ui_layout', {'twoColumnHistory': True})
    sweep('history-horizontal', '日志 / 历史', True)
    tool('ui_layout', {'twoColumnHistory': False})
    repo_tool('ui_command', command='commit_files')
    sweep('revision-files', '日志 / 历史', True)
    repo_tool('ui_command', command='commit_changes')
    repo_tool('ui_command', command='compare_commits', base=initial, to=head)
    wait_for(lambda: state()['detailView'] == 'RevisionCompare')
    time.sleep(.3)
    repo_tool('ui_diff', action='select', source='history', file='text.txt')
    sweep('compare-commits', '日志 / 历史', True)
    repo_tool('ui_select_commit', revision=head)
    wait_for(lambda: state()['detailView'] == 'CommitDetail')
    wait_for(lambda: 'component' in (state().get('detailChanges') or []))
    repo_tool('ui_diff', action='select', source='history', file='component')
    wait_for(lambda: any(d['type'] == 'SubmoduleDiff' for d in tool('ui_layout')['diffs']))
    repo_tool('ui_command', command='submodule_compare')
    wait_for(lambda: tool('ui_state')['dialogs'])
    time.sleep(.3)
    sweep('submodule-compare', captures=True, active=True)
    audit_active_sizes('submodule-compare')
    tool('ui_command', {'command': 'close_dialog'})
    repo_tool('ui_command', command='interactive_rebase', base=initial)
    wait_for(lambda: tool('ui_state')['dialogs'])
    time.sleep(.4)
    sweep('interactive-rebase', captures=True, active=True)
    audit_active_sizes('interactive-rebase')
    tool('ui_command', {'command': 'close_dialog'})
    for command, args in [('file_history', {'file': 'text.txt'}), ('directory_history', {'file': '.'}),
                          ('compare_branches', {'base': 'feature/long-component-name/resize-review', 'to': 'main'}), ('git_logs', {})]:
        repo_tool('ui_command', command=command, **args)
        wait_for(lambda: tool('ui_state')['dialogs'])
        time.sleep(.5)
        sweep(command, captures=True, active=True)
        audit_active_sizes(command)
        tool('ui_command', {'command': 'close_dialog'})
    repo_tool('ui_search', method='message', query='long subject')
    wait_for(lambda: not state()['searchQuerying'] and state()['searchResults'])
    repo_tool('ui_search', select=head)
    sweep('search', '搜索', True)
    repo_tool('ui_working_copy')
    repo_tool('ui_diff', action='select', source='unstaged', file='text.txt')
    sweep('working-copy-overview', '文件状态', True)
    # Full commit editor and overview are distinct existing entry points.
    repo_tool('ui_select_view', view='working_copy')
    sweep('commit-editor', '文件状态', True)
    # Exercise the actual UI click handlers, then restore the fixture's original
    # index. This checks operation targets as well as geometry after dragging.
    repo_tool('ui_diff', action='select', source='unstaged', file='text.txt')
    click_ready('StageSelectedChanges')
    wait_for(lambda: git('show', ':text.txt') == 'staged change\nunstaged change')
    check('stage selected button stages exactly the selected text content', git('diff', '--name-only', '--', 'text.txt') == '')
    repo_tool('ui_diff', action='select', source='staged', file='text.txt')
    click_ready('UnstageSelectedChanges')
    wait_for(lambda: git('diff', '--cached', '--name-only', '--', 'text.txt') == '')
    check('unstage selected button leaves working file intact', (repo / 'text.txt').read_text() == 'staged change\nunstaged change\n')
    click_ready('StageAllChanges')
    wait_for(lambda: git('status', '--porcelain') and all(line[1] == ' ' for line in git('status', '--porcelain').splitlines()))
    check('stage all includes nested untracked files and binary changes', len(git('diff', '--cached', '--name-only').splitlines()) == 4)
    repo_tool('ui_working_copy', filter='text.txt')
    click_ready('UnstageAllChanges')
    wait_for(lambda: len(git('diff', '--cached', '--name-only').splitlines()) == 3)
    check('filtered unstage all preserves excluded staged files', 'text.txt' not in git('diff', '--cached', '--name-only').splitlines())
    click_ready('StageAllChanges')
    wait_for(lambda: len(git('diff', '--cached', '--name-only').splitlines()) == 4)
    check('filtered stage all refreshes the just-unstaged file', git('diff', '--name-only', '--', 'text.txt') == '')
    repo_tool('ui_working_copy', filter='')
    click_ready('UnstageAllChanges')
    wait_for(lambda: git('diff', '--cached', '--name-only') == '')
    check('unstage all preserves all working files', (folder / 'a very long untracked filename with spaces and unicode 中文.txt').exists() and (repo / 'firmware.bin').stat().st_size == 16384)
    blocked_temp = root / 'unavailable-temp-directory'
    blocked_temp.write_text('A fixture file deliberately cannot be used as a directory.', encoding='utf-8')
    wait_for(lambda: 'text.txt' in state()['workingCopyState']['unstaged'])
    repo_tool('ui_diff', action='select', source='unstaged', file='text.txt')
    click_ready('StageSelectedChanges', tempDirectory=str(blocked_temp))
    wait_for(lambda: state()['workingCopyState']['canChangeIndex'])
    check('failed stage keeps index and selected file intact', git('diff', '--cached', '--name-only') == '' and state()['workingCopyState']['selectedUnstaged'] == ['text.txt'])
    check('failed stage explains the recoverable problem', tool('ui_state')['activePage']['notifications'] > 0)
    repo_tool('ui_command', command='clear_notifications')
    click_ready('StageSelectedChanges')
    wait_for(lambda: git('diff', '--cached', '--name-only') == 'text.txt')
    wait_for(lambda: 'text.txt' in state()['workingCopyState']['staged'])
    repo_tool('ui_diff', action='select', source='staged', file='text.txt')
    click_ready('UnstageSelectedChanges', tempDirectory=str(blocked_temp))
    wait_for(lambda: state()['workingCopyState']['canChangeIndex'])
    check('failed unstage keeps index and selected file intact', git('diff', '--cached', '--name-only') == 'text.txt' and state()['workingCopyState']['selectedStaged'] == ['text.txt'])
    repo_tool('ui_command', command='clear_notifications')
    click_ready('UnstageSelectedChanges')
    wait_for(lambda: git('diff', '--cached', '--name-only') == '')
    check('temporary write errors recover without restarting TreeGit', p.poll() is None and (repo / 'text.txt').read_text() == 'staged change\nunstaged change\n')
    git('update-index', '--cacheinfo', '100644,' + staged_blob + ',text.txt')
    repo_tool('ui_refresh')
    time.sleep(.5)
    repo_tool('ui_diff', action='select', source='unstaged', file='firmware.bin')
    wait_for(lambda: repo_tool('ui_diff')['status'] != 'not_binary')
    sweep('binary-diff', '文件状态', True)
    repo_tool('ui_diff', action='window')
    inspect('binary-window', capture=True, active=True)
    for zoom in [1, 2]:
        tool('ui_window', {'zoom': zoom})
        for width, height in [(640, 420), (1100, 700)]:
            tool('ui_layout', {'action': 'resize', 'window': 'active', 'width': width, 'height': height})
            inspect(f'binary-window-{width}-zoom{zoom}', capture=True, active=True)
            comparison = repo_tool('ui_diff')
            check(f'binary {width} zoom{zoom}: full hex and ASCII cells fit both panes', comparison['requiredPaneWidth'] <= min(comparison['leftPaneWidth'], comparison['rightPaneWidth']) + .5, comparison)
    tool('ui_window', {'zoom': 1})
    tool('ui_command', {'command': 'close_dialog'})
    repo_tool('ui_diff', action='select', source='unstaged', file='image.png')
    wait_for(lambda: any(p['type'] == 'ImageDiffView' for p in tool('ui_layout')['panes']))
    sweep('image-diff', '文件状态', True)
    stash = repo_tool('ui_stashes')['stashes'][0]['sha']
    repo_tool('ui_stashes', action='select', stash=stash)
    wait_for(lambda: repo_tool('ui_stashes').get('changes'))
    repo_tool('ui_diff', action='select', source='stash', file='text.txt')
    sweep('stash', captures=True)
    for theme, zoom in [('Light', .75), ('Dark', 1), ('Blue', 1.25), ('Light', 1.5), ('Dark', 2), ('Blue', 2.5)]:
        tool('ui_window', {'theme': theme, 'zoom': zoom, 'state': 'maximized'})
        repo_tool('ui_working_copy')
        sweep(f'zoom-{theme}-{zoom}', '文件状态', True)
        geometry = tool('ui_layout')
        viewport = next(s for s in geometry['scrolls'] if s['name'] == 'PageViewport' and s['visible'])
        if viewport['extent']['width'] > viewport['viewport']['width'] + 1 or viewport['extent']['height'] > viewport['viewport']['height'] + 1:
            tool('ui_layout', {'action': 'scroll', 'scroller': viewport['index'], 'dx': 5000, 'dy': 5000})
            geometry = inspect(f'zoom-{theme}-{zoom}-scroll-end', '文件状态', capture=True)
            last = next(s for s in geometry['scrolls'] if s['index'] == viewport['index'])
            check(f'zoom-{theme}-{zoom}: overflow is reachable by scrolling',
                  abs(last['offset']['x'] + last['viewport']['width'] - last['extent']['width']) < 1.1 and abs(last['offset']['y'] + last['viewport']['height'] - last['extent']['height']) < 1.1)
            repo_tool('ui_select_view', view='search')
            time.sleep(.1)
            fresh = next(s for s in tool('ui_layout')['scrolls'] if s['name'] == 'PageViewport' and s['visible'])
            check(f'zoom-{theme}-{zoom}: new page starts at its own top left', fresh['offset']['x'] == 0 and fresh['offset']['y'] == 0)
            repo_tool('ui_working_copy')
    tool('ui_window', {'theme': 'Blue', 'zoom': 1, 'state': 'normal', 'width': 1024, 'height': 640})
    inspect('minimum-normal-window', '文件状态', capture=True)
    tool('ui_window', {'state': 'maximized'})
    sidebar = next(s for s in tool('ui_layout')['splitters'] if s['owner'] == 'Repository' and s['visible'])
    tool('ui_layout', {'action': 'drag', 'splitter': sidebar['index'], 'dx': 100})
    time.sleep(.1)
    preferred_sidebar = tool('ui_layout')['preferredSidebarWidth']
    tool('ui_window', {'zoom': 2.5})
    time.sleep(.1)
    tool('ui_window', {'zoom': 1})
    restored_layout = inspect('sidebar-zoom-roundtrip', '文件状态', capture=True)
    restored_sidebar = next(s for s in restored_layout['splitters'] if s['owner'] == 'Repository' and s['visible'])
    check('zoom roundtrip preserves the user sidebar width', abs(restored_layout['preferredSidebarWidth'] - preferred_sidebar) < 1.1 and abs(restored_sidebar['actualColumns'][0] - preferred_sidebar) < 1.1)
    tool('ui_layout', {'action': 'restore', 'splitter': sidebar['index'], 'columns': sidebar['columns'], 'rows': sidebar['rows']})
    repo_tool('ui_select_view', view='history')
    repo_tool('ui_select_commit', revision=head)
    check('resizing never switches branch', git('branch', '--show-current') == 'main')
    check('resizing preserves staged and unstaged content', git('show', ':text.txt') == 'staged change' and (repo / 'text.txt').read_text() == 'staged change\nunstaged change\n')
    check('resizing never changes history target', state()['detailCommit'] == head)
    inspect('final-restored', '日志 / 历史', capture=True)
    expected_columns = repo_tool('ui_history_columns', graph=160, description=600, date=190, author=220)
    stop_session()
    pid = None
    p, messages = start_process()
    seq = 0
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    repo_tool('ui_open_repository')
    pid = tool('ui_state')['processId']
    wait_for(lambda: state()['graphCommits'])
    tool('ui_window', {'state': 'maximized', 'theme': 'Blue', 'zoom': 1})
    actual_columns = repo_tool('ui_history_columns')
    check('column widths survive closing and reopening', all(abs(actual_columns[k] - expected_columns[k]) < 1.1 for k in ['graph', 'description', 'date', 'author']), actual_columns)
    repo_tool('ui_select_commit', revision=head)
    inspect('cold-reopen-saved-layout', '日志 / 历史', capture=True)
    check('reopening a resized layout preserves HEAD and the index', git('rev-parse', 'HEAD') == head and git('rev-parse', ':text.txt') == staged_blob)

except Exception as error:
    checks.append({'name': 'audit exception', 'ok': False, 'detail': repr(error)})
    print('FAIL audit exception: ' + repr(error), flush=True)
    try: screenshot('failure')
    except Exception: pass
finally:
    (out / 'layout-audit-results.json').write_text(json.dumps({'checks': checks, 'failures': [c for c in checks if not c['ok']], 'records': records, 'pointer': pointer_records, 'stderr': errors[-15:]}, ensure_ascii=False, indent=2), encoding='utf-8')
    stop_session()
    def readonly(fn, path, _):
        owned = Path(path)
        assert owned.resolve().is_relative_to(root) and not owned.is_symlink() and not owned.is_junction()
        os.chmod(path, stat.S_IWRITE)
        fn(path)
    assert root.name.startswith('TreeGit resize audit ') and repo.parent == root and not root.is_symlink()
    shutil.rmtree(root, onerror=readonly)
    (data / 'preference.json').write_text(json.dumps(preferences), encoding='utf-8')
assert not [c for c in checks if not c['ok']], 'See layout-audit-results.json'
