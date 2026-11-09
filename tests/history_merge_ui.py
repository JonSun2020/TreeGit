"""Exercise history and merge failure paths with real Git and native GUI buttons.

Native input is restricted to the owned Windows CI process. No business repository,
installed application, or user configuration is used.
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
import subprocess
import sys
import tempfile
import threading
import time

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit merge flow QA ')).resolve()
repo = root / 'repo'; repo.mkdir()
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root / 'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1')
checks = []; failure = None; pid = None; motion_samples = []

def git(*args, ok=True):
    r = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8')
    if ok: assert r.returncode == 0, (args, r.stdout, r.stderr)
    return r

def write(name, text): (repo / name).write_text(text, encoding='utf-8')
def commit(message):
    git('add', '-A'); git('commit', '-m', message)
    return git('rev-parse', 'HEAD').stdout.strip()

git('init', '-b', 'main')
git('config', 'user.name', 'Merge flow QA'); git('config', 'user.email', 'qa@example.invalid')
git('config', 'core.autocrlf', 'false')
write('values.txt', 'base\n'); write('draft.txt', 'original draft file\n'); write('.gitignore', 'ignored.data\n')
base = commit('Base')
git('checkout', '-b', 'conflicting'); write('values.txt', 'remote\n'); conflict_sha = commit('Remote conflict')
git('checkout', '-b', 'safe', base); write('remote.txt', 'remote addition\n'); safe_sha = commit('Remote addition')
git('checkout', '-b', 'untracked-collision', base); write('future.txt', 'incoming\n'); collision_sha = commit('Incoming tracked file')
git('checkout', '-b', 'ignored-collision', base); write('ignored.data', 'incoming ignored path\n'); git('add', '-f', 'ignored.data'); ignored_sha = commit('Incoming ignored file')
git('checkout', '-b', 'restore-conflict', base); write('values.txt', 'incoming restore\n'); restore_sha = commit('Incoming restore')
git('checkout', '--orphan', 'unrelated'); git('rm', '-rf', '.'); write('independent.txt', 'independent\n'); unrelated_sha = commit('Independent history')
git('checkout', 'main'); write('values.txt', 'local\n'); main = commit('Local head')
git('branch', 'ff-target', base)
remote = root / 'remote.git'
subprocess.run(['git', 'clone', '--bare', str(repo), str(remote)], env=env, capture_output=True, check=True)
git('remote', 'add', 'origin', str(remote))
git('update-ref', 'refs/remotes/origin/ff-safe', safe_sha)
git('update-ref', 'refs/remotes/origin/main', main); git('branch', '--set-upstream-to=origin/main', 'main')

prefs = exe.parent / 'data/preference.json'; prefs.parent.mkdir(exist_ok=True)
default_prefs = {'Locale': 'zh_CN', 'Theme': 'Blue', 'EnableAutoFetch': False, 'Check4UpdatesOnStartup': False, 'UseStashAndReapplyByDefault': True}
prefs.write_text(json.dumps(default_prefs), encoding='utf-8')
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue(); threading.Thread(target=lambda: [messages.put(line) for line in p.stdout], daemon=True).start(); seq = 0

def rpc(method, params=None):
    global seq
    seq += 1; p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': seq, 'method': method, 'params': params or {}}) + '\n'); p.stdin.flush()
    r = json.loads(messages.get(timeout=60)); assert 'error' not in r, r
    return r['result']

def tool(name, args=None):
    r = rpc('tools/call', {'name': name, 'arguments': args or {}}); assert not r.get('isError'), r
    return base64.b64decode(r['content'][0]['data']) if name == 'ui_screenshot' else json.loads(r['content'][0]['text'])

def check(label, condition):
    assert condition, label
    checks.append(label); print('PASS ' + label, flush=True)

def wait_for(fn):
    for _ in range(200):
        r = fn()
        if r: return r
        time.sleep(.1)
    raise AssertionError('Timed out waiting for ' + repr(fn))

def state(): return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)
def layout(): return tool('ui_layout')
def shot(name): (out / (name + '.png')).write_bytes(tool('ui_screenshot'))
def command(name, **args): return tool('ui_command', {'repo': str(repo), 'command': name, **args})
def refresh():
    before = state()['workingCopyRefreshVersion']
    actual_branch = git('symbolic-ref', '--quiet', 'HEAD', ok=False)
    actual_head = git('rev-parse', 'HEAD').stdout.strip()
    tool('ui_refresh', {'repo': str(repo)})
    wait_for(lambda: state()['workingCopyRefreshVersion'] > before and state()['workingCopyStatusKnown'] and not state()['graphLoading'])
    if actual_branch.returncode == 0:
        reference = actual_branch.stdout.strip()
        wait_for(lambda: state()['branch'] == reference.removeprefix('refs/heads/') and any(b['reference'] == reference and b['head'] == actual_head for b in state()['loadedBranches']))

api = ctypes.WinDLL('user32', use_last_error=True)
api.WindowFromPoint.argtypes = [wintypes.POINT]; api.WindowFromPoint.restype = wintypes.HWND
api.GetForegroundWindow.restype = wintypes.HWND
api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
api.SetForegroundWindow.argtypes = [wintypes.HWND]
api.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]; api.GetAncestor.restype = wintypes.HWND
api.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
api.IsWindowVisible.argtypes = [wintypes.HWND]
enum_callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
api.EnumWindows.argtypes = [enum_callback, wintypes.LPARAM]
def owner(hwnd):
    value = wintypes.DWORD(); api.GetWindowThreadProcessId(hwnd, ctypes.byref(value)); return value.value

def activate_main():
    owned = []
    def collect(hwnd, _):
        if owner(hwnd) == pid and api.IsWindowVisible(hwnd):
            bounds = wintypes.RECT()
            if api.GetWindowRect(hwnd, ctypes.byref(bounds)):
                owned.append(((bounds.right-bounds.left)*(bounds.bottom-bounds.top), hwnd))
        return True
    assert api.EnumWindows(enum_callback(collect), 0)
    assert owned, 'Owned CI main window is unavailable'
    hwnd = max(owned)[1]; assert owner(hwnd) == pid
    api.SetForegroundWindow(hwnd)
    return hwnd

api.GetDC.argtypes = [wintypes.HWND]; api.GetDC.restype = wintypes.HDC
api.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
gdi = ctypes.WinDLL('gdi32', use_last_error=True)
gdi.GetPixel.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]; gdi.GetPixel.restype = wintypes.DWORD

def click_pending_and_measure(label):
    activate_main()
    row = state()['pendingRow']; scale = layout()['window']['scaling']
    point = wintypes.POINT(round(row['x'] + 6*scale), round(row['y'] + 9.5*scale))
    assert owner(api.WindowFromPoint(point)) == pid
    left = round(row['subjectX'] - scale)
    top = round(row['y'] + 3*scale); bottom = round(row['y'] + 16*scale)
    samples = []; stopped = threading.Event(); ready = threading.Event()
    # Capture one small top-down DIB per frame. GetPixel performs a synchronous
    # desktop read on every call and cannot measure a transient text shift.
    class BitmapHeader(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('width', wintypes.LONG), ('height', wintypes.LONG),
                    ('planes', wintypes.WORD), ('bits', wintypes.WORD), ('compression', wintypes.DWORD),
                    ('image_size', wintypes.DWORD), ('xppm', wintypes.LONG), ('yppm', wintypes.LONG),
                    ('used', wintypes.DWORD), ('important', wintypes.DWORD)]
    gdi.CreateCompatibleDC.argtypes = [wintypes.HDC]; gdi.CreateCompatibleDC.restype = wintypes.HDC
    gdi.CreateDIBSection.argtypes = [wintypes.HDC, ctypes.POINTER(BitmapHeader), wintypes.UINT, ctypes.POINTER(ctypes.c_void_p), wintypes.HANDLE, wintypes.DWORD]
    gdi.CreateDIBSection.restype = wintypes.HANDLE
    gdi.SelectObject.argtypes = [wintypes.HDC, wintypes.HANDLE]; gdi.SelectObject.restype = wintypes.HANDLE
    gdi.BitBlt.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
    gdi.DeleteObject.argtypes = [wintypes.HANDLE]; gdi.DeleteDC.argtypes = [wintypes.HDC]
    def contrast(a, b):
        return max(abs(((a >> shift) & 255) - ((b >> shift) & 255)) for shift in (0, 8, 16))
    def observe():
        dc = api.GetDC(None); memory = gdi.CreateCompatibleDC(dc)
        width = round(row['subjectX'] - row['x'] + 125*scale); height = round(19*scale)
        header = BitmapHeader(ctypes.sizeof(BitmapHeader), width, -height, 1, 32, 0, width*height*4, 0, 0, 0, 0)
        bits = ctypes.c_void_p(); bitmap = gdi.CreateDIBSection(dc, ctypes.byref(header), 0, ctypes.byref(bits), None, 0)
        assert bitmap and bits.value
        old = gdi.SelectObject(memory, bitmap); pixels = (ctypes.c_uint32*(width*height)).from_address(bits.value)
        first_x = left - row['x']; top_y = top - row['y']; bottom_y = bottom - row['y']
        ready.set()
        try:
            while not stopped.is_set():
                assert gdi.BitBlt(memory, 0, 0, width, height, dc, row['x'], row['y'], 0x00CC0020)
                background = pixels[top_y*width + first_x - round(scale)]
                first = None
                for x in range(first_x, first_x + round(120*scale)):
                    if sum(contrast(pixels[y*width+x], background) > 80 for y in range(top_y, bottom_y)) >= 2:
                        first = x + row['x']; break
                samples.append({'dot': pixels[round(9.5*scale)*width + round(6*scale)] & 0xffffff, 'inkX': first})
                time.sleep(.008)
        finally:
            gdi.SelectObject(memory, old); gdi.DeleteObject(bitmap); gdi.DeleteDC(memory); api.ReleaseDC(None, dc)
    observer = threading.Thread(target=observe); observer.start()
    try:
        assert ready.wait(5), 'Desktop frame observer did not initialize'
        press('WorkingCopyRow'); wait_for(lambda: state()['workingCopySelected']); time.sleep(.5)
    finally: stopped.set(); observer.join(timeout=3)
    motion_samples.append({'case': label, 'row': row, 'scale': scale, 'samples': samples})
    (out / 'pending-row-motion.json').write_text(json.dumps(motion_samples, indent=2), encoding='utf-8')
    check(label + ': pending graph marker stays at the same physical pixel throughout selection', len(samples) >= 10 and all(p['dot'] == 0x8a8a8a for p in samples))
    positions = [p['inkX'] for p in samples]
    check(label + ': rendered description ink stays fixed throughout selection', len(samples) >= 10 and None not in positions and max(positions) - min(positions) <= 1)

def open_commit_menu(sha):
    tool('ui_select_commit', {'repo': str(repo), 'revision': sha})
    row = wait_for(lambda: next((r for r in state()['historyRows'] if r['sha'] == sha), None))
    hwnd = activate_main(); point = wintypes.POINT(row['x'], row['y'])
    assert owner(api.WindowFromPoint(point)) == pid and api.GetAncestor(api.WindowFromPoint(point), 2) == hwnd
    assert api.SetCursorPos(point.x, point.y)
    api.mouse_event(8, 0, 0, 0, 0); api.mouse_event(16, 0, 0, 0, 0)
    return wait_for(lambda: state()['commitMenu'])['items']

def close_commit_menu():
    assert owner(api.GetForegroundWindow()) == pid
    api.keybd_event(0x1B, 0, 0, 0); api.keybd_event(0x1B, 0, 2, 0)
    wait_for(lambda: state()['commitMenu'] is None)

def press(name):
    main = activate_main()
    def available(): return next((b for b in layout()['buttons'] if b['name'] == name and b['enabled']), None)
    b = wait_for(available)
    # Wait for the real button to finish moving during popup/detail layout.
    stable = 0
    for _ in range(60):
        time.sleep(.05); current = available()
        stable = stable + 1 if current and current['bounds'] == b['bounds'] and current['screen'] == b['screen'] else 0
        if current: b = current
        if stable >= 3: break
    assert stable >= 3, 'Button layout did not settle: ' + name
    hwnd = api.GetForegroundWindow(); assert owner(hwnd) == pid, 'Native input outside owned CI GUI'
    point = wintypes.POINT(b['screen']['x'], b['screen']['y'])
    assert owner(api.WindowFromPoint(point)) == pid, 'Pointer target outside owned CI GUI'
    assert api.GetAncestor(api.WindowFromPoint(point), 2) == main, 'An owned dialog is covering the main-window button'
    assert api.SetCursorPos(point.x, point.y)
    api.mouse_event(2, 0, 0, 0, 0); api.mouse_event(4, 0, 0, 0, 0)

def merge(revision, mode=None):
    command('clear_notifications')
    command('merge_dialog', revision=revision)
    wait_for(lambda: state()['mergeDialog'])
    if mode is not None: command('merge_options', mode=mode)
    wait_for(lambda: state()['mergeDialog']['testing'] != 'Testing')

def confirm():
    before = state()['pendingDialogAttemptVersion']
    press('ConfirmPopup')
    wait_for(lambda: state()['pendingDialogAttemptVersion'] > before)
def completed(): wait_for(lambda: state()['pendingDialog'] is None)
def error(): return wait_for(lambda: (state()['mergeDialog'] or {}).get('error'))
def cancel():
    wait_for(lambda: not state()['pendingDialogInProgress'])
    response = command('cancel_popup')
    assert response.get('cancelled'), ('Idle popup could not be cancelled', response, state())
    wait_for(lambda: state()['pendingDialog'] is None)
def pending(value): wait_for(lambda: state()['hasPendingChanges'] == value and state()['pendingRow']['visible'] == value)
def reset():
    if state()['pendingDialog']: cancel()
    if (repo / '.git/MERGE_HEAD').exists(): git('merge', '--abort')
    git('reset', '--hard', main); git('clean', '-fdx'); git('stash', 'clear')
    tool('ui_working_copy', {'repo': str(repo), 'message': '', 'filter': ''})
    refresh(); wait_for(lambda: state()['changes'] == 0); pending(False)

try:
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    tool('ui_open_repository', {'repo': str(repo)}); pid = tool('ui_state')['processId']
    tool('ui_window', {'state': 'maximized', 'theme': 'Blue'})
    refresh(); pending(False)
    check('clean repository has no synthetic history row', not state()['pendingRow']['visible'])
    tool('ui_working_copy', {'repo': str(repo)})
    check('clean working-copy page remains accessible', state()['detailView'] == 'WorkingCopy')
    write('values.txt', 'local edit\n'); refresh(); pending(True)
    check('tracked change reveals the history row', state()['changes'] == 1)
    tool('ui_select_commit', {'repo': str(repo), 'revision': base})
    click_pending_and_measure('Blue 100%')
    for theme, zoom in [('Light', 1), ('Dark', 1), ('Light', 1.5), ('Blue', 1.5)]:
        tool('ui_window', {'state': 'maximized', 'theme': theme, 'zoom': zoom})
        tool('ui_select_commit', {'repo': str(repo), 'revision': base}); time.sleep(.2)
        click_pending_and_measure(f'{theme} {zoom*100:g}%')
    tool('ui_window', {'state': 'maximized', 'theme': 'Blue', 'zoom': 1})
    items = open_commit_menu(main)
    expected = ['CommitCheckout', 'CommitMerge', 'CommitRebase', 'CommitTag', 'CommitArchive', 'CommitBranch',
                'CommitInteractiveRebase', 'CommitReset', 'CommitRevert', 'CommitPatch', 'CommitCherryPick', 'CommitCopySHA', 'CommitAdvanced']
    check('commit context menu exposes SourceTree common actions in order', [i['name'] for i in items] == expected)
    check('reset to current HEAD remains available while redundant merge and rebase are disabled', next(i for i in items if i['name'] == 'CommitReset')['enabled'] and not any(i['enabled'] for i in items if i['name'] in ['CommitMerge', 'CommitRebase', 'CommitInteractiveRebase']))
    shot('commit-menu-current-head')
    reset_item = next(i for i in items if i['name'] == 'CommitReset')
    point = wintypes.POINT(reset_item['x'], reset_item['y']); assert owner(api.WindowFromPoint(point)) == pid
    api.SetCursorPos(point.x, point.y); api.mouse_event(2, 0, 0, 0, 0); api.mouse_event(4, 0, 0, 0, 0)
    wait_for(lambda: state()['pendingDialog'] == 'Reset'); cancel()
    check('current HEAD reset opens a real cancellable operation without changing files', (repo / 'values.txt').read_text() == 'local edit\n' and git('rev-parse', 'HEAD').stdout.strip() == main)
    tool('ui_working_copy', {'repo': str(repo)})
    check('native row click opens working copy without losing history columns', state()['pendingRow']['visible'] and state()['detailView'] == 'WorkingCopy')
    tool('ui_history_columns', {'repo': str(repo), 'graph': 120, 'description': 1000, 'date': 200, 'author': 200, 'commit': 160})
    horizontal = wait_for(lambda: next((b for b in layout()['scrollBars'] if b['visible'] and b['orientation'] == 'Horizontal' and b['owner'] == 'Histories' and b['maximum'] > 100), None))
    subject_left = state()['pendingRow']['subjectX']; graph_left = state()['pendingRow']['x']
    tool('ui_layout', {'action': 'scroll', 'scrollbar': horizontal['index'], 'value': 100})
    wait_for(lambda: abs(state()['pendingRow']['subjectX'] - subject_left + 100) <= 1)
    check('pending description scrolls with its real history column while graph stays frozen', state()['pendingRow']['x'] == graph_left)
    tool('ui_history_columns', {'repo': str(repo), 'reset': True})
    git('add', 'values.txt'); refresh(); pending(True)
    check('staged-only change keeps the row visible', state()['workingCopyState']['staged'] == ['values.txt'])
    config = repo / '.git/config'; saved_config = config.read_bytes(); before = state()['workingCopyRefreshVersion']
    config.write_bytes(b'[broken')
    tool('ui_refresh', {'repo': str(repo)})
    wait_for(lambda: state()['workingCopyRefreshVersion'] > before and not state()['workingCopyStatusKnown'])
    check('status failure preserves the previous pending row and staged list', state()['hasPendingChanges'] and state()['pendingRow']['visible'] and state()['workingCopyState']['staged'] == ['values.txt'])
    config.write_bytes(saved_config); refresh()
    check('status display recovers after a transient read failure', state()['workingCopyStatusKnown'] and state()['workingCopyState']['staged'] == ['values.txt'])
    reset(); write('new [1].txt', 'untracked\n'); refresh(); pending(True)
    check('untracked file reveals the row', state()['workingCopyState']['unstaged'] == ['new [1].txt'])
    reset(); write('ignored.data', 'ignored only\n'); refresh(); pending(False)
    check('ignored-only files do not add a pending row', state()['changes'] == 0)
    reset()

    # A successful no-op stash must never reapply a pre-existing stash.
    write('draft.txt', 'old stash content\n'); git('stash', 'push', '-m', 'Unrelated old stash')
    old_stash = git('rev-parse', 'refs/stash').stdout.strip()
    refresh(); command('stash_dialog'); command('stash_options', stashMode='keep_all', onlyStaged=False)
    confirm(); completed()
    check('no-op keep-all stash does not apply a previous stash', (repo / 'draft.txt').read_text() == 'original draft file\n' and git('rev-parse', 'refs/stash').stdout.strip() == old_stash)
    command('stash_dialog'); command('stash_options', stashMode='keep_all', onlyStaged=True)
    confirm(); completed()
    check('no staged changes cannot reapply a previous stash', (repo / 'draft.txt').read_text() == 'original draft file\n' and git('rev-parse', 'refs/stash').stdout.strip() == old_stash)
    reset()
    write('draft.txt', 'staged draft edit\n'); git('add', 'draft.txt')
    write('values.txt', 'unstaged values edit\n'); write('new [1].txt', 'untracked edit\n')
    original_index = git('ls-files', '--stage').stdout
    command('stash_dialog'); command('stash_options', stashMode='keep_all', onlyStaged=False, includeUntracked=True)
    confirm(); completed(); refresh()
    check('keep-all stash restores staged unstaged and literal untracked contents', (repo / 'draft.txt').read_text() == 'staged draft edit\n' and (repo / 'values.txt').read_text() == 'unstaged values edit\n' and (repo / 'new [1].txt').read_text() == 'untracked edit\n')
    check('keep-all stash restores the exact index', git('ls-files', '--stage').stdout == original_index)
    check('one stash operation produces exactly one recoverable stash', len(git('stash', 'list').stdout.splitlines()) == 1)
    reset()

    draft = 'Authored message that must survive merge\nsecond line'
    tool('ui_working_copy', {'repo': str(repo), 'message': draft, 'filter': 'not-the-conflict'})
    merge(conflict_sha); confirm(); completed()
    wait_for(lambda: state()['workingCopyState']['selectedUnstaged'] == ['values.txt'])
    check('conflicting commits enter normal manual resolution', bool(git('ls-files', '-u').stdout) and (repo / '.git/MERGE_HEAD').exists())
    check('conflict is selected and old file filter is cleared', state()['workingCopySelected'] and state()['workingCopyState']['selectedUnstaged'] == ['values.txt'])
    check('authored commit draft survives merge and message refresh', state()['workingCopyState']['message'] == draft)
    tool('ui_graph', {'repo': str(repo), 'mode': 'current', 'showRemotes': False})
    wait_for(lambda: not state()['graphLoading'] and {p['sha'] for p in state()['pendingRow']['parents']} == {main, conflict_sha})
    parents = state()['pendingRow']['parents']
    check('current-branch graph includes both actual pending merge parents', len(parents) == 2 and len({p['x'] for p in parents}) == 2)
    shot('pending-merge-two-parents')
    items = open_commit_menu(main)
    check('unfinished merge keeps common actions visible and disables another merge rebase or cherry-pick', not any(i['enabled'] for i in items if i['name'] in ['CommitCheckout', 'CommitMerge', 'CommitRebase', 'CommitRevert', 'CommitCherryPick']))
    close_commit_menu()
    tool('ui_working_copy', {'repo': str(repo)})
    shot('commit-conflict')
    index_before = git('ls-files', '-u').stdout
    merge(safe_sha); confirm(); completed()
    check('second merge cannot replace an unfinished operation', git('ls-files', '-u').stdout == index_before and git('rev-parse', 'HEAD').stdout.strip() == main)
    reset()
    write('values.txt', 'post-abort draft\n'); refresh()
    check('aborting merge removes incoming parent while ordinary pending changes remain', {p['sha'] for p in state()['pendingRow']['parents']} == {main})
    tool('ui_graph', {'repo': str(repo), 'mode': 'all', 'showRemotes': True}); reset()

    merge(conflict_sha); git('branch', '-f', 'conflicting', safe_sha); confirm(); completed()
    check('merge uses the selected immutable commit after its source branch moves', (repo / '.git/MERGE_HEAD').read_text().strip() == conflict_sha and bool(git('ls-files', '-u').stdout))
    reset(); git('branch', '-f', 'conflicting', conflict_sha)

    tool('ui_working_copy', {'repo': str(repo), 'message': draft})
    command('merge_multiple_dialog', revisions=[safe_sha, collision_sha]); confirm(); completed()
    check('multiple-head merge preserves the draft and merges exact selected commits', state()['workingCopyState']['message'] == draft and (repo / 'remote.txt').exists() and (repo / 'future.txt').exists() and len(git('rev-list', '--parents', '-n', '1', 'HEAD').stdout.split()) == 4)
    reset(); command('clear_notifications')
    command('merge_multiple_dialog', revisions=[conflict_sha, safe_sha]); confirm()
    wait_for(lambda: state()['pendingDialog'] is None or not state()['pendingDialogInProgress'] and tool('ui_state')['activePage']['notifications'] > 0)
    check('complex multiple-head merge never reports a false success', bool(git('ls-files', '-u').stdout) and state()['workingCopySelected'] or state()['pendingDialog'] == 'MergeMultiple' and git('rev-parse', 'HEAD').stdout.strip() == main)
    reset()

    local_draft = b'local dirty content\n'; (repo / 'draft.txt').write_bytes(local_draft)
    git('add', 'draft.txt'); refresh()
    merge(conflict_sha); confirm(); completed()
    check('dirty staged edits do not block starting a conflicting merge', bool(git('ls-files', '-u').stdout))
    check('local edits are protected in the merge autostash', (repo / '.git/MERGE_AUTOSTASH').exists())
    git('merge', '--abort')
    check('aborting merge restores local file contents', (repo / 'draft.txt').read_bytes() == local_draft and git('rev-parse', 'HEAD').stdout.strip() == main)
    reset()

    (repo / 'draft.txt').write_bytes(local_draft); refresh()
    merge(safe_sha); confirm(); completed()
    check('successful merge restores unrelated dirty contents', (repo / 'draft.txt').read_bytes() == local_draft)
    check('local dirty contents do not enter the merge commit', git('show', 'HEAD:draft.txt').stdout == 'original draft file\n')
    reset()

    # The merge itself is clean, but applying the protected local edit conflicts.
    git('reset', '--hard', base); write('values.txt', 'uncommitted local restore\n'); refresh()
    merge(restore_sha); confirm(); completed()
    wait_for(lambda: state()['workingCopyState']['selectedUnstaged'] == ['values.txt'])
    check('autostash restoration conflict is handled even when merge exits successfully', bool(git('ls-files', '-u').stdout) and state()['workingCopySelected'])
    check('restoration conflict retains the local edit in a recoverable stash', bool(git('stash', 'list').stdout))
    shot('restore-conflict'); reset()

    for name, revision in [('future.txt', collision_sha), ('ignored.data', ignored_sha)]:
        contents = b'local data must never be overwritten\n'; (repo / name).write_bytes(contents); refresh()
        merge(revision); confirm(); message = error()
        check('incoming path collision keeps the dialog open: ' + name, bool(message) and state()['pendingDialog'] == 'Merge')
        check('incoming path collision preserves local bytes and HEAD: ' + name, (repo / name).read_bytes() == contents and git('rev-parse', 'HEAD').stdout.strip() == main)
        if name == 'ignored.data':
            cancel()
            response = rpc('tools/call', {'name': 'git_action', 'arguments': {'repo': str(repo), 'action': 'merge', 'revision': revision}})
            try: payload = json.loads(response['content'][0]['text'])
            except json.JSONDecodeError: payload = {}
            check('MCP merge enforces the same ignored-file protection', (response.get('isError') or payload.get('error')) and (repo / name).read_bytes() == contents and git('rev-parse', 'HEAD').stdout.strip() == main)
        cancel(); reset()

    git('config', 'merge.ff', 'only'); merge(safe_sha); confirm(); error()
    check('fast-forward-only failure offers explicit regular merge', state()['mergeDialog']['canUseRegularMerge'])
    press('UseRegularMerge'); confirm(); completed()
    check('explicit regular merge succeeds without changing Git preference', git('config', '--get', 'merge.ff').stdout.strip() == 'only' and len(git('rev-list', '--parents', '-n', '1', 'HEAD').stdout.split()) == 3)
    git('config', '--unset', 'merge.ff'); reset()

    merge(unrelated_sha); confirm(); error()
    check('unrelated history requires explicit consent in the same dialog', state()['mergeDialog']['showAllowUnrelated'] and not state()['mergeDialog']['allowUnrelated'])
    command('merge_options', allowUnrelated=True); confirm(); completed()
    check('explicit unrelated-history merge preserves both trees', (repo / 'independent.txt').exists() and (repo / 'values.txt').read_text() == 'local\n')
    reset()

    merge(safe_sha, '--no-ff --no-commit'); confirm(); completed()
    check('no-commit merge shows the pending result for review', state()['workingCopySelected'] and (repo / '.git/MERGE_HEAD').exists() and git('rev-parse', 'HEAD').stdout.strip() == main)
    reset()
    merge(safe_sha, '--squash'); confirm(); completed()
    wait_for(lambda: 'remote.txt' in state()['workingCopyState']['staged'])
    check('squash merge shows staged files without creating a commit', state()['workingCopySelected'] and 'remote.txt' in state()['workingCopyState']['staged'] and git('rev-parse', 'HEAD').stdout.strip() == main)
    reset()

    merge(safe_sha); git('checkout', 'conflicting'); confirm(); error()
    check('changing destination while a dialog is open cannot merge into another branch', git('rev-parse', 'HEAD').stdout.strip() == conflict_sha and not (repo / 'remote.txt').exists())
    cancel(); git('checkout', 'main'); reset()
    merge(safe_sha); git('commit', '--allow-empty', '-m', 'External new destination'); external_head = git('rev-parse', 'HEAD').stdout.strip(); confirm(); error()
    check('moving the same destination branch while a dialog is open is also rejected', git('rev-parse', 'HEAD').stdout.strip() == external_head and not (repo / 'remote.txt').exists())
    cancel(); reset()

    git('config','merge.autoStash','true'); write('values.txt','preserve unchecked auto stash\n'); refresh()
    merge(conflict_sha); command('merge_options',autoStash=False); confirm(); error()
    check('unchecked merge auto stash overrides repository config',git('rev-parse','HEAD').stdout.strip()==main and (repo/'values.txt').read_text()=='preserve unchecked auto stash\n' and not git('ls-files','-u').stdout and not (repo/'.git/MERGE_AUTOSTASH').exists() and not git('stash','list').stdout)
    cancel(); git('config','--unset-all','merge.autoStash'); reset()

    # A rejected checkout must restore this action's stash, preserving both the
    # exact index and any older stash. No blind stash@{0} pop is permitted.
    write('draft.txt', 'older checkout stash\n'); git('stash', 'push', '-m', 'Older checkout stash')
    older = git('rev-parse', 'refs/stash').stdout.strip()
    write('draft.txt', 'staged checkout draft\n'); git('add', 'draft.txt')
    write('future.txt', 'untracked collision remains\n'); before_index = git('ls-files', '--stage').stdout; refresh()
    command('checkout_dialog', to='refs/heads/untracked-collision'); confirm()
    wait_for(lambda: not state()['pendingDialogInProgress'] and state()['pendingDialog'] == 'Checkout')
    wait_for(lambda: (repo / 'draft.txt').read_text() == 'staged checkout draft\n')
    check('failed checkout restores local bytes and exact staged index', git('rev-parse', 'HEAD').stdout.strip() == main and git('ls-files', '--stage').stdout == before_index and (repo / 'future.txt').read_text() == 'untracked collision remains\n')
    check('failed checkout never applies or drops an older stash', git('rev-parse', 'refs/stash').stdout.strip() == older and len(git('stash', 'list').stdout.splitlines()) == 1)
    cancel(); wait_for(lambda: state()['logs']['active'] == 0); reset()

    write('draft.txt', 'preserved on checkout\n'); git('add', 'draft.txt'); refresh()
    command('checkout_dialog', to='refs/heads/safe'); confirm(); completed()
    wait_for(lambda: state()['branch'] == 'safe')
    check('successful branch switch restores staged edits without leaving an auto stash', (repo / 'draft.txt').read_text() == 'preserved on checkout\n' and 'draft.txt' in git('diff', '--cached', '--name-only').stdout.splitlines() and not git('stash', 'list').stdout)
    git('checkout', 'main'); reset()

    write('draft.txt', 'preserved while creating a branch\n'); git('add', 'draft.txt'); refresh()
    command('create_branch'); command('create_branch_options', branchName='', checkoutAfterCreated=True); confirm()
    wait_for(lambda: state()['validationErrors'])
    check('empty branch name shows translated validation without invoking Git', any('请输入分支名称' in e for e in state()['validationErrors']) and all(not e.startswith('Text.') for e in state()['validationErrors']) and git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'main')
    command('create_branch_options', branchName='qa-created', checkoutAfterCreated=True); confirm(); completed()
    check('create and switch branch restores the exact staged working file', git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'qa-created' and (repo / 'draft.txt').read_text() == 'preserved while creating a branch\n' and 'draft.txt' in git('diff', '--cached', '--name-only').stdout.splitlines() and not git('stash', 'list').stdout)
    git('checkout', 'main'); reset(); git('branch', '-D', 'qa-created'); refresh()
    write('draft.txt', 'preserved without switching branches\n'); refresh(); before_create_index = git('ls-files', '--stage').stdout
    command('create_branch'); command('create_branch_options', branchName='qa-stay', checkoutAfterCreated=False); confirm(); completed()
    check('creating a branch without switching leaves current branch contents and index unchanged', git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'main' and git('rev-parse', 'qa-stay').stdout.strip() == main and git('ls-files', '--stage').stdout == before_create_index and (repo / 'draft.txt').read_text() == 'preserved without switching branches\n' and not git('stash', 'list').stdout)
    reset(); git('branch', '-D', 'qa-stay'); refresh()

    write('draft.txt', 'preserved on detached checkout\n'); refresh()
    command('checkout_commit_dialog', revision=base); confirm(); completed()
    check('detached checkout also restores its protected local edits', git('symbolic-ref', '--quiet', 'HEAD', ok=False).returncode == 1 and (repo / 'draft.txt').read_text() == 'preserved on detached checkout\n' and not git('stash', 'list').stdout)
    git('checkout', 'main'); reset()

    refresh(); wait_for(lambda: any(b['reference'] == 'refs/heads/ff-target' and b['head'] == base for b in state()['loadedBranches']))
    command('clear_notifications'); command('checkout_fast_forward_dialog', base='refs/heads/ff-target', to='refs/remotes/origin/ff-safe')
    git('branch', '-f', 'ff-target', main); confirm()
    wait_for(lambda: not state()['pendingDialogInProgress'] and tool('ui_state')['activePage']['notifications'] > 0)
    check('stale fast-forward dialog cannot reset newer local commits', git('rev-parse', 'ff-target').stdout.strip() == main and git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'main')
    cancel(); git('branch', '-f', 'ff-target', base); refresh()
    wait_for(lambda: any(b['reference'] == 'refs/heads/ff-target' and b['head'] == base for b in state()['loadedBranches']))
    write('draft.txt', 'preserved on fast forward\n'); refresh()
    command('checkout_fast_forward_dialog', base='refs/heads/ff-target', to='refs/remotes/origin/ff-safe'); confirm(); completed()
    check('real fast-forward advances the selected branch and restores local edits', git('rev-parse', 'ff-target').stdout.strip() == safe_sha and git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'ff-target' and (repo / 'draft.txt').read_text() == 'preserved on fast forward\n' and not git('stash', 'list').stdout)
    git('checkout', 'main'); reset()

    checkout_hook = repo / '.git/hooks/post-checkout'
    checkout_hook.write_text('#!/bin/sh\necho "checkout hook rejected" >&2\nexit 1\n', encoding='utf-8')
    write('draft.txt', 'recoverable after hook failure\n'); refresh()
    command('checkout_dialog', to='refs/heads/safe'); confirm()
    wait_for(lambda: not state()['pendingDialogInProgress'] and state()['pendingDialog'] == 'Checkout' and state()['workingCopySelected'])
    check('a hook failure after checkout keeps the local edit recoverable and refreshes the true branch', git('symbolic-ref', '--short', 'HEAD').stdout.strip() == 'safe' and bool(git('stash', 'list').stdout) and state()['workingCopySelected'])
    checkout_hook.unlink(); cancel(); wait_for(lambda: state()['logs']['active'] == 0)
    git('checkout', 'main'); reset()

    incoming_tree = git('rev-parse', collision_sha + '^{tree}').stdout.strip()
    pulled_head = git('commit-tree', incoming_tree, '-p', main, '-m', 'Incoming pull update').stdout.strip()
    git('push', 'origin', pulled_head + ':refs/heads/main')
    write('draft.txt', 'staged pull draft\n'); git('add', 'draft.txt'); write('future.txt', 'preserve pull collision\n')
    pull_index = git('ls-files', '--stage').stdout; refresh()
    command('pull_dialog'); confirm()
    wait_for(lambda: state()['pendingDialog'] == 'Pull' and not state()['pendingDialogInProgress'] and state()['logs']['active'] == 0)
    check('failed pull restores staged local edits and preserves colliding untracked files', git('rev-parse', 'HEAD').stdout.strip() == main and git('ls-files', '--stage').stdout == pull_index and (repo / 'draft.txt').read_text() == 'staged pull draft\n' and (repo / 'future.txt').read_text() == 'preserve pull collision\n' and not git('stash', 'list').stdout)
    check('failed pull removes its temporary metadata files', not list((repo / '.git').glob('treegit-pull-*')))
    cancel(); reset()

    write('draft.txt', 'old pull stash\n'); git('stash', 'push', '-m', 'Older pull stash'); old_pull_stash = git('rev-parse', 'refs/stash').stdout.strip()
    write('draft.txt', 'pull interrupted draft\n'); git('add', 'draft.txt'); network_index = git('ls-files', '--stage').stdout
    git('remote', 'set-url', 'origin', str(root / 'unavailable.git')); refresh()
    command('pull_dialog'); confirm()
    wait_for(lambda: state()['pendingDialog'] == 'Pull' and not state()['pendingDialogInProgress'] and state()['logs']['active'] == 0)
    check('unavailable remote restores local bytes and index without touching an older stash', (repo / 'draft.txt').read_text() == 'pull interrupted draft\n' and git('ls-files', '--stage').stdout == network_index and git('rev-parse', 'refs/stash').stdout.strip() == old_pull_stash)
    cancel(); git('remote', 'set-url', 'origin', str(remote)); reset()

    write('draft.txt', 'preserved on successful pull\n'); git('add', 'draft.txt'); refresh()
    command('pull_dialog'); confirm(); completed()
    check('successful pull integrates the fetched update and restores staged work', git('rev-parse', 'HEAD').stdout.strip() == pulled_head and (repo / 'draft.txt').read_text() == 'preserved on successful pull\n' and 'draft.txt' in git('diff', '--cached', '--name-only').stdout.splitlines() and not git('stash', 'list').stdout)
    check('successful pull leaves no private snapshot or message files', not list((repo / '.git').glob('treegit-pull-*')))
    reset(); git('push', '--force', 'origin', main + ':refs/heads/main'); git('update-ref', 'refs/remotes/origin/main', main); refresh()
    git('config', '--unset', 'user.name'); git('config', '--unset', 'user.email')
    merge(safe_sha); confirm(); error()
    check('missing author identity offers repository settings instead of a silent failure', state()['mergeDialog']['showAuthorSettings'])
    cancel(); git('config', 'user.name', 'Merge flow QA'); git('config', 'user.email', 'qa@example.invalid'); reset()

    hook = repo / '.git/hooks/pre-merge-commit'
    hook.write_text('#!/bin/sh\necho invoked >> hook-calls\necho "rejected by local policy" >&2\nexit 1\n', encoding='utf-8')
    merge(safe_sha); confirm(); error()
    check('rejected merge hook keeps the dialog and is never replayed automatically', (repo / 'hook-calls').read_text().splitlines() == ['invoked'])
    check('hook rejection retains staged merge result and original HEAD', (repo / '.git/MERGE_HEAD').exists() and git('rev-parse', 'HEAD').stdout.strip() == main)
    hook.unlink(); cancel(); reset()
except BaseException as exc:
    failure = repr(exc)
    try: (out / 'failure-state.json').write_text(json.dumps({'state': tool('ui_state'), 'layout': layout(),
        'git': {'head': git('rev-parse', 'HEAD', ok=False).stdout, 'status': git('status', '--porcelain', ok=False).stdout,
                'unmerged': git('ls-files', '-u', ok=False).stdout, 'stashes': git('stash', 'list', ok=False).stdout}}, ensure_ascii=False, indent=2), encoding='utf-8')
    except BaseException: pass
    try: shot('failure')
    except BaseException: pass
    raise
finally:
    (out / 'history-merge-results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'failure': failure}, ensure_ascii=False, indent=2), encoding='utf-8')
    try:
        if pid: command('cancel_popup')
    except BaseException: pass
    p.stdin.close(); p.wait(timeout=15)
    if pid:
        script = "$p=Get-Process -Id " + str(pid) + " -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '" + str(exe).replace("'", "''") + "'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell', '-NoProfile', '-Command', script], capture_output=True, check=True)
    assert root.name.startswith('TreeGit merge flow QA ') and root.parent == Path(tempfile.gettempdir()).resolve()
    for item in (root, *root.rglob('*')): assert not item.is_symlink() and not item.is_junction()
    def readonly(function, path, error):
        owned = Path(path); assert owned.resolve().is_relative_to(root)
        owned.chmod(owned.stat().st_mode | stat.S_IWRITE); function(path)
    shutil.rmtree(root, onerror=readonly)
    prefs.write_text(json.dumps(default_prefs), encoding='utf-8')
