"""Exercise language preview, table geometry, standalone viewers and search state.

Uses routed events in an owned integration application; no desktop mouse or keys.
Run in a disposable build directory, with an empty portable data profile.
"""
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import xml.etree.ElementTree as ET

from qa_cleanup import new_fixture, remove_fixture

exe, out = (Path(value).resolve() for value in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
root = new_fixture(out, 'TreeGit locale ')
temp_root = new_fixture(Path('/tmp') if os.name == 'posix' else Path(tempfile.gettempdir()), 'TreeGit locale tmp ')
profile = exe.parent / 'data/preference.json' if os.name == 'nt' else root / 'config/SourceGit/preference.json'
profile.parent.mkdir(parents=True, exist_ok=True)
backup = profile.read_bytes() if profile.exists() else None
if backup:
    settings = json.loads(backup.decode('utf-8-sig'))
    assert all(not workspace.get('Repositories') for workspace in settings.get('Workspaces', [])), 'Use an empty QA profile'
    assert not settings.get('RepositoryNodes'), 'Use an empty QA bookmark list'
repo = root / 'Demo repository'
repo.mkdir()
env = {**os.environ, 'TREEGIT_TEST_MENU_PROBE': '1', 'TREEGIT_LOCALE_RUN': str(root),
       'TMPDIR': str(temp_root), 'TEMP': str(temp_root), 'TMP': str(temp_root),
       'XDG_CONFIG_HOME': str(root / 'config'), 'XDG_CACHE_HOME': str(root / 'cache'),
       'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
(root / 'cache').mkdir()
checks, records = [], []
failure = None
pid = None
process = None
responses = queue.Queue()
sequence = 0
key = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'
source = Path(__file__).resolve().parents[1]
catalog = {lang: {element.attrib[key]: element.text or '' for element in ET.parse(source / f'src/Resources/Locales/{lang}.axaml').getroot() if key in element.attrib}
           for lang in ['zh_CN', 'zh_TW', 'en_US']}


def check(name, passed):
    assert passed, name
    checks.append(name)
    print('PASS', name, flush=True)


def git(*arguments):
    value = subprocess.run([shutil.which('git'), '-C', str(repo), *arguments], env=env, capture_output=True, text=True, encoding='utf-8')
    assert value.returncode == 0, value.stderr
    return value.stdout.strip()


def call(method, params=None):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params or {}}, ensure_ascii=False) + '\n')
    process.stdin.flush()
    answer = json.loads(responses.get(timeout=40))
    assert answer.get('id') == sequence and 'error' not in answer, answer
    return answer['result']


def tool(name, **arguments):
    answer = call('tools/call', {'name': name, 'arguments': arguments})
    assert not answer.get('isError'), answer
    return base64.b64decode(answer['content'][0]['data']) if name == 'ui_screenshot' else json.loads(answer['content'][0]['text'])


def menu(action, index=0, **arguments):
    return tool('ui_layout', action='test_menu', text=action, value=index, **arguments)


def choose(index):
    menu('language_open')
    menu('language_hover', index)
    menu('language_choose', index)
    time.sleep(.1)


def state():
    return next(value for value in tool('ui_state')['repositories'] if Path(value['repo']).resolve() == repo)


def wait(predicate):
    for _ in range(150):
        value = predicate()
        if value:
            return value
        time.sleep(.08)
    raise AssertionError('The expected UI state did not appear')


def header_geometry(label):
    result = menu('inspect')
    headers = [header for header in result['tableHeaders'] if header['text']]
    check(label + ' uses the shared static headers', bool(headers) and all(header['sharedTheme'] for header in headers))
    for header in headers:
        height = header['bounds']['height']
        check(label + ' header height: ' + header['text'], abs(height - 24) < 1)
        check(label + ' caption is centered: ' + header['text'],
              all(abs(point['x'] - header['bounds']['width'] / 2) < 2 and abs(point['y'] - height / 2) < 2
                  for point in header['textCenters'] if point))
        check(label + ' separator joins and stays inside its header: ' + header['text'],
              all(abs(line['y']) < 1 and abs(line['height'] - height) < 1 for line in header['separators']))
    backgrounds = [header['backgrounds'] for header in headers]
    for action in ['header_hover', 'header_press']:
        changed = menu(action)
        actual = [header['backgrounds'] for header in changed['tableHeaders'] if header['text']]
        check(label + ' does not recolor on ' + action, actual == backgrounds)
    menu('header_reset')
    original = headers[0]['bounds']['width']
    menu('column_drag', 0, dx=18)
    time.sleep(.1)
    resized = [header for header in menu('inspect')['tableHeaders'] if header['text']][0]['bounds']['width']
    check(label + ' column still resizes through its normal pointer handlers', abs(resized - original - 18) < 2)
    records.append({'headers': label, 'values': headers})


