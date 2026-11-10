"""Exercise the real Avalonia UI in an isolated Linux CI display and repository."""
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import subprocess
import sys
import threading
import time

assert os.name == 'posix' and os.environ.get('CI') == 'true', 'Run only in the isolated cloud CI environment'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
data = exe.parent / 'data'
assert data.is_dir() and exe.is_file()
repo = out / 'repository'
assert not repo.exists(), 'Use a fresh fixture directory'
repo.mkdir()
checks = []
client = None
environment = os.environ.copy()
environment.update(APPIMAGE=str(exe), GIT_CONFIG_GLOBAL=str(out / 'global.gitconfig'),
                   GIT_CONFIG_SYSTEM=str(out / 'system.gitconfig'))
environment.pop('GIT_CONFIG_COUNT', None)
environment.pop('GIT_CONFIG_NOSYSTEM', None)
(out / 'global.gitconfig').write_text('# cloud UI fixture\n[user]\nname = UI QA\nemail = ui@example.invalid\n', encoding='utf-8')
(out / 'system.gitconfig').write_text('', encoding='utf-8')


def git(*args):
    return subprocess.check_output(['git', '-C', str(repo), *args], env=environment, stderr=subprocess.STDOUT).decode().strip()


def check(name, value):
    assert value, name
    checks.append(name)
    print('PASS', name, flush=True)


def until(predicate, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.2)
    raise AssertionError('UI did not reach the expected state')


class Client:
    def __init__(self):
        self.error = (out / 'mcp-stderr.log').open('w')
        self.gui_process = subprocess.Popen([str(exe)], stdin=subprocess.DEVNULL, stdout=self.error,
                                            stderr=self.error, env=environment)
        self.gui_pid = self.gui_process.pid
        self.process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.error, text=True, encoding='utf-8', env=environment)
        self.queue = queue.Queue()
        threading.Thread(target=lambda: [self.queue.put(line) for line in self.process.stdout], daemon=True).start()
        self.sequence = 0
        try:
            self.rpc('initialize', {'protocolVersion': '2025-11-25'})
            time.sleep(1)
            state = self.tool('ui_state')
            check('only the owned integration app receives UI events', state['processId'] == self.gui_pid and Path(f'/proc/{self.gui_pid}/exe').resolve() == exe)
        except Exception:
            self.close()
            raise

    def rpc(self, method, params):
        self.sequence += 1
        self.process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': self.sequence, 'method': method, 'params': params}) + '\n')
        self.process.stdin.flush()
        response = json.loads(self.queue.get(timeout=60))
        assert 'error' not in response, response
        return response['result']

    def tool(self, name, args=None):
        result = self.rpc('tools/call', {'name': name, 'arguments': args or {}})
        assert not result.get('isError'), result
        if name == 'ui_screenshot':
            return base64.b64decode(result['content'][0]['data'])
        return json.loads(result['content'][0]['text'])

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            self.process.wait(timeout=15)
        if self.gui_pid and Path(f'/proc/{self.gui_pid}/exe').exists():
            assert Path(f'/proc/{self.gui_pid}/exe').resolve() == exe
            os.kill(self.gui_pid, signal.SIGTERM)
            self.gui_process.wait(timeout=15)
        self.error.close()


def command(name, **kwargs):
    return client.tool('ui_command', {'command': name, 'repo': str(repo), **kwargs})


def layout(active=False):
    return client.tool('ui_layout', {'window': 'active' if active else 'main'})


def click(name, active=False):
    return client.tool('ui_layout', {'action': 'test_click', 'button': name, 'window': 'active' if active else 'main'})


def set_control(name, value=None, text=None, active=False):
    args = {'action': 'test_set', 'button': name, 'window': 'active' if active else 'main'}
    if text is not None:
        args['text'] = text
    else:
        args['value'] = value
    return client.tool('ui_layout', args)


def labels(active=False):
    return '\n'.join(item['text'] for item in layout(active)['labels'])


def capture(name, active=False):
    (out / (name + '.png')).write_bytes(client.tool('ui_screenshot', {'window': 'active' if active else 'main'}))


def preferences_open(page='appearance'):
    command('preferences', page=page)
    until(lambda: any(item['type'] == 'Preferences' for item in client.tool('ui_state')['dialogs']))


