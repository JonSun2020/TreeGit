"""Copy real metadata through native CI mouse/keyboard and the Windows clipboard."""
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
from qa_cleanup import remove_fixture
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, out = (Path(arg).resolve() for arg in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
profile = IsolatedProfile(exe, out)
root = Path(tempfile.mkdtemp(prefix='TreeGit metadata copy ')).resolve()
env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
checks = []
failure = None
api = ctypes.WinDLL('user32', use_last_error=True)
api.GetForegroundWindow.restype = wintypes.HWND
api.WindowFromPoint.argtypes = [wintypes.POINT]; api.WindowFromPoint.restype = wintypes.HWND
api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
api.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]; api.GetAncestor.restype = wintypes.HWND
api.SetForegroundWindow.argtypes = [wintypes.HWND]
api.OpenClipboard.argtypes = [wintypes.HWND]; api.GetClipboardData.argtypes = [wintypes.UINT]; api.GetClipboardData.restype = wintypes.HANDLE
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.GlobalLock.argtypes = [wintypes.HANDLE]; kernel.GlobalLock.restype = ctypes.c_void_p
kernel.GlobalUnlock.argtypes = [wintypes.HANDLE]


def check(name, value):
    assert value, name
    checks.append(name); print('PASS ' + name, flush=True)


def until(fn):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        value = fn()
        if value: return value
        time.sleep(.08)
    raise AssertionError('Metadata UI did not settle')


def owner(hwnd):
    value = wintypes.DWORD(); api.GetWindowThreadProcessId(hwnd, ctypes.byref(value)); return value.value


def key(vk, ctrl=False):
    assert owner(api.GetForegroundWindow()) == pid
    if ctrl: api.keybd_event(0x11, 0, 0, 0)
    api.keybd_event(vk, 0, 0, 0); api.keybd_event(vk, 0, 2, 0)
    if ctrl: api.keybd_event(0x11, 0, 2, 0)
    time.sleep(.08)


def pointer(point, right=False):
    target = wintypes.POINT(round(point['x']), round(point['y']))
    hwnd = api.WindowFromPoint(target)
    assert owner(hwnd) == pid
    if owner(api.GetForegroundWindow()) != pid: api.SetForegroundWindow(api.GetAncestor(hwnd, 2))
    assert api.SetCursorPos(target.x, target.y); time.sleep(.1)
    api.mouse_event(8 if right else 2, 0, 0, 0, 0); time.sleep(.05); api.mouse_event(16 if right else 4, 0, 0, 0, 0)
    time.sleep(.1)


def clipboard():
    for _ in range(30):
        if api.OpenClipboard(None): break
        time.sleep(.03)
    else: raise AssertionError('CI clipboard unavailable')
    try:
        handle = api.GetClipboardData(13)
        if not handle: return None
        address = kernel.GlobalLock(handle)
        assert address
        try: return ctypes.wstring_at(address)
        finally: kernel.GlobalUnlock(handle)
    finally: api.CloseClipboard()


def clear_clipboard():
    until(lambda: api.OpenClipboard(None))
    try: assert api.EmptyClipboard()
    finally: api.CloseClipboard()


def settled_field(name, text):
    previous = None; stable = 0
    for _ in range(100):
        value = field(name, text)
        geometry = (value['bounds'], value['screen'])
        stable = stable + 1 if geometry == previous and value['visibleHeight'] >= min(12, value['bounds']['height']) else 0
        if stable >= 3: return value
        previous = geometry; time.sleep(.06)
    raise AssertionError('Metadata field did not settle: ' + name)


def copy_menu(name, text, item):
    pointer(settled_field(name, text)['screen'], right=True)
    previous = None; stable = 0
    for _ in range(100):
        menu = field(name, text)['menu']
        target = next((v for v in (menu or []) if v['name'] == item and v['enabled']), None)
        point = target['screen'] if target else None
        stable = stable + 1 if point and point == previous else 0
        if stable >= 3: break
        previous = point; time.sleep(.06)
    assert stable >= 3, 'Metadata menu did not settle: ' + item
    clear_clipboard(); pointer(target['screen'])
    until(lambda: field(name, text)['menu'] is None)


