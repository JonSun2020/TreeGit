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
        gui_environment = environment.copy()
        # Make an AI connection race a desktop that the user already opened.
        # The endpoint must wait for that process, not open a competing window.
        gui_environment['TREEGIT_TEST_DESKTOP_STARTUP_DELAY_MS'] = '3000'
        gui_environment['TREEGIT_TEST_POINTER_TRACE'] = str(out / 'pointer-events.jsonl')
        self.gui_process = subprocess.Popen([str(exe)], stdin=subprocess.DEVNULL, stdout=self.error,
                                            stderr=self.error, env=gui_environment)
        self.gui_pid = self.gui_process.pid
        self.process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.error, text=True, encoding='utf-8', env=environment)
        self.queue = queue.Queue()
        threading.Thread(target=lambda: [self.queue.put(line) for line in self.process.stdout], daemon=True).start()
        self.sequence = 0
        try:
            self.rpc('initialize', {'protocolVersion': '2025-11-25'})
            deadline = time.monotonic() + 20
            while True:
                assert self.gui_process.poll() is None, 'Owned GUI exited during startup'
                try:
                    state = self.tool('ui_state')
                    break
                except AssertionError:
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(.2)
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
    state = layout(active)
    return '\n'.join([item['text'] for item in state['labels']] + [item['text'] for item in state.get('metadataDocuments', [])])