def close_owned():
    if not pid:
        return
    if os.name == 'posix':
        target = Path('/proc') / str(pid) / 'exe'
        if not target.exists():
            return
        assert target.resolve() == exe, 'A different process was left untouched'
        os.kill(pid, signal.SIGTERM)
        wait(lambda: not target.exists())
    else:
        literal = str(exe).replace("'", "''")
        command = f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{if($p.Path -ne '{literal}'){{throw 'Unexpected process'}};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(3000)){{$p.Kill();$p.WaitForExit()}}}}"
        subprocess.run(['powershell', '-NoProfile', '-Command', command], check=True, capture_output=True, timeout=15)


try:
    profile.write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light', 'Check4UpdatesOnStartup': False,
                                   'EnableAutoFetch': False}), encoding='utf-8')
    git('init', '-b', 'main')
    git('config', 'user.name', 'TreeGit demonstration')
    git('config', 'user.email', 'demo@example.invalid')
    (repo / 'demo.bin').write_bytes(bytes(range(256)) * 8)
    git('add', '.'); git('commit', '-m', 'Demo initial')
    (repo / 'demo.bin').write_bytes(bytes(range(256)) * 7 + bytes(range(255, -1, -1)))
    git('commit', '-am', 'Demo binary update')
    head = git('rev-parse', 'HEAD')
    git('branch', 'feature/demo')
    git('checkout', '--orphan', 'isolated')
    git('rm', '-rf', '.')
    (repo / 'unattached.txt').write_text('Tagged example without a containing branch\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', 'Demo tagged example')
    unattached = git('rev-parse', 'HEAD')
    git('tag', 'demo-tag-only')
    git('checkout', 'main'); git('branch', '-D', 'isolated')
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')]
    process = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=(out / 'mcp.log').open('w', encoding='utf-8'), text=True, encoding='utf-8',
                               creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    threading.Thread(target=lambda: [responses.put(line) for line in process.stdout], daemon=True).start()
    call('initialize', {'protocolVersion': '2025-11-25'})
    pid = tool('ui_state')['processId']
    if os.name == 'posix':
        check('connected to the exact owned executable', (Path('/proc') / str(pid) / 'exe').resolve() == exe)
    tool('ui_window', width=1280, height=820, state='normal')
    tool('ui_open_repository', repo=str(repo))
    tool('ui_select_commit', repo=str(repo), revision=head)
    wait(lambda: not state()['graphLoading'] and state()['selectedCommits'] == [head])
    check('startup commit navigation selects the real history row after loading', state()['selectedCommits'] == [head])
    wait(lambda: (state().get('commitBranchHighlights') or {}).get('sha') == head)
    check('startup commit navigation also selects and highlights the local branch',
          any(branch['reference'] == 'refs/heads/main' and branch['level'] == 2
              for branch in state()['commitBranchHighlights']['local']))
    for refresh in range(3):
        tool('ui_refresh', repo=str(repo))
        tool('ui_select_commit', repo=str(repo), revision=head)
        wait(lambda: not state()['graphLoading'] and state()['selectedCommits'] == [head] and
             (state().get('commitBranchHighlights') or {}).get('sha') == head)
        check('commit navigation retains sidebar highlighting through refresh ' + str(refresh),
              any(branch['reference'] == 'refs/heads/main' and branch['level'] == 2
                  for branch in state()['commitBranchHighlights']['local']))
    wait(lambda: state()['detailChanges'])
    tool('ui_diff', repo=str(repo), action='select', file='demo.bin', source='history')
    wait(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
    choose(0)
    for index, locale in [(2, 'en_US'), (1, 'zh_TW'), (0, 'zh_CN')]:
        menu('language_open'); preview = menu('language_hover', index)
        check(locale + ' preview does not change the saved selection', preview['activeLocale'] == locale and preview['savedLocale'] == 'zh_CN')
        menu('language_exit'); time.sleep(.15)
        check(locale + ' leaving the language popup restores selection', menu('inspect')['activeLocale'] == 'zh_CN')
        choose(index)
        check(locale + ' all background text keys match the active UI catalog', menu('catalog')['textCatalog'] == catalog[locale])
        toolbar = menu('inspect')['toolbarLabels']
        check(locale + ' toolbar labels remain fully readable', len(toolbar) == 12 and all(label['width'] + 1 >= label['textWidth'] for label in toolbar))
        expected_search = [catalog[locale]['Text.TreeGit.Search.NoMatches'], catalog[locale]['Text.TreeGit.Search.OneMatch'],
                           catalog[locale]['Text.TreeGit.Search.Matches'].replace('{0}', '12'),
                           catalog[locale]['Text.TreeGit.Search.Position'].replace('{0}', '3').replace('{1}', '12')]
        check(locale + ' editor search counts follow the chosen language', menu('inspect')['editorSearchMessages'] == expected_search)
        for theme in ['Light', 'Dark', 'Blue']:
            tool('ui_window', theme=theme)
            for header_index in range(len(menu('inspect')['headers'])):
                menu('reset')
                menu('press', header_index); opened = menu('release', header_index)
                check(locale + ' ' + theme + ' menu opens with its outline ' + str(header_index),
                      opened['headers'][header_index]['open'] and opened['headers'][header_index]['outlineVisible'])
                openings = sum(event['open'] for event in opened['events'])
                menu('press', header_index); closed = menu('release', header_index)
                check(locale + ' ' + theme + ' menu closes without reopening ' + str(header_index),
                      not closed['menuOpen'] and not any(header['open'] for header in closed['headers']) and
                      sum(event['open'] for event in closed['events']) == openings)
        tool('ui_window', theme='Light')
        binary = tool('ui_diff', repo=str(repo), action='state')
        check(locale + ' binary sides distinguish the parent from the selected commit',
              catalog[locale]['Text.Diff.Source.Parent'] in binary['oldTitle'] and
              head[:12] in binary['newTitle'] and binary['oldTitle'] != binary['newTitle'])
        header_geometry(locale + ' history')
        (out / (locale + '-history.png')).write_bytes(tool('ui_screenshot'))
        for width in [1024, 1280, 1600]:
            tool('ui_window', width=width, height=820, state='normal'); time.sleep(.1)
            layout = tool('ui_layout', action='list')
            group = layout['binaryJumpGroups'][0]['children']
            a, b = (child['bounds'] for child in group)
            check(locale + f' binary jump stays together at {width}', abs(a['y'] + a['height'] / 2 - b['y'] - b['height'] / 2) < 1)
        tool('ui_command', repo=str(repo), command='external_diff')
        wait(lambda: any(dialog['type'] == 'InternalDiffWindow' for dialog in tool('ui_state')['dialogs']))
        wait(lambda: any(pane['type'] == 'BinaryDiffView' for pane in tool('ui_layout', action='list', window='active')['panes']))
        builtin = tool('ui_layout', action='list', window='active')
        check(locale + ' the default comparison tool opens the built-in viewer', bool(builtin['diffs']))
        check(locale + ' the built-in tool has no recursive independent-window command',
              not any(button['text'] == catalog[locale]['Text.TreeGit.Binary.SeparateWindow'] for button in builtin['buttons']))
        count = len(tool('ui_state')['dialogs'])
        tool('ui_diff', repo=str(repo), action='window')
        check(locale + ' repeated API open cannot nest a built-in inspector', len(tool('ui_state')['dialogs']) == count)
        tool('ui_command', command='close_dialog')
        tool('ui_diff', repo=str(repo), action='window')
        time.sleep(.2)
        standalone = tool('ui_layout', action='list', window='active')
        check(locale + ' standalone inspector cannot open another inspector',
              not any(button['text'] == catalog[locale]['Text.TreeGit.Binary.SeparateWindow'] for button in standalone['buttons']))
        (out / (locale + '-standalone.png')).write_bytes(tool('ui_screenshot', window='active'))
        tool('ui_command', command='close_dialog')
        tool('ui_search', repo=str(repo), method='message', currentBranchOnly=False, query='Demo')
        wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 2)
        tool('ui_search', repo=str(repo), select=head)
        menu('search_focus')
        header_geometry(locale + ' search')
        (out / (locale + '-search.png')).write_bytes(tool('ui_screenshot'))
        cells = menu('inspect')['searchCells']
        check(locale + ' search has no cell focus rectangles', bool(cells) and all(cell['focusAdorner'] is None and not cell['focusBorders'] for cell in cells))
        current = state()['searchSelected']; menu('search_down')
        check(locale + ' search keyboard navigation still selects another commit', state()['searchSelected'] != current)
        tool('ui_search', repo=str(repo), method='sha', query=unattached)
        wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
        menu('language_open'); menu('language_hover', 2); time.sleep(.15)
        visible = [label['text'] for label in tool('ui_layout', action='list')['localizedTexts']]
        check(locale + ' no-containing-branch caption previews English live', catalog['en_US']['Text.TreeGit.Branches.None'] in visible)
        menu('reset'); time.sleep(.1)
        tool('ui_search', repo=str(repo), select=unattached, open=True)
        wait(lambda: not state()['searchActive'] and unattached in (state()['selectedCommits'] or []))
        check(locale + ' search navigation selects the actual commit', state()['detailCommit'] == unattached)
        tool('ui_select_commit', repo=str(repo), revision=head)
        wait(lambda: state()['detailCommit'] == head and state()['detailChanges'])
        tool('ui_diff', repo=str(repo), action='select', file='demo.bin', source='history')
        wait(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
        for command, page in [('preferences', 'general'), ('preferences', 'appearance'), ('preferences', 'git'),
                ('preferences', 'signing'), ('preferences', 'integration'), ('about', None),
                ('mcp_connection', None), ('repository_settings_remotes', None), ('repository_settings_advanced', None),
                ('credentials', None), ('file_history', None), ('directory_history', None), ('create_branch', None), ('reset_dialog', None)]:
            arguments = {'command': command, 'repo': str(repo)}
            if page: arguments['page'] = page
            if command == 'reset_dialog': arguments['revision'] = head
            if command == 'file_history': arguments['file'] = 'demo.bin'
            if command == 'directory_history': arguments['file'] = '.'
            if command.startswith('repository_settings_'):
                tool('ui_command', command='repository_settings', repo=str(repo))
            tool('ui_command', **arguments)
            time.sleep(.12)
            if command.startswith('repository_settings_'):
                wait(lambda: (dialogs := tool('ui_state')['dialogs']) and all(dialog['confirmVisible'] for dialog in dialogs))
            application = tool('ui_state')
            check(locale + ' ' + command + str(page or '') + ' stays owned and never globally topmost',
                  all(not dialog['topmost'] and dialog['owner'] for dialog in application['dialogs']))
            layout = tool('ui_layout', action='list', window='active')
            check(locale + ' ' + command + str(page or '') + ' has no missing UI resources',
                  layout['locale'] == locale and not any(value['text'].startswith('Text.') for value in layout['localizedTexts']))
            if command.startswith('repository_settings_'):
                check(locale + ' ' + command + ' confirmation stays visible',
                      bool(application['dialogs']) and all(dialog['confirmVisible'] for dialog in application['dialogs']))
            check(locale + ' ' + command + str(page or '') + ' stays within the display',
                  all(dialog['width'] * dialog['scaling'] <= dialog['workArea']['width'] + 1 and
                      dialog['height'] * dialog['scaling'] <= dialog['workArea']['height'] + 1
                      for dialog in application['dialogs'] if dialog['workArea']))
            if command in ['repository_settings_advanced', 'preferences']:
                (out / (locale + '-' + command + str(page or '') + '.png')).write_bytes(tool('ui_screenshot', window='active'))
            tool('ui_command', command='close_dialog')
            tool('ui_command', repo=str(repo), command='cancel_popup')
        for zoom in [1.5, 2]:
            tool('ui_window', width=1024, height=720, state='normal', zoom=zoom)
            for command in ['repository_settings_advanced', 'preferences']:
                if command.startswith('repository_settings_'):
                    tool('ui_command', command='repository_settings', repo=str(repo))
                tool('ui_command', command=command, repo=str(repo), **({'page': 'appearance'} if command == 'preferences' else {}))
                time.sleep(.15)
                if command.startswith('repository_settings_'):
                    wait(lambda: (dialogs := tool('ui_state')['dialogs']) and all(dialog['confirmVisible'] for dialog in dialogs))
                dialogs = tool('ui_state')['dialogs']
                requested_scale = float(os.environ.get('AVALONIA_GLOBAL_SCALE_FACTOR', '0'))
                if requested_scale:
                    check(locale + ' ' + command + ' uses the requested display scaling', bool(dialogs) and
                          all(abs(dialog['scaling'] - requested_scale) < .01 for dialog in dialogs))
                records.append({'locale': locale, 'dialog': command, 'zoom': zoom, 'windows': dialogs})
                check(locale + ' ' + command + f' fits the screen at {zoom}', bool(dialogs) and
                      all(dialog['width'] * dialog['scaling'] <= dialog['workArea']['width'] + 1 and
                          dialog['height'] * dialog['scaling'] <= dialog['workArea']['height'] + 1 for dialog in dialogs if dialog['workArea']))
                (out / (locale + '-' + command + '-zoom-' + str(zoom) + '.png')).write_bytes(tool('ui_screenshot', window='active'))
                tool('ui_command', command='close_dialog')
        tool('ui_window', width=1280, height=820, state='normal', zoom=1)
        choose(0)
    check('all UI checks preserve HEAD, index and working files', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
    # Exercise a real read-only MCP peer against the existing owned desktop.
    # It must never dispatch a configurable executable or shell command.
    tool('ui_select_commit', repo=str(repo), revision=head)
    tool('ui_diff', repo=str(repo), action='select', file='demo.bin', source='history')
    wait(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
    peer = None
    menu('external_unconfigured')
    try:
        with (out / 'readonly-peer.log').open('w', encoding='utf-8') as error_log:
            peer = subprocess.Popen([str(exe), '--mcp', '--read-only'], env=env, stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=error_log, text=True, encoding='utf-8',
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            peer_responses = queue.Queue()
            threading.Thread(target=lambda: [peer_responses.put(line) for line in peer.stdout], daemon=True).start()
            def peer_call(identifier, method, parameters):
                peer.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': identifier, 'method': method, 'params': parameters}) + '\n')
                peer.stdin.flush()
                response = json.loads(peer_responses.get(timeout=30))
                assert 'error' not in response, response
                return response['result']
            peer_call(1, 'initialize', {'protocolVersion': '2025-11-25'})
            response = peer_call(2, 'tools/call', {'name': 'ui_command', 'arguments': {'command': 'external_diff'}})
            check('a read-only peer can request a comparison', not response.get('isError'))
            wait(lambda: any(dialog['type'] == 'InternalDiffWindow' for dialog in tool('ui_state')['dialogs']))
            check('a read-only peer uses the built-in viewer despite the configured external preference',
                  any(dialog['type'] == 'InternalDiffWindow' for dialog in tool('ui_state')['dialogs']))
            tool('ui_command', command='close_dialog')
    finally:
        if peer:
            if peer.poll() is None:
                peer.stdin.close()
                try: peer.wait(timeout=10)
                except subprocess.TimeoutExpired: peer.kill(); peer.wait()
        menu('external_restore')
    cycles = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    assert 0 <= cycles <= 300, 'Choose between 0 and 300 soak cycles'
    samples = []
    for cycle in range(cycles):
        tool('ui_select_commit', repo=str(repo), revision=head)
        wait(lambda: state()['detailCommit'] == head and state()['detailChanges'])
        tool('ui_diff', repo=str(repo), action='select', file='demo.bin', source='history')
        wait(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
        tool('ui_command', repo=str(repo), command='external_diff')
        wait(lambda: any(dialog['type'] == 'InternalDiffWindow' for dialog in tool('ui_state')['dialogs']))
        wait(lambda: any(pane['type'] == 'BinaryDiffView' for pane in tool('ui_layout', action='list', window='active')['panes']))
        wait(lambda: tool('ui_diff', repo=str(repo), action='state').get('status') == 'ready')
        snapshot = Path(tool('ui_diff', repo=str(repo), action='state')['snapshotDirectory']).resolve()
        check(f'soak {cycle}: comparison snapshot belongs to the isolated temporary folder', snapshot.is_relative_to(temp_root))
        tool('ui_command', command='close_dialog')
        wait(lambda: not snapshot.exists())
        check(f'soak {cycle}: closing the viewer releases its snapshot', not snapshot.exists())
        tool('ui_search', repo=str(repo), query='Demo', method='message', currentBranchOnly=False)
        wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 2)
        tool('ui_search', repo=str(repo), select=head, open=True)
        wait(lambda: not state()['searchActive'] and state()['selectedCommits'] == [head])
        check(f'soak {cycle}: search and history keep the requested commit', state()['detailCommit'] == head)
        tool('ui_window', state='maximized' if cycle % 2 else 'normal')
        tool('ui_refresh', repo=str(repo))
        wait(lambda: not state()['graphLoading'])
        if cycle % 10 == 0 or cycle == cycles - 1:
            sample = menu('inspect')['resources']
            sample['cycle'] = cycle
            sample['temporaryBytes'] = sum(path.stat().st_size for path in temp_root.rglob('*') if path.is_file() and not path.is_symlink())
            samples.append(sample)
            check(f'soak {cycle}: temporary files remain bounded', sample['temporaryBytes'] < 2 * 1024 ** 3)
            check(f'soak {cycle}: owned handles remain bounded', sample['handles'] <= samples[0]['handles'] + 40)
            check(f'soak {cycle}: resident memory remains bounded', sample['workingSet'] <= samples[0]['workingSet'] + 256 * 1024 ** 2)
    if cycles:
        records.append({'soakCycles': cycles, 'samples': samples})
        check('the soak preserves repository data', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
        tool('ui_window', width=1280, height=820, state='normal')
    # A second, explicit mutation fixture exercises real conflict state. The
    # read-only navigation checks above have already verified the first repo.
    repo = root / 'Merge demonstration'
    repo.mkdir()
    git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit demonstration'); git('config', 'user.email', 'demo@example.invalid')
    git('config', 'merge.conflictStyle', 'diff3')
    lines = ['context line ' + str(i) + '\n' for i in range(30)]
    (repo / 'merge.txt').write_text(''.join(lines), encoding='utf-8')
    git('add', '.'); git('commit', '-m', 'Initial merge example')
    git('checkout', '-b', 'feature/demo')
    remote = lines.copy(); remote[2] = 'Remote first change\n'; remote[24] = 'Remote second change\n'
    (repo / 'merge.txt').write_text(''.join(remote), encoding='utf-8'); git('commit', '-am', 'Remote changes')
    git('checkout', 'main')
    local = lines.copy(); local[2] = 'Local first change\n'; local[24] = 'Local second change\n'
    (repo / 'merge.txt').write_text(''.join(local), encoding='utf-8'); git('commit', '-am', 'Local changes')
    original_head = git('rev-parse', 'HEAD')
    result = subprocess.run(['git', '-C', str(repo), 'merge', '--no-edit', 'feature/demo'], env=env, capture_output=True)
    check('the merge fixture has actual conflicts', result.returncode == 1 and bool(git('ls-files', '--unmerged')))
    tool('ui_open_repository', repo=str(repo))
    wait(lambda: state()['changes'] == 1)
    tool('ui_diff', repo=str(repo), action='select', file='merge.txt', source='unstaged')
    tool('ui_command', repo=str(repo), command='merge_conflicts', file='merge.txt')
    menu('merge_reopen')
    check('opening the same conflicted file reuses its existing editor', sum(bool(dialog.get('merge')) for dialog in tool('ui_state')['dialogs']) == 1)
    def editor_state():
        return next(dialog['merge'] for dialog in tool('ui_state')['dialogs'] if dialog.get('merge'))
    check('the editor starts with two independently editable conflicts', editor_state()['unsolved'] == editor_state()['totalConflicts'] == 2)
    check('an unresolved result cannot be saved or staged', not tool('ui_merge_result', repo=str(repo), file='merge.txt', action='save')['saved'])
    for index, locale in [(2, 'en_US'), (1, 'zh_TW'), (0, 'zh_CN')]:
        choose(index)
        expected = catalog[locale]['Text.MergeConflictEditor.ConflictsRemaining'].replace('{0}', '2')
        error = catalog[locale]['Text.MergeConflictEditor.StillUnresolved']
        labels = [value['text'] for value in tool('ui_layout', action='list', window='active')['localizedTexts']]
        check(locale + ' merge count refreshes in the existing editor', expected in labels)
        check(locale + ' merge errors refresh in the existing editor', error in labels and editor_state()['error'] == error)
        main_labels = [value['text'] for value in tool('ui_layout', action='list', window='main')['localizedTexts']]
        check(locale + ' inline conflict reason refreshes', catalog[locale]['Text.Conflict.State.BothModified'] in main_labels)
        (out / (locale + '-merge.png')).write_bytes(tool('ui_screenshot', window='active'))
    tool('ui_merge_result', repo=str(repo), file='merge.txt', action='resolve', region=0, choice='ours')
    check('resolving updates the conflict immediately while preserving the original total', editor_state()['unsolved'] == 1 and editor_state()['totalConflicts'] == 2 and editor_state()['regions'][0]['resolved'])
    tool('ui_merge_result', repo=str(repo), file='merge.txt', action='undo')
    check('undo restores the unresolved conflict', editor_state()['unsolved'] == 2)
    tool('ui_merge_result', repo=str(repo), file='merge.txt', action='resolve', region=0, choice='ours')
    tool('ui_merge_result', repo=str(repo), file='merge.txt', action='resolve', region=1, choice='theirs')
    check('both conflicts must be resolved before saving', editor_state()['canSave'] and editor_state()['unsolved'] == 0)
    check('save and stage completes and closes the editor', tool('ui_merge_result', repo=str(repo), file='merge.txt', action='save')['saved'])
    wait(lambda: not any(dialog.get('merge') for dialog in tool('ui_state')['dialogs']))
    check('the real index is resolved and HEAD is unchanged', not git('ls-files', '--unmerged') and git('rev-parse', 'HEAD') == original_head)
    expected_lines = lines.copy(); expected_lines[2] = local[2]; expected_lines[24] = remote[24]
    check('the saved result contains both requested choices', (repo / 'merge.txt').read_text(encoding='utf-8') == ''.join(expected_lines))
except BaseException:
    failure = traceback.format_exc()
    if pid:
        try: (out / 'failure.png').write_bytes(tool('ui_screenshot'))
        except Exception: pass
        try: (out / 'failure-active.png').write_bytes(tool('ui_screenshot', window='active'))
        except Exception: pass
        try: (out / 'failure-state.json').write_text(json.dumps(tool('ui_state'), ensure_ascii=False, indent=2), encoding='utf-8')
        except Exception: pass
    raise
finally:
    try:
        close_owned()
    finally:
        if process:
            if process.poll() is None:
                process.stdin.close()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        if backup is not None:
            profile.write_bytes(backup)
        elif profile.exists():
            profile.unlink()
        (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'records': records,
            'failure': failure, 'committed': False, 'published': False}, ensure_ascii=False, indent=2), encoding='utf-8')
        remove_fixture(root, out, 'TreeGit locale ')
        remove_fixture(temp_root, temp_root.parent, 'TreeGit locale tmp ')
print(json.dumps({'passed': len(checks), 'failure': failure}))