try:
    for object_format in ['sha1', 'sha256']:
        profile.reset()
        repo = root / object_format; repo.mkdir()

        def git(*args):
            result = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8', timeout=30)
            assert result.returncode == 0, result.stderr
            return result.stdout.strip()

        git('init', '-b', 'main', '--object-format=' + object_format)
        git('config', 'user.name', '提交者 QA'); git('config', 'user.email', 'committer@example.invalid')
        (repo / 'base.txt').write_text('base\n', encoding='utf-8'); git('add', '.'); git('commit', '-m', 'Base')
        base = git('rev-parse', 'HEAD'); git('checkout', '-b', 'other')
        (repo / 'side.txt').write_text('side\n', encoding='utf-8'); git('add', '.'); git('commit', '-m', 'Other')
        git('checkout', 'main')
        (repo / 'local.txt').write_text('local\n', encoding='utf-8'); git('add', '.'); git('commit', '-m', 'Local')
        env.update(GIT_AUTHOR_NAME='原始作者 QA', GIT_AUTHOR_EMAIL='author@example.invalid')
        git('merge', '--no-ff', 'other', '-m', '可复制的合并提交\n\n完整正文 / UTF-8')
        env.pop('GIT_AUTHOR_NAME'); env.pop('GIT_AUTHOR_EMAIL')
        head = git('rev-parse', 'HEAD')
        before = git('rev-parse', 'HEAD'), git('status', '--porcelain'), (repo / '.git/index').read_bytes()
        (exe.parent / 'data/preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Blue', 'Check4UpdatesOnStartup': False,
            'EnableAutoFetch': False, 'ShowChangesInCommitDetailByDefault': True}), encoding='utf-8')
        process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=(out / (object_format + '.log')).open('w'), text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        messages = queue.Queue(); threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
        seq = 0; pid = None

        def rpc(method, params=None):
            global seq
            seq += 1; process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': seq, 'method': method, 'params': params or {}}) + '\n'); process.stdin.flush()
            result = json.loads(messages.get(timeout=45)); assert 'error' not in result, result
            return result['result']

        def tool(name, **args):
            response = rpc('tools/call', {'name': name, 'arguments': args}); assert not response.get('isError'), response
            return base64.b64decode(response['content'][0]['data']) if name == 'ui_screenshot' else json.loads(response['content'][0]['text'])

        def state(): return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)
        def layout(): return tool('ui_layout')
        def field(name, text): return next(v for v in layout()['metadataFields'] if v['name'] == name and v['text'] == text)

        try:
            rpc('initialize', {'protocolVersion': '2025-11-25'}); pid = tool('ui_state')['processId']
            tool('ui_open_repository', repo=str(repo)); tool('ui_window', state='maximized', zoom=1)
            tool('ui_select_commit', repo=str(repo), revision=head)
            until(lambda: state()['detailCommit'] == head and not state()['detailBranchesLoading'])
            until(lambda: len(layout()['metadataFields']) >= 7 and any(v['name'] == 'CommitMetadataMessage' and '完整正文' in v['text'] for v in layout()['metadataFields']))
            fields = layout()['metadataFields']
            check(object_format + ': full commit hash and ten-character abbreviation have the exact required separator',
                  next(v['text'] for v in fields if v['name'] == 'CommitMetadataSHA') == head + ' [' + head[:10] + ']')
            check(object_format + ': all parent values retain complete hashes', len([v for v in fields if v['name'] == 'CommitMetadataParent']) == 2 and
                  all(len(v['text'].split()[0]) == len(head) for v in fields if v['name'] == 'CommitMetadataParent'))
            check(object_format + ': author and committer preserve their distinct identities',
                  next(v['text'] for v in fields if v['name'] == 'CommitMetadataAuthor') == git('show', '-s', '--format=%an <%ae>', head) and
                  next(v['text'] for v in fields if v['name'] == 'CommitMetadataCommitter') == git('show', '-s', '--format=%cn <%ce>', head))
            for row in fields:
                name, text = row['name'], row['text']
                tool('ui_layout', action='test_reveal', button=name, text=text)
                current = settled_field(name, text)
                clear_clipboard(); pointer(current['screen'])
                until(lambda: field(name, text)['focused'])
                key(0x41, ctrl=True)
                until(lambda: field(name, text)['selected'] == text)
                key(0x43, ctrl=True)
                until(lambda: clipboard() == text)
                check(object_format + ': native select-all and Ctrl+C copies ' + name, clipboard() == text)
                copy_menu(name, text, 'CopyMetadataAll')
                until(lambda: clipboard() == text)
                check(object_format + ': metadata right-click copies the entire ' + name, clipboard() == text)
                if name in ('CommitMetadataSHA', 'CommitMetadataParent'):
                    copy_menu(name, text, 'CopyMetadataSHA')
                    until(lambda: clipboard() == text.split()[0])
                    check(object_format + ': full SHA copy excludes the display abbreviation ' + name, clipboard() == text.split()[0])
                check(object_format + ': selecting metadata keeps the same commit ' + name, state()['detailCommit'] == head)
            sha_row = next(v for v in layout()['metadataFields'] if v['name'] == 'CommitMetadataSHA')
            tool('ui_layout', action='test_reveal', button=sha_row['name'], text=sha_row['text']); time.sleep(.15)
            sha_row = field(sha_row['name'], sha_row['text']); scale = layout()['window']['scaling']
            x = round(sha_row['screen']['x'] - sha_row['bounds']['width'] * scale / 2 + 2 * scale)
            y = round(sha_row['screen']['y'] - sha_row['bounds']['height'] * scale / 2 + 7 * scale)
            assert owner(api.WindowFromPoint(wintypes.POINT(x, y))) == pid
            api.SetCursorPos(x, y); time.sleep(.1); api.mouse_event(2, 0, 0, 0, 0)
            for step in range(1, 8): api.SetCursorPos(x + step * 8, y); time.sleep(.025)
            api.mouse_event(4, 0, 0, 0, 0); time.sleep(.1)
            selected = field(sha_row['name'], sha_row['text'])['selected']; key(0x43, ctrl=True)
            until(lambda: clipboard() == selected)
            check(object_format + ': dragging copies just the selected SHA fragment', 1 < len(selected) < len(head) and selected in head)
            (out / (object_format + '-selectable-metadata.png')).write_bytes(tool('ui_screenshot'))
            parent_button = layout()['metadataParentButtons'][0]
            tool('ui_layout', action='test_reveal', button='CommitMetadataNavigateParent')
            parent_button = next(v for v in layout()['metadataParentButtons'] if v['sha'] == parent_button['sha'])
            pointer(parent_button['screen']); until(lambda: state()['detailCommit'] == parent_button['sha'])
            check(object_format + ': the separate parent action still navigates', state()['detailCommit'] == parent_button['sha'])
            check(object_format + ': metadata operations preserve Git HEAD index and worktree', before == (git('rev-parse', 'HEAD'), git('status', '--porcelain'), (repo / '.git/index').read_bytes()))
        except BaseException:
            evidence = {}
            for label, read in [('state', lambda: tool('ui_state')), ('layout', layout)]:
                try: evidence[label] = read()
                except Exception as error: evidence[label] = {'error': str(error)}
            (out / (object_format + '-failure.json')).write_text(json.dumps(evidence, ensure_ascii=False), encoding='utf-8')
            try: (out / (object_format + '-failure.png')).write_bytes(tool('ui_screenshot'))
            except Exception: pass
            raise
        finally:
            process.stdin.close()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)
            if pid: close_owned_gui(exe, pid)
except BaseException:
    failure = traceback.format_exc()
    raise
finally:
    profile.close()
    remove_fixture(root, root.parent, 'TreeGit metadata copy ')
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'failure': failure, 'fixtureRemoved': not root.exists()}, ensure_ascii=False, indent=2), encoding='utf-8')
