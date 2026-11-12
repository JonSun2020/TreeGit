"""Run the real startup path against a test-only HTTP handler in an owned CI app."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
from qa_cleanup import remove_fixture
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe, out = (Path(arg).resolve() for arg in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
profile = IsolatedProfile(exe, out)
root = Path(tempfile.mkdtemp(prefix='TreeGit startup updates ')).resolve()
checks = []

try:
    for name, enabled, response in [('enabled-after-checking-today', True, 'available'), ('disabled', False, 'available'), ('offline', True, 'offline')]:
        profile.reset()
        (exe.parent / 'data/preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Blue',
            'Check4UpdatesOnStartup': enabled, 'LastCheckUpdateTime': time.time(), 'EnableAutoFetch': False}), encoding='utf-8')
        payload = root / (name + '.json')
        payload.write_text('offline' if response == 'offline' else json.dumps({'tag_name': 'treegit-v2026.999.0', 'body': 'CI startup update', 'assets': [], 'draft': False, 'prerelease': False}), encoding='utf-8')
        env = {**os.environ, 'TREEGIT_TEST_UPDATE_RESPONSE_FILE': str(payload),
               'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
        process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=(out / (name + '.log')).open('w'),
                                   text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        messages = queue.Queue()
        threading.Thread(target=lambda: [messages.put(line) for line in process.stdout], daemon=True).start()
        seq = 0
        pid = None

        def rpc(method, params=None):
            global seq
            seq += 1
            process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': seq, 'method': method, 'params': params or {}}) + '\n'); process.stdin.flush()
            result = json.loads(messages.get(timeout=45)); assert 'error' not in result, result
            return result['result']

        def tool(tool_name, **args):
            result = rpc('tools/call', {'name': tool_name, 'arguments': args}); assert not result.get('isError'), result
            return json.loads(result['content'][0]['text'])

        try:
            rpc('initialize', {'protocolVersion': '2025-11-25'})
            pid = tool('ui_state')['processId']
            requested = Path(str(payload) + '.requests')
            deadline = time.monotonic() + 15
            if enabled:
                while not requested.exists() and time.monotonic() < deadline: time.sleep(.1)
                assert requested.exists(), 'The checked startup preference never triggered a check'
            time.sleep(2)
            state = tool('ui_state')
            updates = [d for d in state['dialogs'] if d['type'] == 'SelfUpdate']
            if not enabled:
                assert not requested.exists() and not updates
                checks.append('disabled startup preference performs no request and opens no update window')
            elif response == 'available':
                while not updates and time.monotonic() < deadline:
                    time.sleep(.1); state = tool('ui_state'); updates = [d for d in state['dialogs'] if d['type'] == 'SelfUpdate']
                assert len(updates) == 1
                assert len(requested.read_text().splitlines()) == 1, 'Presenting a startup result redundantly checked again'
                assert tool('ui_update', action='state')['state'] == 'available'
                checks.append('enabled startup checks despite today\'s timestamp and presents one available update')
                checks.append('startup presentation reuses the fetched release without another request')
            else:
                assert not updates and state['activePage'] is not None
                checks.append('startup service failure leaves the usable application without an error dialog')
            print('PASS startup update ' + name, flush=True)
        finally:
            process.stdin.close()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=10)
            if pid: close_owned_gui(exe, pid)
finally:
    profile.close()
    remove_fixture(root, root.parent, 'TreeGit startup updates ')
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'fixtureRemoved': not root.exists()}, indent=2), encoding='utf-8')