def capture(name, active=False):
    if name == 'failure' or os.environ.get('TREEGIT_TEST_NATIVE_CAPTURE') == '1':
        from PIL import ImageGrab
        ImageGrab.grab(xdisplay=os.environ['DISPLAY']).save(out / (name + '-native.png'))
    if os.environ.get('TREEGIT_TEST_NATIVE_CAPTURE') == '1':
        return
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
    until(lambda: 'src/file2.txt' in client.tool('ui_working_copy', {'repo': str(repo)})['unstaged'])
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
    message = next(s for s in layout()['metadataDocuments'][0]['spans'] if s['name'] == 'Message')
    metadata_scroll = next(s for s in layout()['scrolls'] if s['name'] == 'CommitMetadataScroll')
    header_before = layout()['commitBranchHeaders'][0]['bounds']
    client.tool('ui_layout', {'action': 'scroll', 'scroller': metadata_scroll['index'], 'dy': metadata_scroll['extent']['height']})
    until(lambda: client.tool('ui_layout', {'action': 'test_metadata', 'value': message['start'] + message['length'] - 1})['visible'])
    check('the complete commit message remains readable in the metadata scroll area', True)
    check('reading a long message keeps the branch header fixed and visible', layout()['commitBranchHeaders'][0]['bounds'] == header_before)
    client.tool('ui_layout', {'action': 'scroll', 'scroller': metadata_scroll['index'], 'dy': 0})
    check('history diff clearly identifies the parent commit', '父级：' in labels())
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
    # Branch headers use the real graph, local creation evidence and actual containment.
    git('reset', '--hard', revision); git('clean', '-fdx')
    git('switch', 'feature/ui')
    middle = git('rev-parse', 'HEAD')
    (repo / 'src/later.txt').write_text('later branch change\n', encoding='utf-8')
    git('add', '.'); git('-c', 'commit.gpgsign=false', 'commit', '-m', 'Later branch commit')
    feature_tip = git('rev-parse', 'HEAD')
    git('switch', 'main')
    (repo / 'src/main-only.txt').write_text('only on main\n', encoding='utf-8')
    git('add', '.'); git('-c', 'commit.gpgsign=false', 'commit', '-m', 'Only main contains this commit')
    unique = git('rev-parse', 'HEAD')
    client.tool('ui_window', {'width': 1280, 'height': 800, 'zoom': 1, 'theme': 'Blue'})
    def repository_state():
        return next(r for r in client.tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)
    def refresh_header_repository():
        client.tool('ui_refresh', {'repo': str(repo)})
        until(lambda: not repository_state()['graphLoading'] and any(b['head'] == feature_tip for b in repository_state()['loadedBranches']))
    def select_header(sha):
        client.tool('ui_select_view', {'repo': str(repo), 'view': 'history'})
        client.tool('ui_select_commit', {'repo': str(repo), 'revision': sha})
        command('commit_changes')
        def ready():
            state = repository_state()
            return state['detailBranches'] if state['detailCommit'] == sha and not state['detailBranchesLoading'] else None
        return until(ready)
    refresh_header_repository()
    info = select_header(middle)
    check('an intermediate commit shows its recorded local branch', info['primary'] == 'refs/heads/feature/ui' and not info['inferred'])
    check('branch badge counts other refs rather than the total', info['extraCount'] == 2 and info['total'] == 3)
    more = next(b for b in layout()['buttons'] if b['name'] == 'MoreCommitBranches')
    check('multiple containing branches enable the actual ellipsis button', more['enabled'] and not more['outsideParentBorder'])
    time.sleep(.25); capture('commit-branch-header')
    def header_ready():
        current = layout()
        return current if any(b['name'] == 'MoreCommitBranches' and b['enabled'] for b in current['buttons']) else None
    header_layout = until(header_ready)
    (out / 'branch-header-layout.json').write_text(json.dumps(header_layout, ensure_ascii=False), encoding='utf-8')
    header = header_layout['commitBranchHeaders'][0]
    more = next(b for b in header_layout['buttons'] if b['name'] == 'MoreCommitBranches')
    changes_pane = next(p for p in header_layout['panes'] if p['type'] == 'CommitChanges')['bounds']
    diff_pane = next(p for p in header_layout['panes'] if p['type'] == 'DiffView')['bounds']
    check('branch header belongs to the lower commit metadata area', header['bounds']['y'] >= changes_pane['y'] and more['bounds']['x'] + more['bounds']['width'] <= diff_pane['x'])
    check('branch count badge is fully inside the button and viewport', header['badgeVisible'] and header['badgeVisibleHeight'] >= header['badge']['height'] * .99 and header['badge']['x'] >= more['bounds']['x'] and header['badge']['x'] + header['badge']['width'] <= more['bounds']['x'] + more['bounds']['width'] and header['badge']['y'] >= more['bounds']['y'] and header['badge']['y'] + header['badge']['height'] <= more['bounds']['y'] + more['bounds']['height'])
    check('branch action has no outer frame and a smaller contained badge', not header['buttonHasBorder'] and header['badge']['height'] == 14)
    command('commit_branches')
    popup = until(lambda: repository_state()['detailBranchPopup'])
    check('branch dropdown contains exactly the refs represented by the badge', popup['total'] == 3 and set(popup['rows']) == set(info['branches']))
    capture('commit-branches-main-window')
    capture('commit-branches-dropdown', active=True)
    info = select_header(unique)
    check('changing commits closes the old containing-branches dropdown', repository_state()['detailBranchPopup'] is None)
    single_layout = layout()
    check('a commit contained in one branch hides the ellipsis button', info['extraCount'] == 0 and info['total'] == 1 and not any(b['name'] == 'MoreCommitBranches' for b in single_layout['buttons']))
    check('single-branch metadata releases the unused action space', not single_layout['commitBranchHeaders'][0]['buttonVisible'])
    capture('commit-branch-single')
    for number in range(10):
        git('branch', f'badge-check-{number}', middle)
    refresh_header_repository(); many = select_header(middle)
    header = layout()['commitBranchHeaders'][0]
    check('two-digit branch badge stays completely visible', many['extraCount'] == 12 and header['badgeVisibleHeight'] >= header['badge']['height'] * .99)
    capture('commit-branch-two-digits')
    for number in range(10):
        git('branch', '-D', f'badge-check-{number}')
    refresh_header_repository(); select_header(unique)
    for sha in [middle, unique, feature_tip, middle, unique]:
        client.tool('ui_select_commit', {'repo': str(repo), 'revision': sha})
    until(lambda: not repository_state()['detailBranchesLoading'] and (repository_state()['detailBranches'] or {}).get('total') == 1)
    check('rapid commit selection cannot restore stale branch counts', repository_state()['detailCommit'] == unique and repository_state()['detailBranches']['extraCount'] == 0)
    git('reflog', 'expire', '--expire=now', '--all')
    client.close(); client = Client()
    client.tool('ui_window', {'width': 1280, 'height': 800, 'zoom': 1, 'theme': 'Blue'})
    client.tool('ui_open_repository', {'repo': str(repo)}); refresh_header_repository()
    info = select_header(middle)
    check('missing origin evidence falls back to the corresponding side graph line', info['primary'] == 'refs/heads/feature/ui' and info['inferred'] and info['extraCount'] == 2)
    capture('commit-branch-inferred')
    long_name = 'feature/用于验证窗口缩小后分支框和省略按钮仍保持可见的长分支名'
    git('branch', '-m', 'feature/ui', long_name)
    refresh_header_repository(); info = select_header(middle)
    until(lambda: (repository_state()['detailBranches'] or {}).get('primary') == 'refs/heads/' + long_name)
    split = next(t for t in layout()['splitters'] if t['owner'] == 'CommitChanges')
    client.tool('ui_layout', {'action': 'drag', 'splitter': split['index'], 'dx': -900})
    for theme in ['Blue', 'Light', 'Dark']:
        client.tool('ui_window', {'theme': theme}); time.sleep(.3)
        def narrow_ready():
            current = layout()
            state = repository_state()
            return current if not state['detailBranchesLoading'] and any(b['name'] == 'MoreCommitBranches' and b['enabled'] for b in current['buttons']) else None
        narrow_layout = until(narrow_ready)
        more = next(b for b in narrow_layout['buttons'] if b['name'] == 'MoreCommitBranches')
        (out / 'branch-narrow-layout.json').write_text(json.dumps({'layout': narrow_layout, 'state': repository_state()}, ensure_ascii=False), encoding='utf-8')
        capture('branch-narrow-current')
        header = narrow_layout['commitBranchHeaders'][0]
        check('narrow commit branch header keeps its action visible ' + theme, more['enabled'] and not more['outsideParentBorder'] and header['badgeVisibleHeight'] >= header['badge']['height'] * .99)
        check('long branch label wraps with its entire frame inside the viewport ' + theme, header['primaryDesiredHeight'] > 35 and header['primaryDesiredWidth'] <= header['primaryViewport']['width'] + .5 and header['primaryDesiredHeight'] <= header['primaryViewport']['height'] + .5)
        capture('commit-branch-narrow-' + theme.lower())
    def verify_scope(sha):
        current = until(lambda: repository_state() if (repository_state().get('commitBranchHighlights') or {}).get('sha') == sha and not repository_state()['detailBranchesLoading'] else None)
        scope = current['commitBranchHighlights']
        rows = (scope['local'] or []) + (scope['remote'] or [])
        actual = set(git('for-each-ref', '--contains=' + sha, '--format=%(refname)', 'refs/heads/', 'refs/remotes/').splitlines())
        assert {r['reference'] for r in rows} == actual, (scope, actual)
        assert all(r['level'] == (2 if r['reference'] == current['detailBranches']['primary'] else 1) for r in rows), scope
        return current, scope, rows
    select_header(middle)
    current, scope, rows = verify_scope(middle)
    check('all containing local and remote branches receive a background highlight', len(rows) == 3 and len(scope['local']) == 2 and len(scope['remote']) == 1)
    check('the darker sidebar branch matches the metadata primary branch', [r['reference'] for r in rows if r['level'] == 2] == [current['detailBranches']['primary']])
    check('an inferred primary branch carries the inferred tooltip note', scope['inferred'] and any('推定' in r['note'] for r in rows if r['level'] == 2))
    until(lambda: all(any(b['reference'] == r['reference'] for b in (repository_state()['localBranchTargets'] or []) + (repository_state()['remoteBranchTargets'] or [])) for r in rows))
    check('all containing branch folders expand automatically', current['localBranchTreeVisible'] and current['remoteBranchTreeVisible'])
    rendered = {t['reference']: t for t in until(lambda: layout() if len(layout().get('sidebarCommitTints', [])) >= 3 else None)['sidebarCommitTints']}
    painted = layout()
    shades = {t['reference']: t for t in painted['sidebarCommitShades']}
    check('sidebar uses theme-related primary and lighter coverage backgrounds', all(rendered[r['reference']]['opacity'] == 1 and rendered[r['reference']]['color'] == painted['selectedBranchBackground'] and shades[r['reference']]['color'] == painted['primaryBranchBackground'] and shades[r['reference']]['opacity'] == (1 if r['level'] == 2 else 0) for r in rows))
    capture('commit-all-branch-highlights')
    select_header(unique)
    current, scope, rows = verify_scope(unique)
    check('a new single-branch commit clears highlights from previously containing branches', len(rows) == 1 and rows[0]['reference'] == 'refs/heads/main' and rows[0]['level'] == 2)
    # Exercise actual history-row mouse clicks; only the owned CI GUI receives input.
    client.tool('ui_window', {'theme': 'Blue', 'width': 1280, 'height': 800, 'zoom': 1})
    remote_ref = 'refs/remotes/origin/folder/selection'
    local_ref = 'refs/heads/' + long_name
    tree = git('rev-parse', feature_tip + '^{tree}')
    local_new = git('commit-tree', tree, '-p', feature_tip, '-m', 'Local newer tip')
    remote_middle = git('commit-tree', tree, '-p', feature_tip, '-m', 'Remote intermediate selection')
    remote_new = git('commit-tree', tree, '-p', remote_middle, '-m', 'Remote newer tip')
    git('update-ref', local_ref, local_new); git('update-ref', remote_ref, remote_new)
    client.tool('ui_refresh', {'repo': str(repo)})
    client.tool('ui_graph', {'repo': str(repo), 'mode': 'all', 'showRemotes': True})
    until(lambda: not repository_state()['graphLoading'] and any(c['sha'] == remote_middle for c in repository_state()['graphCommits']))
    before_navigation = git('rev-parse', 'HEAD'), git('status', '--porcelain=v1')
    def native_click(target):
        assert client.tool('ui_state')['processId'] == client.gui_pid and Path(f'/proc/{client.gui_pid}/exe').resolve() == exe
        # Let pointer-enter/leave and tooltip dismissal reach the UI before the
        # press, matching a real pointer move rather than an instantaneous jump.
        subprocess.run(['xdotool', 'mousemove', '--sync', str(int(target['x'])), str(int(target['y'])), 'sleep', '.2'], check=True)
        pointer = subprocess.check_output(['xdotool', 'getmouselocation', '--shell'], text=True)
        subprocess.run(['xdotool', 'mousedown', '1'], check=True)
        try:
            time.sleep(.08)
            pressed = repository_state()
        finally:
            subprocess.run(['xdotool', 'mouseup', '1'], check=True)
        after = repository_state()
        with (out / 'native-clicks.jsonl').open('a', encoding='utf-8') as log:
            log.write(json.dumps({'time': time.time(), 'target': target, 'pointer': pointer, 'window': layout()['window'], 'pressed': pressed['historySelectedCommits'], 'after': after['historySelectedCommits'], 'pressedLocal': pressed['localBranchSelection'], 'afterLocal': after['localBranchSelection']}, ensure_ascii=False) + '\n')
    def click_commit(sha):
        # External ref changes can still be laying out a newer graph after the
        # query completes. Click only a stable, actually painted row position.
        previous = None
        stable_since = time.monotonic()
        def stable_row():
            nonlocal previous, stable_since
            current = repository_state()
            row = next((r for r in current['historyRows'] if r['sha'] == sha), None)
            key = None if current['graphLoading'] or row is None else (row['x'], row['y'], tuple(c['sha'] for c in current['graphCommits']))
            if key != previous:
                previous, stable_since = key, time.monotonic()
                return None
            return row if key is not None and time.monotonic() - stable_since >= .15 else None
        native_click(until(stable_row))
        until(lambda: repository_state()['historySelectedCommits'] == [sha], timeout=3)
    def collapse_group(prefix, state_name):
        if repository_state()[state_name]:
            native_click(next(b['screen'] for b in layout()['buttons'] if 'group_expander' in b['classes'] and b['text'].startswith(prefix)))
            until(lambda: not repository_state()[state_name])
    def selected_branch(sha, reference, local):
        current = repository_state()
        expected = current['localBranchSelection'] if local else current['remoteBranchSelection']
        return current if current['historySelectedCommits'] == [sha] and expected == [reference] and not current['detailBranchesLoading'] else None
    select_header(unique)
    collapse_group('分支', 'localBranchTreeVisible')
    click_commit(feature_tip)
    chosen = until(lambda: selected_branch(feature_tip, local_ref, True))
    check('a real history-row click prefers an untracked local branch over a containing remote', chosen['localBranchSelection'] == [local_ref] and not chosen['remoteBranchSelection'])
    until(lambda: any(b['reference'] == local_ref for b in repository_state()['localBranchTargets'] or []))
    check('history-row selection expands the local group and nested branch folder', repository_state()['localBranchTreeVisible'])
    check('sidebar highlighting keeps an intermediate commit selected instead of jumping to the branch tip', chosen['detailCommit'] == feature_tip and feature_tip != local_new)
    capture('history-click-local-sidebar')
    collapse_group('远程', 'remoteBranchTreeVisible')
    click_commit(remote_middle)
    chosen = until(lambda: selected_branch(remote_middle, remote_ref, False))
    check('a real remote-only commit click selects its remote branch', chosen['remoteBranchSelection'] == [remote_ref] and not chosen['localBranchSelection'])
    until(lambda: any(b['reference'] == remote_ref for b in repository_state()['remoteBranchTargets'] or []))
    check('remote selection expands the group and ancestor folders', repository_state()['remoteBranchTreeVisible'])
    check('remote sidebar highlighting also preserves the clicked intermediate commit', chosen['detailCommit'] == remote_middle and remote_middle != remote_new)
    capture('history-click-remote-sidebar')
    for sha in [feature_tip, remote_middle, unique, remote_middle, feature_tip]:
        client.tool('ui_select_commit', {'repo': str(repo), 'revision': sha})
    until(lambda: selected_branch(feature_tip, local_ref, True))
    check('rapid commit selection cannot restore an older sidebar highlight', repository_state()['localBranchSelection'] == [local_ref])
    exact_local = 'refs/heads/local/merged-tip'
    git('branch', 'local/merged-tip', middle)
    client.tool('ui_refresh', {'repo': str(repo)})
    until(lambda: not repository_state()['graphLoading'] and any(b['reference'] == exact_local for b in repository_state()['loadedBranches']))
    click_commit(middle)
    until(lambda: selected_branch(middle, exact_local, True))
    check('an exact local branch tip wins over another local branch merely containing the commit', repository_state()['localBranchSelection'] == [exact_local])
    git('branch', '-D', 'local/merged-tip')
    git('branch', 'tracking/remote', feature_tip)
    git('config', 'branch.tracking/remote.remote', 'origin')
    git('config', 'branch.tracking/remote.merge', 'refs/heads/folder/selection')
    client.tool('ui_refresh', {'repo': str(repo)})
    until(lambda: not repository_state()['graphLoading'] and any(b['reference'] == 'refs/heads/tracking/remote' and b['upstream'] == remote_ref for b in repository_state()['loadedBranches']))
    click_commit(remote_middle)
    chosen = until(lambda: selected_branch(remote_middle, 'refs/heads/tracking/remote', True))
    check('a local tracking branch is preferred even when it has not fetched the clicked remote commit', chosen['localBranchSelection'] == ['refs/heads/tracking/remote'])
    # Watcher refreshes replace branch-tree nodes independently of commit details.
    # Selection must remain usable throughout, without waiting for another query.
    for repeat in range(4):
        client.tool('ui_refresh', {'repo': str(repo)})
        deadline = time.monotonic() + .6
        while time.monotonic() < deadline:
            current = repository_state()
            assert current['localBranchSelection'] == ['refs/heads/tracking/remote'] and not current['remoteBranchSelection'], current
            assert current['historySelectedCommits'] == [remote_middle], current
            time.sleep(.02)
    check('branch-tree refresh retains the selected tracking branch and historical commit without flicker', True)
    verify_scope(remote_middle)
    check('background refresh restores the complete commit-branch scope without losing selection intent', True)
    git('branch', '-D', 'tracking/remote')
    client.tool('ui_refresh', {'repo': str(repo)})
    until(lambda: not repository_state()['graphLoading'] and not any(b['reference'] in [exact_local, 'refs/heads/tracking/remote'] for b in repository_state()['loadedBranches']))
    client.tool('ui_graph', {'repo': str(repo), 'mode': 'current', 'showRemotes': False})
    until(lambda: not repository_state()['graphLoading'] and repository_state()['graphMode'] == 1)
    shape = [(c['sha'], c['x']) for c in repository_state()['graphCommits']]
    click_commit(middle)
    until(lambda: selected_branch(middle, 'refs/heads/main', True))
    check('commit-to-sidebar synchronization retains graph mode and remote visibility', repository_state()['graphMode'] == 1 and not repository_state()['graphShowRemotes'])
    check('commit-to-sidebar synchronization retains the graph lanes and checked-out branch', shape == [(c['sha'], c['x']) for c in repository_state()['graphCommits']] and before_navigation == (git('rev-parse', 'HEAD'), git('status', '--porcelain=v1')))
    client.tool('ui_select_view', {'repo': str(repo), 'view': 'search'})
    client.tool('ui_search', {'repo': str(repo), 'method': 'sha', 'query': remote_middle})
    until(lambda: not repository_state()['searchQuerying'] and repository_state()['searchResultCount'] > 0)
    client.tool('ui_search', {'repo': str(repo), 'select': remote_middle, 'open': True})
    until(lambda: selected_branch(remote_middle, remote_ref, False) if not repository_state()['graphLoading'] and not repository_state()['searchActive'] else None)
    check('search-result navigation still selects and expands its matching branch', repository_state()['remoteBranchSelection'] == [remote_ref])
    current, scope, rows = verify_scope(remote_middle)
    check('search double-open applies the same complete branch-background logic', rows == scope['remote'] and len(rows) == 1 and rows[0]['reference'] == remote_ref and rows[0]['level'] == 2)
    client.tool('ui_graph', {'repo': str(repo), 'mode': 'current', 'showRemotes': False})
    until(lambda: not repository_state()['graphLoading'])
    select_header(middle)
    verify_scope(middle)
    native_click(next(b for b in repository_state()['localBranchTargets'] if b['reference'] == local_ref))
    until(lambda: repository_state()['commitBranchHighlights'] is None)
    client.tool('ui_refresh', {'repo': str(repo)})
    until(lambda: not repository_state()['graphLoading'] and not repository_state()['detailBranchesLoading'])
    current = repository_state()
    check('explicit sidebar selection cancels old branch coverage through subsequent refreshes', current['commitBranchHighlights'] is None)
    check('clicking another sidebar branch in current-only mode preserves the selected historical commit', current['historySelectedCommits'] == [middle] and current['graphMode'] == 1)
    check('sidebar synchronization performs no checkout, index write or working-tree edit', before_navigation == (git('rev-parse', 'HEAD'), git('status', '--porcelain=v1')))
    before_failed_refresh = repository_state()
    config = repo / '.git/config'
    config_bytes = config.read_bytes()
    try:
        config.write_text('[broken\n', encoding='utf-8')
        client.tool('ui_refresh', {'repo': str(repo)})
        until(lambda: not repository_state()['workingCopyStatusKnown'])
        time.sleep(.4)
        failed_refresh = repository_state()
        (out / 'failed-refresh-state.json').write_text(json.dumps(failed_refresh, ensure_ascii=False, indent=2), encoding='utf-8')
        capture('failed-refresh')
        check('a failed refresh preserves previously loaded branch references', failed_refresh['loadedBranches'] == before_failed_refresh['loadedBranches'])
        check('a failed refresh preserves history and the selected commit', [c['sha'] for c in failed_refresh['graphCommits']] == [c['sha'] for c in before_failed_refresh['graphCommits']] and failed_refresh['historySelectedCommits'] == before_failed_refresh['historySelectedCommits'])
    finally:
        config.write_bytes(config_bytes)
        client.tool('ui_refresh', {'repo': str(repo)})
        until(lambda: repository_state()['workingCopyStatusKnown'] and not repository_state()['graphLoading'])
    (out / 'ui-report.json').write_text(json.dumps({'passed': len(checks), 'checks': checks}, ensure_ascii=False, indent=2), encoding='utf-8')
except Exception as error:
    try:
        (out / 'failure-layout.json').write_text(json.dumps({'layout': layout(), 'state': client.tool('ui_state')}, ensure_ascii=False), encoding='utf-8')
        capture('failure')
    except Exception:
        pass
    (out / 'ui-report.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'error': repr(error)}, ensure_ascii=False, indent=2), encoding='utf-8')
    raise
finally:
    if client:
        client.close()
    shutil.rmtree(repo)
    if (out / 'remote.git').exists():
        shutil.rmtree(out / 'remote.git')
