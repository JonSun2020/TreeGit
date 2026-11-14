"""Check menu toggling, header outlines and actual pointer input in owned Windows CI."""
import base64
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import traceback

from qa_process import close_owned_gui, process_sample
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, out = (Path(a).resolve() for a in sys.argv[1:3])
profile = IsolatedProfile(exe, out)
checks, states = [], []
failure = None
pid = None
process = None
theme = 'Blue'
sequence = 0
messages = queue.Queue()
user = ctypes.windll.user32
user.WindowFromPoint.argtypes = [wintypes.POINT]
user.WindowFromPoint.restype = wintypes.HWND
user.GetForegroundWindow.restype = wintypes.HWND
user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
user.GetAncestor.restype = wintypes.HWND
user.SetForegroundWindow.argtypes = [wintypes.HWND]


def tool(name, **arguments):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': 'tools/call',
        'params': {'name': name, 'arguments': arguments}}) + '\n')
    process.stdin.flush()
    response = json.loads(messages.get(timeout=45))
    assert response.get('id') == sequence and 'error' not in response, response
    result = response['result']
    assert not result.get('isError'), result
    return base64.b64decode(result['content'][0]['data']) if name == 'ui_screenshot' else json.loads(result['content'][0]['text'])


def action(mode, index=0):
    result = tool('ui_layout', action='test_menu', text=mode, value=index)
    states.append({'theme': theme, 'mode': mode, 'index': index, **result})
    return result


def check(name, condition):
    assert condition, theme + ': ' + name
    checks.append(theme + ': ' + name)


def owner(hwnd):
    target = wintypes.DWORD()
    user.GetWindowThreadProcessId(hwnd, ctypes.byref(target))
    return target.value


def move(header):
    point = wintypes.POINT(header['screenX'], header['screenY'])
    hwnd = user.WindowFromPoint(point)
    assert owner(hwnd) == pid, 'Pointer target is not the owned CI window'
    if owner(user.GetForegroundWindow()) != pid:
        user.SetForegroundWindow(user.GetAncestor(hwnd, 2))
        time.sleep(.12)
    assert owner(user.GetForegroundWindow()) == pid, 'Owned CI window lost foreground'
    assert user.SetCursorPos(point.x, point.y)
    time.sleep(.12)
    actual = wintypes.POINT()
    assert user.GetCursorPos(ctypes.byref(actual))
    assert (actual.x, actual.y) == (point.x, point.y), 'CI pointer target is outside the visible desktop'


def click():
    assert owner(user.GetForegroundWindow()) == pid
    user.mouse_event(2, 0, 0, 0, 0)
    time.sleep(.04)
    user.mouse_event(4, 0, 0, 0, 0)
    time.sleep(.12)


try:
    (profile.data / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': theme,
        'Check4UpdatesOnStartup': False, 'Workspaces': [{'Name': '菜单演示', 'IsActive': True,
        'Repositories': [], 'RestoreOnStartup': True}]}), encoding='utf-8')
    process = subprocess.Popen([str(exe), '--mcp'], env={**os.environ, 'TREEGIT_TEST_MENU_PROBE': '1'},
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=(out / 'stderr.log').open('w'),
        text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': 0, 'method': 'initialize',
        'params': {'protocolVersion': '2025-11-25'}}) + '\n'); process.stdin.flush()
    assert 'result' in json.loads(messages.get(timeout=45))
    pid = tool('ui_state')['processId']
    process_sample(exe, pid)
    tool('ui_window', width=1040, height=736, state='normal', zoom=1)
    for theme in ['Blue', 'Light', 'Dark']:
        tool('ui_window', theme=theme, state='normal')
        color = {'Blue': '#ff0078d7', 'Light': '#ffa7e1a7', 'Dark': '#ff6f9f76'}[theme]
        check('all header outlines follow the theme', all(h['outlineBrush'].lower() == color for h in action('inspect')['headers']))
        for index in range(6):
            bounds = action('reset')['headers'][index]['bounds']
            action('enter', index); action('press', index)
            state = action('release', index)
            check('routed first press opens ' + str(index), state['headers'][index]['open'])
            check('open header outline ' + str(index), state['headers'][index]['outlineVisible'])
            check('outline leaves header geometry unchanged ' + str(index), state['headers'][index]['bounds'] == bounds)
            start = len(state['events'])
            action('press', index); action('release', index)
            time.sleep(.55)
            state = action('inspect')
            check('second press closes without reopening ' + str(index), not state['menuOpen'] and
                not any(h['open'] for h in state['headers']) and not any(e['open'] for e in state['events'][start:]))
            check('closed header removes outline ' + str(index), not state['headers'][index]['outlineVisible'])
            check('closed geometry unchanged ' + str(index), state['headers'][index]['bounds'] == bounds)
            action('press', index); state = action('release', index)
            check('next press opens again ' + str(index), state['headers'][index]['open'])
            other = (index + 1) % 6
            state = action('enter', other)
            check('hover switches header ' + str(index), state['headers'][other]['open'] and not state['headers'][index]['open'])
            state = action('escape', other)
            check('Escape closes hover menu ' + str(index), not state['menuOpen'])
        action('reset'); action('enter', 0); action('press', 0); action('release', 0)
        (out / (theme + '-header.png')).write_bytes(tool('ui_screenshot'))
        state = action('right', 0)
        check('keyboard Right opens next header', state['headers'][1]['open'] and not state['headers'][0]['open'])
        check('keyboard Down selects command', action('down', 1)['headers'][1]['childSelected'] >= 0)
        check('Escape after keyboard navigation', not action('escape', 1)['menuOpen'])
        action('press', 0); action('release', 0)
        check('outside press closes menu', not action('outside')['menuOpen'])
        # Re-enable real hit testing and verify the same event sequence through
        # the OS, only after process ownership and foreground are established.
        # The initial large centered window can extend above a small CI desktop;
        # resizing alone does not relocate it. Maximize before native input.
        tool('ui_window', state='maximized')
        time.sleep(.25)
        state = action('native_reset')
        for index in range(6):
            state = action('native_reset')
            move(state['headers'][index])
            click()
            state = action('native_inspect')
            check('native click opens header ' + str(index), state['headers'][index]['open'] and state['headers'][index]['outlineVisible'])
            before = len(state['events'])
            click(); time.sleep(.55)
            state = action('native_inspect')
            transitions = [e['open'] for e in state['events'][before:]]
            check('native second click closes once without flashing ' + str(index), not state['menuOpen'] and transitions == [False])
            check('native close removes outline ' + str(index), not state['headers'][index]['outlineVisible'])
            click()
            state = action('native_inspect')
            check('native third click reopens ' + str(index), state['headers'][index]['open'])
            other = (index + 1) % 6
            move(state['headers'][other])
            state = action('native_inspect')
            check('native hover switches menu ' + str(index), state['headers'][other]['open'] and not state['headers'][index]['open'])
    action('native_reset')
except BaseException:
    failure = traceback.format_exc()
    raise
finally:
    if pid:
        close_owned_gui(exe, pid)
    if process:
        process.stdin.close(); process.wait(timeout=15)
    profile.close()
    result = {'passed': len(checks), 'checks': checks, 'failure': failure,
        'source': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
        'ownedProcessExited': True, 'profileRestored': profile.closed, 'states': states}
    (out / 'results.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'passed': len(checks), 'failure': failure, 'ownedProcessExited': True}))
