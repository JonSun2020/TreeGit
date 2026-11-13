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
from parent_collision import create_fixture

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
class CursorInfo(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('flags', wintypes.DWORD), ('cursor', wintypes.HANDLE), ('point', wintypes.POINT)]
api.GetCursorInfo.argtypes = [ctypes.POINTER(CursorInfo)]
api.LoadCursorW.argtypes = [wintypes.HINSTANCE, ctypes.c_void_p]; api.LoadCursorW.restype = wintypes.HANDLE
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


def native_cursor():
    cursor = CursorInfo(ctypes.sizeof(CursorInfo)); assert api.GetCursorInfo(ctypes.byref(cursor))
    return {'handle': cursor.cursor, 'flags': cursor.flags, 'x': cursor.point.x, 'y': cursor.point.y,
            'hand': api.LoadCursorW(None, ctypes.c_void_p(32649))}


def substring(text, start, end):
    return text.encode('utf-16-le')[2 * min(start, end):2 * max(start, end)].decode('utf-16-le')


def drag(start, end, finish=None, return_to_start=False):
    origin = wintypes.POINT(round(start['x']), round(start['y']))
    target = wintypes.POINT(round(end['x']), round(end['y']))
    hwnd = api.WindowFromPoint(origin)
    assert owner(hwnd) == pid and owner(api.WindowFromPoint(target)) == pid
    api.SetForegroundWindow(api.GetAncestor(hwnd, 2))
    api.SetCursorPos(origin.x, origin.y); time.sleep(.1)
    api.mouse_event(2, 0, 0, 0, 0)
    try:
        for step in range(1, 13):
            api.SetCursorPos(round(origin.x + (target.x - origin.x) * step / 12), round(origin.y + (target.y - origin.y) * step / 12))
            time.sleep(.035)
        if finish: until(finish)
        if return_to_start:
            for step in range(1, 13):
                api.SetCursorPos(round(target.x + (origin.x - target.x) * step / 12), round(target.y + (origin.y - target.y) * step / 12))
                time.sleep(.025)
    finally: api.mouse_event(4, 0, 0, 0, 0)
    time.sleep(.15)


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
        git('merge', '--no-ff', 'other', '-m', '可复制的合并提交\n\n完整正文 / UTF-8 🙂')
        env.pop('GIT_AUTHOR_NAME'); env.pop('GIT_AUTHOR_EMAIL')
        head = git('rev-parse', 'HEAD')
        expected_parents = {sha: git('rev-parse', '--short=10', sha) for sha in git('show', '-s', '--format=%P', head).split()}
        collision = create_fixture(repo, object_format, env)
        (out / (object_format + '-collision.json')).write_text(json.dumps(collision, indent=2), encoding='utf-8')
        collision_short = git('rev-parse', '--short=10', collision['first'])
        check(object_format + ': the valid collision fixture needs more than ten characters', len(collision_short) > 10)
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
        def document(): return next(iter(layout()['metadataDocuments']), None)
        def span(name, sha=None): return next(v for v in document()['spans'] if v['name'] == name and (sha is None or v['sha'] == sha))

        def point(index, reveal=False):
            if reveal: tool('ui_layout', action='test_metadata', value=index, text='reveal')
            previous = None; stable = 0
            for _ in range(100):
                result = tool('ui_layout', action='test_metadata', value=index)
                assert result['hit'] == index, ('No physical caret hit', result)
                stable = stable + 1 if result['visible'] and result['screen'] == previous else 0
                if stable >= 3: return result['screen']
                previous = result['screen']; time.sleep(.05)
            raise AssertionError('Metadata caret did not become visible: ' + str(index))

        def copy_menu(index, item):
            pointer(point(index, True), right=True)
            previous = None; stable = 0
            for _ in range(100):
                target = next((v for v in (document()['menu'] or []) if v['name'] == item and v['enabled']), None)
                screen = target['screen'] if target else None
                stable = stable + 1 if screen and screen == previous else 0
                if stable >= 3: break
                previous = screen; time.sleep(.05)
            assert stable >= 3, 'Metadata context menu did not settle'
            clear_clipboard(); pointer(screen)
            until(lambda: document()['menu'] is None)

        def select(start, end, label):
            point(start, True); point(end, True)
            expected = substring(document()['text'], start, end)
            first = point(start); last = point(end)
            clear_clipboard(); drag(first, last)
            if document()['selected'] != expected:
                (out / (object_format + '-drag-failure.json')).write_text(json.dumps({'expected': expected, 'start': start, 'end': end, 'first': first, 'last': last,
                    'after': tool('ui_layout', action='test_metadata', value=end), 'document': document()}, ensure_ascii=False, indent=2), encoding='utf-8')
            check(object_format + ': ' + label + ' selects the exact text', document()['selected'] == expected and state()['detailCommit'] == head)
            check(object_format + ': ' + label + ' keeps text geometry stable while selecting', tool('ui_layout', action='test_metadata', value=end)['screen'] == last)
            key(0x43, ctrl=True); until(lambda: clipboard() == expected)
            check(object_format + ': ' + label + ' copies the actual selected range', clipboard() == expected)

        def ready(revision, expected):
            until(lambda: state()['detailCommit'] == revision and not state()['detailBranchesLoading'])
            until(lambda: (d := document()) and all(any(v['name'] == 'ParentSHA' and v['sha'] == sha and v['text'] == short for v in d['spans']) for sha, short in expected.items()))

        try:
            rpc('initialize', {'protocolVersion': '2025-11-25'}); pid = tool('ui_state')['processId']
            tool('ui_open_repository', repo=str(repo)); tool('ui_window', state='maximized', zoom=1)
            tool('ui_select_commit', repo=str(repo), revision=head); ready(head, expected_parents)
            until(lambda: '完整正文' in document()['text'])
            initial = document()['text']
            check(object_format + ': one selectable document contains every label value and full message',
                  {'CommitLabel', 'ParentLabel', 'AuthorLabel', 'DateLabel', 'CommitterLabel', 'CommitSHA', 'ParentSHA', 'Author', 'Date', 'Committer', 'Message'} <= {v['name'] for v in document()['spans']} and '完整正文 / UTF-8 🙂' in initial)
            check(object_format + ': complete commit SHA formatting is unchanged', span('CommitSHA')['text'] == head + ' [' + head[:10] + ']')
            check(object_format + ': parent labels and unique abbreviations are correct', span('ParentLabel')['text'] == '父级：' and span('CommitterLabel')['text'] == '提交者：' and all(span('ParentSHA', sha)['text'] == short for sha, short in expected_parents.items()))
            check(object_format + ': distinct author and committer identities remain correct', span('Author')['text'] == git('show', '-s', '--format=%an <%ae>', head) and span('Committer')['text'] == git('show', '-s', '--format=%cn <%ce>', head))
            pointer(point(1, True)); key(0x41, ctrl=True); clear_clipboard(); key(0x43, ctrl=True)
            until(lambda: clipboard() == initial)
            check(object_format + ': Ctrl+A and Ctrl+C include labels values newlines and message', document()['selected'] == initial and clipboard() == initial)
            copy_menu(1, 'CopyMetadataSelection'); until(lambda: clipboard() == initial)
            check(object_format + ': context copy retains a selection spanning the whole document', clipboard() == initial)
            copy_menu(1, 'CopyMetadataAll'); until(lambda: clipboard() == initial)
            check(object_format + ': copy-all includes all labels and full message', clipboard() == initial)

            for name in ['CommitLabel', 'ParentLabel', 'AuthorLabel', 'DateLabel', 'CommitterLabel']:
                value = span(name); select(value['start'], value['start'] + value['length'], name)
                copy_menu(value['start'] + 1, 'CopyMetadataSelection'); until(lambda: clipboard() == value['text'])
                check(object_format + ': reopening the context menu copies the current ' + name, clipboard() == value['text'])
            for parent_sha in expected_parents:
                parent = span('ParentSHA', parent_sha); label = span('ParentLabel')
                select(label['start'], parent['start'] + parent['length'], 'label through parent SHA')
                select(parent['start'] + parent['length'], label['start'], 'parent SHA back through its label')
                select(parent['start'] + 1, parent['start'] + 8, 'forward partial parent drag')
                select(parent['start'] + 8, parent['start'] + 1, 'reverse partial parent drag')
                select(parent['start'] + 2, parent['start'] + 3, 'one-character parent drag')
                start = point(parent['start'] + 1, True); end = point(parent['start'] + 8)
                drag(start, end, return_to_start=True)
                check(object_format + ': an out-and-back parent drag never becomes navigation', state()['detailCommit'] == head)
                copy_menu(parent['start'] + 2, 'CopyMetadataSHA'); until(lambda: clipboard() == parent_sha)
                check(object_format + ': parent full-SHA copying uses the complete ID', clipboard() == parent_sha)
            author = span('AuthorLabel'); committer = span('Committer')
            select(author['start'], committer['start'] + committer['length'], 'cross-row author date and committer drag')
            select(committer['start'] + committer['length'], author['start'], 'reverse cross-row drag')
            copy_menu(span('CommitSHA')['start'] + 1, 'CopyMetadataSHA'); until(lambda: clipboard() == head)
            check(object_format + ': full commit SHA copy excludes the suffix', clipboard() == head)

            pointer(point(1, True)); key(0x41, ctrl=True)
            version = state()['workingCopyRefreshVersion']; tool('ui_refresh', repo=str(repo))
            until(lambda: state()['workingCopyRefreshVersion'] > version and not state()['graphLoading'])
            check(object_format + ': background refresh preserves the whole-pane selection', document()['selected'] == initial)
            for theme in ['Dark', 'Blue']:
                tool('ui_window', theme=theme)
                check(object_format + ': ' + theme + ' preserves selected rich text', document()['selected'] == initial)
            point(0, True)
            viewport = document()['viewport']; scale = layout()['window']['scaling']
            edge = {'x': viewport['screen']['x'] + viewport['bounds']['width'] * scale / 2 - 24 * scale,
                    'y': viewport['screen']['y'] + viewport['bounds']['height'] * scale / 2 + 8 * scale}
            drag(point(0), edge, finish=lambda: document()['selected'] == initial)
            check(object_format + ': dragging to the lower edge scrolls and selects the entire message', document()['selected'] == initial and state()['detailCommit'] == head)
            clear_clipboard(); key(0x43, ctrl=True); until(lambda: clipboard() == initial)
            check(object_format + ': the complete mouse selection reaches the system clipboard', clipboard() == initial)
            (out / (object_format + '-whole-pane-selection.png')).write_bytes(tool('ui_screenshot'))
            tool('ui_window', zoom=1.5)
            parent = span('ParentSHA', next(iter(expected_parents)))
            select(parent['start'] + 1, parent['start'] + 8, 'zoomed parent drag')
            tool('ui_window', zoom=1)

            for parent_sha in expected_parents:
                tool('ui_select_commit', repo=str(repo), revision=head); ready(head, expected_parents)
                parent = span('ParentSHA', parent_sha); index = parent['start'] + 3
                hover = point(index, True); api.SetCursorPos(round(hover['x']), round(hover['y']))
                until(lambda: document()['tooltipOpen'] and document()['tooltip'] == parent_sha and document()['cursor'] == 'Hand')
                cursor = native_cursor(); (out / (object_format + '-cursor.json')).write_text(json.dumps(cursor, indent=2), encoding='utf-8')
                check(object_format + ': parent hover exposes the complete SHA and Hand cursor', document()['tooltip'] == parent_sha)
                pointer(hover); until(lambda: state()['detailCommit'] == parent_sha)
                check(object_format + ': a plain parent click still navigates', state()['detailCommit'] == parent_sha)
            tool('ui_select_commit', repo=str(repo), revision=collision['child']); ready(collision['child'], {collision['first']: collision_short})
            parent = span('ParentSHA', collision['first'])
            check(object_format + ': unreachable-object collisions still extend the parent prefix', parent['text'] == collision_short and len(parent['text']) > 10)
            pointer(point(parent['start'] + 2, True)); until(lambda: state()['detailCommit'] == collision['first'])
            check(object_format + ': extended parent links navigate with the full ID', state()['detailCommit'] == collision['first'])
            check(object_format + ': selection and navigation preserve Git HEAD index and worktree', before == (git('rev-parse', 'HEAD'), git('status', '--porcelain'), (repo / '.git/index').read_bytes()))

        except BaseException:
            evidence = {'nativeCursor': native_cursor()}
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