def preferences_closed():
    until(lambda: not any(item['type'] == 'Preferences' for item in client.tool('ui_state')['dialogs']))


def preference_json():
    return json.loads((data / 'preference.json').read_text(encoding='utf-8-sig'))


try:
    git('init', '-b', 'main')
    (repo / 'src').mkdir()
    for name in ['file2.txt', 'file10.txt', 'obsolete.txt']:
        (repo / 'src' / name).write_text(name + '\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', '初始版本')
    git('switch', '-c', 'feature/ui')
    (repo / 'src/file2.txt').write_text('feature change\n', encoding='utf-8')
    git('commit', '-am', '分支修改')
    git('switch', 'main')
    (repo / 'src/file10.txt').write_text('main change\n', encoding='utf-8')
    git('commit', '-am', '本地修改')
    git('merge', '--no-ff', 'feature/ui', '-m', '合并验证\n\n完整正文验证：应在默认详情中显示。\n第二段：父提交应可点击，文件列表仍可使用。')
    revision = git('rev-parse', 'HEAD')
    remote = out / 'remote.git'
    subprocess.run(['git', 'init', '--bare', str(remote)], check=True, capture_output=True, env=environment)
    git('remote', 'add', 'origin', str(remote)); git('push', '-u', 'origin', 'main')
    (repo / 'src/file2.txt').write_text('unstaged change\n', encoding='utf-8')
    (repo / 'src/file10.txt').write_text('staged change\n', encoding='utf-8')
    (repo / 'src/added.txt').write_text('hidden staged addition\n', encoding='utf-8')
    (repo / 'src/new.txt').write_text('untracked\n', encoding='utf-8')
    (repo / 'src/obsolete.txt').unlink()
    git('add', 'src/file10.txt', 'src/added.txt')
    (data / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Blue', 'Zoom': 1,
        'DefaultFontFamily': '', 'MonospaceFontFamily': '', 'GitInstallPath': shutil.which('git'),
        'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False, 'Workspaces': []}), encoding='utf-8')
    client = Client()
    client.tool('ui_window', {'width': 1280, 'height': 800, 'state': 'normal'})
    client.tool('ui_open_repository', {'repo': str(repo)})
    until(lambda: len(client.tool('ui_working_copy', {'repo': str(repo)})['unstaged']) == 3)
    visible = client.tool('ui_working_copy', {'repo': str(repo), 'status': 'modified', 'sort': 'name'})
    check('status filtering reaches the real working-copy view', visible['unstaged'] == ['src/file2.txt'] and visible['staged'] == ['src/file10.txt'])
    capture('working-copy-filter')
    click('StageAllChanges')
    until(lambda: set(git('diff', '--cached', '--name-only').splitlines()) == {'src/file2.txt', 'src/file10.txt', 'src/added.txt'})
    check('stage all does not stage hidden untracked or deleted files', git('ls-files', '--error-unmatch', 'src/obsolete.txt') == 'src/obsolete.txt' and (repo / 'src/new.txt').exists())
    client.tool('ui_refresh', {'repo': str(repo)})
    until(lambda: client.tool('ui_working_copy', {'repo': str(repo)})['stagedCount'] == 2)
    click('UnstageAllChanges')
    until(lambda: git('diff', '--cached', '--name-only') == 'src/added.txt')
    check('unstage all preserves hidden staged additions', git('diff', '--cached', '--name-only') == 'src/added.txt')
    client.tool('ui_working_copy', {'repo': str(repo), 'status': 'all', 'sort': 'status'})
    client.tool('ui_diff', {'repo': str(repo), 'action': 'select', 'source': 'unstaged', 'file': 'src/file2.txt'})
    until(lambda: '暂存区' in labels() and '工作区' in labels())
    check('text diff displays the actual source pair', '暂存区' in labels() and '工作区' in labels())
    capture('text-diff-sources')
    client.tool('ui_select_view', {'repo': str(repo), 'view': 'history'})
    client.tool('ui_select_commit', {'repo': str(repo), 'revision': revision})
    command('commit_changes')
    until(lambda: '完整正文验证' in labels())
    check('default commit detail includes the full message', '完整正文验证' in labels())
    time.sleep(.5)  # Allow the text change to complete its normal layout pass.
    (out / 'complete-commit-layout.json').write_text(json.dumps(layout(), ensure_ascii=False), encoding='utf-8')
    capture('complete-commit-detail')
    message = next(t for t in layout()['labels'] if '完整正文验证' in t['text'])
    check('commit message is visible without scrolling metadata', message['visibleHeight'] >= message['bounds']['height'] * .9)
    check('history diff clearly identifies the parent commit', '父提交' in labels())
    capture('complete-commit-detail')
    before_popup = git('status', '--porcelain=v1'), git('show-ref')
    for action in ['create_branch', 'pull_dialog', 'push_dialog', 'stash_dialog', 'reset_dialog']:
        command(action)
        until(lambda: any(b['name'] == 'ConfirmPopup' for b in layout()['buttons']))
        check(action + ' has a fixed footer inside the window', all(0 <= b['bounds']['y'] and b['bounds']['y'] + b['bounds']['height'] <= 800 for b in layout()['buttons'] if b['name'] in ['ConfirmPopup', 'CancelPopup']))
        if action == 'create_branch':
            set_control('BranchAdvancedOptions', 1)
            set_control('BranchAllowOverwrite', 1)
            set_control('BranchAdvancedOptions', 0)
            until(lambda: '允许将已有分支重置' in labels())
            check('collapsed branch overwrite option still displays its warning', '允许将已有分支重置' in labels())
        if action == 'push_dialog':
            set_control('PushAdvancedOptions', 1)
            set_control('PushForce', 1)
            set_control('PushSkipHooks', 1)
            set_control('PushAdvancedOptions', 0)
            until(lambda: '已开启强制推送' in labels() and '跳过' in labels())
            check('collapsed advanced push options still display active warnings', '已开启强制推送' in labels())
        capture(action)
        command('cancel_popup')
    check('opening and cancelling dialogs does not modify Git', before_popup == (git('status', '--porcelain=v1'), git('show-ref')))
    preferences_open()
    client.tool('ui_window', {'theme': 'Dark', 'zoom': 1.25})
    fonts = client.tool('ui_fonts', {'action': 'list', 'kind': 'interface'})['fonts']
    family = next(f.get('family', f.get('Family')) for f in fonts if f.get('family', f.get('Family')))
    client.tool('ui_fonts', {'action': 'set', 'kind': 'interface', 'font': family})
    check('background save does not persist theme or font previews', preference_json()['Theme'] == 'Blue' and preference_json()['Zoom'] == 1 and not preference_json()['DefaultFontFamily'])
    capture('preferences-preview', active=True)
    click('CancelPreferencesButton', active=True); preferences_closed()
    check('Cancel restores the theme and fonts', client.tool('ui_window')['theme'] == 'Blue' and not client.tool('ui_fonts', {'action': 'state'})['interfaceFont'])
    preferences_open(); client.tool('ui_window', {'theme': 'Dark'})
    command('close_dialog'); preferences_closed()
    check('closing preferences also cancels its preview', client.tool('ui_window')['theme'] == 'Blue')
    preferences_open()
    client.tool('ui_window', {'theme': 'Dark'})
    client.tool('ui_fonts', {'action': 'set', 'kind': 'interface', 'font': family})
    result = command('save_preferences'); preferences_closed()
    check('MCP can explicitly save previewed preferences', result['saved'] and preference_json()['Theme'] == 'Dark' and preference_json()['DefaultFontFamily'] == family)
    preferences_open('git')
    before_global = (out / 'global.gitconfig').read_bytes()
    set_control('DefaultGitUserBox', text='界面保存验证', active=True)
    click('CancelPreferencesButton', active=True); preferences_closed()
    check('Cancel never writes Git identity settings', (out / 'global.gitconfig').read_bytes() == before_global)
    preferences_open('git')
    set_control('DefaultGitUserBox', text='界面保存验证', active=True)
    client.tool('ui_window', {'theme': 'Dark'})
    click('SavePreferencesButton', active=True); preferences_closed()
    check('Save persists the theme and Git identity together', preference_json()['Theme'] == 'Dark' and git('config', '--global', 'user.name') == '界面保存验证')
    preferences_open('git')
    before_global = (out / 'global.gitconfig').read_bytes()
    set_control('DefaultGitUserBox', text='被锁定时保留的输入', active=True)
    locked = out / 'global.gitconfig.lock'; locked.write_text('another operation', encoding='utf-8')
    result = command('save_preferences')
    check('MCP reports a locked Git configuration without closing the draft', not result['saved'] and '占用' in result['message'])
    check('failed MCP save preserves the configuration and another operation lock', (out / 'global.gitconfig').read_bytes() == before_global and locked.read_text(encoding='utf-8') == 'another operation')
    locked.unlink(); click('CancelPreferencesButton', active=True); preferences_closed()
    preferences_open()
    client.tool('ui_window', {'theme': 'Light'})
    blocked = data / 'preference_tmp.json'
    assert not blocked.exists()
    blocked.mkdir()
    click('SavePreferencesButton', active=True)
    until(lambda: '设置未能保存' in labels(active=True))
    check('save failure keeps the dialog and draft available', any(b['name'] == 'CancelPreferencesButton' and b['enabled'] for b in layout(active=True)['buttons']))
    capture('preferences-save-failure', active=True)
    blocked.rmdir()
    click('CancelPreferencesButton', active=True); preferences_closed()
    check('failed save can be cancelled without losing previous settings', preference_json()['Theme'] == 'Dark' and client.tool('ui_window')['theme'] == 'Dark')
    command('repository_settings')
    command('repository_settings_advanced')
    until(lambda: '同步与合并' in labels(active=True))
    check('advanced repository settings retain a reachable sync group', '同步与合并' in labels(active=True))
    active_layout = layout(active=True)
    confirm = next(b for b in active_layout['buttons'] if b['name'] == 'ConfirmRepositorySettings')
    check('repository-settings footer remains inside the dialog', 0 <= confirm['bounds']['y'] and confirm['bounds']['y'] + confirm['bounds']['height'] <= active_layout['window']['height'])
    capture('repository-advanced', active=True)
    command('close_dialog')
    client.tool('ui_window', {'width': 1024, 'height': 640, 'zoom': 1.5})
    command('push_dialog')
    until(lambda: any(b['name'] == 'ConfirmPopup' for b in layout()['buttons']))
    check('small-window popup retains accessible confirmation and cancel buttons', all(0 <= b['bounds']['y'] and b['bounds']['y'] + b['bounds']['height'] <= 640 for b in layout()['buttons'] if b['name'] in ['ConfirmPopup', 'CancelPopup']))
    capture('small-popup')
    command('cancel_popup')
    git('config', '--global', 'gpg.program', 'OpenPGP QA program')
    preferences_open('signing')
    set_control('SigningFormatBox', 2, active=True)
    time.sleep(.25)
    result = command('save_preferences'); preferences_closed()
    program = subprocess.run(['git', 'config', '--global', '--get', 'gpg.ssh.program'], env=environment, capture_output=True)
    check('changing signing format does not copy an incompatible program', result['saved'] and git('config', '--global', 'gpg.format') == 'ssh' and program.returncode == 1 and git('config', '--global', 'gpg.program') == 'OpenPGP QA program')
    client.tool('ui_window', {'zoom': 1})
    for value in [1, 0]:
        preferences_open()
        set_control('UseSystemFrameOption', value, active=True)
        result = command('save_preferences')
        check('MCP save releases its operation gate before restart confirmation ' + str(value), result['saved'] and result['restartRequired'] and any(d['type'] == 'ConfirmRestart' for d in client.tool('ui_state')['dialogs']))
        command('close_dialog'); preferences_closed()
    (out / 'ui-report.json').write_text(json.dumps({'passed': len(checks), 'checks': checks}, ensure_ascii=False, indent=2), encoding='utf-8')
except Exception as error:
    (out / 'ui-report.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'error': repr(error)}, ensure_ascii=False, indent=2), encoding='utf-8')
    raise
finally:
    if client:
        client.close()
    shutil.rmtree(repo)
    if (out / 'remote.git').exists():
        shutil.rmtree(out / 'remote.git')
