"""Hit-test search cell whitespace, text opacity and popup scrollbar clearance.

Uses real hit testing and routed input in an isolated integration application;
never moves the desktop pointer or sends operating-system keyboard input.
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
from qa_cleanup import new_fixture, remove_fixture

exe, out = (Path(v).resolve() for v in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
root = new_fixture(out, 'TreeGit search interaction ')
repo = root / 'demo'
repo.mkdir()
profile = exe.parent / 'data/preference.json' if os.name == 'nt' else root / 'config/SourceGit/preference.json'
profile.parent.mkdir(parents=True, exist_ok=True)
backup = profile.read_bytes() if profile.exists() else None
if backup:
    saved = json.loads(backup.decode('utf-8-sig'))
    assert not saved.get('RepositoryNodes') and all(not w.get('Repositories') for w in saved.get('Workspaces', []))
temp = new_fixture(Path(tempfile.gettempdir()), 'TreeGit search input temp ')
env = {**os.environ, 'TREEGIT_TEST_MENU_PROBE': '1', 'XDG_CONFIG_HOME': str(root / 'config'),
       'XDG_CACHE_HOME': str(root / 'cache'), 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1',
       'TMPDIR': str(temp), 'TEMP': str(temp), 'TMP': str(temp)}
checks, records = [], []
process = None
pid = None
failure = None
responses = queue.Queue()
sequence = 0


def git(*args, **kwargs):
    if isinstance(kwargs.get('input'), str):
        kwargs['input'] = kwargs['input'].encode('utf-8')
    return subprocess.check_output([shutil.which('git'), '-C', str(repo), *args], env=env, **kwargs).decode('utf-8').strip()


def rpc(method, params):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params}) + '\n')
    process.stdin.flush()
    answer = json.loads(responses.get(timeout=40))
    assert answer.get('id') == sequence and 'error' not in answer, answer
    return answer['result']


def tool(name, **args):
    for attempt in range(30):
        result = rpc('tools/call', {'name': name, 'arguments': args})
        # This guard runs before raising any input event. A just-switched, occluded
        # Windows table may need another compositor frame; never send to a stale hit.
        if (name == 'ui_layout' and args.get('action') == 'test_menu' and result.get('isError') and
                result.get('content') in [[{'type': 'text', 'text': message}] for message in [
                    'The rendered hit-test scene has not caught up with the table layout.', 'Blank cell space did not receive input.']] and attempt < 29):
            time.sleep(.1)
            continue
        break
    assert not result.get('isError'), result
    return base64.b64decode(result['content'][0]['data']) if name == 'ui_screenshot' else json.loads(result['content'][0]['text'])


def probe(action='inspect', column=0):
    return tool('ui_layout', action='test_menu', text=action, value=column)


def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)


def wait(predicate):
    for _ in range(150):
        result = predicate()
        if result:
            return result
        time.sleep(.08)
    raise AssertionError('Expected search state did not appear')


def check(name, condition, details=None):
    assert condition, (name, details)
    checks.append(name)
    print('PASS', name, flush=True)


try:
    profile.write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light', 'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False}), encoding='utf-8')
    git('init', '-b', 'main')
    git('config', 'user.name', 'TreeGit Demo')
    git('config', 'user.email', 'demo@example.invalid')
    (repo / 'sample.txt').write_text('first\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', 'Search first')
    first = git('rev-parse', 'HEAD')
    (repo / 'sample.txt').write_text('second\n', encoding='utf-8')
    git('commit', '-am', 'Search second')
    head = git('rev-parse', 'HEAD')
    git('update-ref', '--stdin', input=''.join(f'create refs/heads/zz/review-{i:02} {head}\n' for i in range(32)))
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'), git('for-each-ref')]
    process = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=(out / 'mcp.log').open('w', encoding='utf-8'), text=True, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    threading.Thread(target=lambda: [responses.put(v) for v in process.stdout], daemon=True).start()
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    pid = tool('ui_state')['processId']
    if os.name == 'posix':
        assert (Path('/proc') / str(pid) / 'exe').resolve() == exe
    tool('ui_open_repository', repo=str(repo))
    tool('ui_search', repo=str(repo), query='Search', method='message', currentBranchOnly=False)
    wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 2)
    for locale in ['zh_CN', 'zh_TW', 'en_US']:
        for theme in ['Light', 'Dark', 'Blue']:
            tool('ui_window', locale=locale, theme=theme, zoom=1, width=1280, height=820, state='normal')
            for width in [1024, 1280]:
                tool('ui_window', width=width)
                if os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows':
                    # Hosted desktops can be narrower than the product's minimum
                    # window. Keep actual routed hit testing on the visible desktop.
                    probe('fit_desktop')
                time.sleep(.2)
                for column in range(1, 5):
                    click = probe('search_click_blank', column)['searchClick']
                    check(f'{locale} {theme} {width} column {column} whitespace selects its row', click['selected'] == click['expected'], click)
            rows = probe()['searchRows']
            check(f'{locale} {theme} branch text matches description color and opacity', len(rows) == 2 and all(
                row['branchForeground'] == row['descriptionForeground'] and row['branchOpacity'] == 1 for row in rows), rows)
            double = probe('search_doubletap_blank', 1)['searchClick']
            records.append({'doubleTap': double, 'stateAfterDoubleTap': state()})
            wait(lambda: not state()['searchActive'] and not state()['graphLoading'] and state()['historySelectedCommits'] == [double['expected']])
            check(f'{locale} {theme} double tapping cell whitespace opens the selected history commit', state()['graphMode'] == 0 and state()['graphShowRemotes'])
            history = probe('history_click_blank', 1)['searchClick']
            check(f'{locale} {theme} history whitespace also selects its row', history['selected'] == history['expected'], history)
            tool('ui_search', repo=str(repo), query='Search', method='message', currentBranchOnly=False)
            wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 2)
    # Geometry checks use each popup's own coordinate space. Keep the input
    # phase separate so an occluded Windows compositor cannot expose a stale
    # pre-zoom hit-test scene to the routed mouse fixture.
    for locale in ['zh_CN', 'zh_TW', 'en_US']:
        for theme in ['Light', 'Dark', 'Blue']:
            tool('ui_window', locale=locale, theme=theme, width=1280)
            for zoom in [1, 1.5, 2]:
                tool('ui_search', repo=str(repo), select=first)
                tool('ui_window', zoom=zoom)
                time.sleep(.2)
                tool('ui_search', repo=str(repo), select=head)
                wait(lambda: state()['detailCommit'] == head and not state()['detailBranchesLoading'])
                tool('ui_search', repo=str(repo), showBranches=True)
                geometry = wait(lambda: (v if (v := probe()['branchPopups']) and v[0]['labels'] and v[0]['bars'] else None))[0]
                records.append({'locale': locale, 'theme': theme, 'zoom': zoom, **geometry})
                check(f'{locale} {theme} zoom {zoom} current label fits before the scrollbar', all(
                    label['bounds']['right'] + 1 <= bar['x'] and label['bounds']['width'] + .5 >= label['desiredWidth']
                    for label in geometry['labels'] for bar in geometry['bars']))
                if theme == 'Light' and zoom == 1:
                    (out / (locale + '-popup.png')).write_bytes(base64.b64decode(probe('search_popup_snapshot')['png']))
                tool('ui_search', repo=str(repo), select=first)
    check('UI interactions preserve HEAD, index, files and all branch references', before == [
        git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'), git('for-each-ref')])
    tool('ui_window', zoom=1, width=1280)
    tool('ui_search', repo=str(repo), query='Search', method='message', select=head)
    wait(lambda: not state()['searchQuerying'] and state()['detailChanges'])
    session = lambda: next(v for v in probe()['searchSessions'] if Path(v['repo']).resolve() == repo)
    cached = session()
    for target in ['history', 'working_copy', 'stashes', 'overview']:
        if target == 'overview': tool('ui_working_copy', repo=str(repo))
        else: tool('ui_select_view', repo=str(repo), view=target)
        tool('ui_select_view', repo=str(repo), view='search')
        restored = session()
        check('return from ' + target + ' restores the same results and selected detail without searching',
              not state()['searchQuerying'] and all(restored[k] == cached[k] for k in ['runs', 'results', 'selected', 'detail']))
    def drag_panel(owner, delta):
        layout = tool('ui_layout', action='list')
        split = next(s for s in layout['splitters'] if s['visible'] and s['owner'] == owner and s['column'] == 1)
        tool('ui_layout', action='drag', splitter=split['index'], dx=delta)
        return session()['width']
    def panel_width(owner):
        layout = tool('ui_layout', action='list')
        return next(s['actualColumns'][0] for s in layout['splitters'] if s['visible'] and s['owner'] == owner and s['column'] == 1)
    tool('ui_working_copy', repo=str(repo))
    width = drag_panel('WorkingCopy', 48)
    tool('ui_select_view', repo=str(repo), view='history')
    tool('ui_select_commit', repo=str(repo), revision=head)
    wait(lambda: state()['detailChanges'])
    wait(lambda: abs(panel_width('CommitChanges') - width) < 1.5)
    check('history reuses the width dragged in file status', abs(session()['width'] - width) < 1 and abs(panel_width('CommitChanges') - width) < 1.5)
    width = drag_panel('CommitChanges', -24)
    tool('ui_working_copy', repo=str(repo))
    wait(lambda: abs(panel_width('WorkingCopy') - width) < 1.5)
    check('file status reuses the width dragged in history', abs(session()['width'] - width) < 1 and abs(panel_width('WorkingCopy') - width) < 1.5)
    tool('ui_select_view', repo=str(repo), view='search')
    tool('ui_working_copy', repo=str(repo))
    check('switching through search does not reset the shared width', abs(session()['width'] - width) < 1)
    time.sleep(.4)
    persisted = json.loads((repo / '.git/sourcegit.uistates').read_text(encoding='utf-8-sig'))
    check('the shared width is saved inside this repository', abs(persisted['ChangesPanelWidth'] - width) < 1)
    other = root / 'other'
    git('clone', '--shared', str(repo), str(other))
    tool('ui_open_repository', repo=str(other))
    tool('ui_select_view', repo=str(other), view='working_copy')
    other_session = next(v for v in probe()['searchSessions'] if Path(v['repo']).resolve() == other)
    check('a different repository does not inherit the dragged width', abs(other_session['width'] - width) > 1)
    tool('ui_open_repository', repo=str(repo))
    tool('ui_select_view', repo=str(repo), view='search')
    check('returning to the first repository retains its width', abs(session()['width'] - width) < 1)
    original_runs = session()['runs']
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['graphLoading'])
    time.sleep(.3)
    check('refresh with unchanged repository refs does not repeat the search', session()['runs'] == original_runs)
    (repo / 'sample.txt').write_text('third\n', encoding='utf-8')
    git('commit', '-am', 'Search third')
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 3)
    check('a real local commit automatically refreshes the visible search results', session()['runs'] > original_runs)
    tool('ui_select_view', repo=str(repo), view='history')
    inactive_runs = session()['runs']
    (repo / 'sample.txt').write_text('fourth\n', encoding='utf-8')
    git('commit', '-am', 'Search fourth')
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['graphLoading'])
    time.sleep(.3)
    check('a change while search is hidden marks the cache stale without querying', session()['runs'] == inactive_runs)
    tool('ui_select_view', repo=str(repo), view='search')
    wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 4)
    check('returning after an actual change refreshes the retained search snapshot', session()['runs'] > inactive_runs)
    remote = root / 'remote.git'
    git('clone', '--bare', str(repo), str(remote))
    git('remote', 'add', 'origin', str(remote))
    previous_runs = session()['runs']
    git('fetch', 'origin')
    tool('ui_refresh', repo=str(repo))
    latest = git('rev-parse', 'HEAD')
    def latest_contains_remote():
        return any(row['sha'] == latest and 'refs/remotes/origin/main' in row['branches'] for row in state()['searchResults'])
    wait(lambda: not state()['searchQuerying'] and latest_contains_remote())
    check('fetch updates containing-branch data in the search snapshot', session()['runs'] > previous_runs)
    previous_runs = session()['runs']
    git('--git-dir=' + str(remote), 'update-ref', 'refs/heads/main', git('rev-parse', 'HEAD^'))
    git('fetch', 'origin')
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['searchQuerying'] and not latest_contains_remote())
    check('a changed remote head invalidates the old branch coverage', session()['runs'] > previous_runs)
    previous_runs = session()['runs']
    git('push', 'origin', 'main')
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['searchQuerying'] and latest_contains_remote())
    check('push refreshes the search snapshot after remote synchronization', session()['runs'] > previous_runs)
    # A side branch is merged into the currently checked-out main branch. Its origin
    # must remain the side branch, even after main also contains the selected commit.
    git('checkout', '-b', 'feature/origin')
    (repo / 'origin.txt').write_text('side branch\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', 'Origin branch change')
    origin_sha = git('rev-parse', 'HEAD')
    git('branch', 'derived/child')
    git('push', '-u', 'origin', 'feature/origin')
    git('remote', 'add', 'other', str(remote))
    git('update-ref', 'refs/remotes/other/feature/origin', origin_sha)
    git('checkout', 'main')
    git('merge', '--no-ff', 'feature/origin', '-m', 'Integrate side branch')
    origin_before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'), git('for-each-ref')]
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['graphLoading'] and any(b['reference'] == 'refs/heads/feature/origin' for b in state()['loadedBranches']))
    local_origin, remote_origin = 'refs/heads/feature/origin', 'refs/remotes/origin/feature/origin'
    pair = {local_origin, remote_origin}
    probe('sidebar_folders_open')

    def highlights():
        current = state()
        scope = current['commitBranchHighlights']
        if not scope or scope['sha'] != origin_sha:
            return None
        return {b['reference']: b['level'] for b in scope['local'] + scope['remote']}

    def inspect_pair(label):
        levels = wait(highlights)
        check(label + ' paints both tracked refs deeply and other containing refs lightly',
              {r for r, level in levels.items() if level == 2} == pair and
              levels.get('refs/heads/main') == 1 and levels.get('refs/heads/derived/child') == 1 and
              levels.get('refs/remotes/other/feature/origin') == 1, levels)
        visual = probe()
        names = [b for b in visual['sidebarBranches'] if b['reference'] in pair]
        check(label + ' does not bold sidebar names to represent the origin pair', len(names) == 2 and
              all(b['weights'] and not b['bold'] for b in names), names)

    for locale in ['zh_CN', 'zh_TW', 'en_US']:
        for theme in ['Light', 'Dark', 'Blue']:
            label = locale + ' ' + theme
            tool('ui_window', locale=locale, theme=theme, zoom=1, width=1280)
            tool('ui_search', repo=str(repo), query=origin_sha, method='sha', currentBranchOnly=False)
            wait(lambda: not state()['searchQuerying'] and state()['detailCommit'] == origin_sha and not state()['detailBranchesLoading'])
            row = state()['searchResults'][0]
            check(label + ' search branch column uses the origin rather than current main',
                  row['branch'] == 'feature/origin' and state()['detailBranches']['primary'] == local_origin, row)
            check(label + ' search header is Branch', probe()['searchHeaders'][0] == ('Branch' if locale == 'en_US' else '分支'))
            inspect_pair(label + ' search')
            tool('ui_search', repo=str(repo), showBranches=True, branchFilter='feature/origin')
            popup = wait(lambda: (v if (v := probe()['branchPopups']) and len(v[0]['rows']) >= 3 else None))[0]
            check(label + ' popup bolds only the actual origin tracking pair',
                  {b['reference'] for b in popup['rows'] if b['weight'] == 'Bold'} == pair, popup['rows'])
            tool('ui_search', repo=str(repo), open=True)
            wait(lambda: not state()['searchActive'] and not state()['graphLoading'] and
                 state()['detailCommit'] == origin_sha and not state()['detailBranchesLoading'])
            inspect_pair(label + ' history')
            folded = probe('sidebar_folders_close')['sidebarFolders']
            required_folders = {'refs/heads/feature', 'refs/heads/derived', 'refs/remotes/origin', 'refs/remotes/other'}
            check(label + ' collapsed folders summarize contained branches with the light level',
                  all(any(f['path'] == path and not f['expanded'] and f['level'] == 1 for f in folded) for path in required_folders), folded)
            tool('ui_search', repo=str(repo), query=origin_sha, method='sha')
            wait(lambda: not state()['searchQuerying'] and not state()['detailBranchesLoading'])
            folded = probe()['sidebarFolders']
            check(label + ' selecting search results preserves collapsed folder paths',
                  all(any(f['path'] == path and not f['expanded'] and f['level'] == 1 for f in folded) for path in required_folders), folded)
            probe('sidebar_groups_close')
            tool('ui_search', repo=str(repo), open=True)
            wait(lambda: not state()['searchActive'] and not state()['graphLoading'] and not state()['detailBranchesLoading'])
            groups = probe()['sidebarGroups']
            light_color = tool('ui_layout')['selectedBranchBackground']
            check(label + ' history navigation keeps top groups closed with the light background', len(groups) == 2 and
                  all(not g['expanded'] and g['covered'] and g['background'] == light_color and g['renderedBackground'] == light_color for g in groups), groups)
            for target in ['working_copy', 'history', 'stashes', 'search']:
                tool('ui_select_view', repo=str(repo), view=target)
                current_session = session()
                check(label + ' ' + target + ' preserves top group collapse state',
                      not current_session['localExpanded'] and not current_session['remoteExpanded'])
            tool('ui_refresh', repo=str(repo))
            wait(lambda: not state()['graphLoading'] and not state()['searchQuerying'])
            opened = probe('sidebar_groups_open')
            check(label + ' reopening top groups retains collapsed child folders after refresh',
                  all(any(f['path'] == path and not f['expanded'] and f['level'] == 1 for f in opened['sidebarFolders']) for path in required_folders) and
                  all(g['expanded'] and g['background'] != light_color for g in opened['sidebarGroups']), opened['sidebarFolders'])
            probe('sidebar_folders_open')
            inspect_pair(label + ' reexpanded')
            for zoom in [1, 1.5, 2]:
                tool('ui_window', zoom=zoom, width=1024)
                time.sleep(.15)
                trees = probe()['sidebarClearances']
                check(label + ' zoom ' + str(zoom) + ' sidebar backgrounds stay before the scrollbar',
                      len(trees) == 2 and all(tree['rows'] and tree['bars'] and all(
                          row['right'] + 1 <= bar['x'] for row in tree['rows'] for bar in tree['bars']) for tree in trees), trees)
            tool('ui_window', zoom=1, width=1280)
    check('origin highlighting and navigation preserve repository contents and refs', origin_before == [
        git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'), git('for-each-ref')])
    # Local and remote names need not match: upstream configuration defines the pair.
    git('branch', '-m', 'feature/origin', 'local/renamed')
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['graphLoading'] and any(b['reference'] == 'refs/heads/local/renamed' for b in state()['loadedBranches']))
    local_origin = 'refs/heads/local/renamed'
    pair = {local_origin, remote_origin}
    probe('sidebar_folders_open')
    tool('ui_search', repo=str(repo), query=origin_sha, method='sha')
    wait(lambda: not state()['searchQuerying'] and state()['detailCommit'] == origin_sha and not state()['detailBranchesLoading'])
    check('a renamed local branch keeps the same upstream origin pair',
          state()['searchResults'][0]['branch'] == 'local/renamed' and {r for r, level in wait(highlights).items() if level == 2} == pair)
    # A remote ref rewound before the commit is not highlighted just because it is tracked.
    git('update-ref', remote_origin, latest)
    tool('ui_refresh', repo=str(repo))
    wait(lambda: not state()['searchQuerying'] and remote_origin not in state()['searchResults'][0]['branches'])
    check('a tracking remote that no longer contains the commit has no deep background',
          remote_origin not in wait(highlights) and wait(highlights).get(local_origin) == 2)
    git('reflog', 'expire', '--expire=now', local_origin)
    tool('ui_search', repo=str(repo), query=origin_sha, method='sha')
    wait(lambda: not state()['searchQuerying'] and not state()['detailBranchesLoading'] and state()['detailBranches'])
    inferred = state()['detailBranches']
    check('missing local origin evidence falls back to a consistently marked graph inference',
          inferred['inferred'] and inferred['primary'] in inferred['branches'] and
          state()['searchResults'][0]['branch'] == inferred['primary'].removeprefix('refs/heads/').removeprefix('refs/remotes/') and
          wait(highlights).get(inferred['primary']) == 2)

    # History and file status share one last-used surface. Search has its own
    # selection; changing its commit/file must not overwrite that shared state.
    repo = root / 'Navigation state'
    repo.mkdir()
    git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit Demo'); git('config', 'user.email', 'demo@example.invalid')
    for name in ['a.bin', 'z.bin']: (repo / name).write_bytes(bytes(range(64)))
    git('add', '.'); git('commit', '-m', 'Navigation base')
    git('checkout', '-b', 'history/view')
    for name in ['a.bin', 'z.bin']: (repo / name).write_bytes(b'\0' + bytes(range(1, 65)))
    git('commit', '-am', 'Earlier selected revision'); earlier = git('rev-parse', 'HEAD')
    git('checkout', 'main')
    for name in ['a.bin', 'z.bin']: (repo / name).write_bytes(b'\0' + bytes(range(2, 66)))
    git('commit', '-am', 'Latest revision'); latest = git('rev-parse', 'HEAD')
    (repo / '.git/sourcegit.uistates').write_text(json.dumps({'IsLocalBranchesExpandedInSideBar': True,
        'ExpandedBranchNodesInSideBar': ['refs/heads/history']}), encoding='utf-8')
    tool('ui_open_repository', repo=str(repo)); tool('ui_graph', repo=str(repo), mode='all', showRemotes=True)
    wait(lambda: not state()['graphLoading'])

    def selected_binary(file, revision=None):
        view = tool('ui_diff', repo=str(repo), action='state')
        return view if view.get('status') == 'ready' and view.get('file') == file and (revision is None or view.get('newRevision') == revision) else None

    for selected, file, other in [(earlier, 'z.bin', latest), (latest, 'a.bin', earlier)]:
        tool('ui_select_view', repo=str(repo), view='history')
        tool('ui_select_commit', repo=str(repo), revision=selected)
        wait(lambda: state()['detailCommit'] == selected and file in (state()['detailChanges'] or []) and not state()['detailBranchesLoading'])
        tool('ui_diff', repo=str(repo), action='select', source='history', file=file)
        wait(lambda: selected_binary(file, selected))
        branch = state()['sidebarSelectedBranch']
        tool('ui_search', repo=str(repo), query=other, method='sha')
        wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
        tool('ui_search', repo=str(repo), select=other)
        wait(lambda: 'a.bin' in (state()['detailChanges'] or []))
        tool('ui_diff', repo=str(repo), action='select', source='history', file='a.bin')
        wait(lambda: selected_binary('a.bin', other))
        tool('ui_select_view', repo=str(repo), view='history')
        wait(lambda: state()['detailCommit'] == selected and selected_binary(file, selected))
        check(file + ' returning from search restores the exact history row', state()['historySelectedCommits'] == [selected])
        check(file + ' returning from search restores its selected sidebar branch', branch is not None and state()['sidebarSelectedBranch'] == branch)
        check(file + ' returning from search restores the actual binary file and revision', bool(selected_binary(file, selected)))
        tool('ui_select_view', repo=str(repo), view='search')
        wait(lambda: state()['detailCommit'] == other and selected_binary('a.bin', other))
        check(file + ' search keeps its own selected commit and file', state()['searchSelected'] == other)
        tool('ui_select_view', repo=str(repo), view='history')

    (repo / 'z.bin').write_bytes(b'\0' + bytes(range(3, 67)))
    tool('ui_refresh', repo=str(repo)); tool('ui_working_copy', repo=str(repo))
    wait(lambda: 'z.bin' in state()['workingCopyState']['unstaged'])
    tool('ui_diff', repo=str(repo), action='select', source='unstaged', file='z.bin')
    wait(lambda: selected_binary('z.bin'))
    tool('ui_search', repo=str(repo), query=earlier, method='sha')
    wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
    tool('ui_search', repo=str(repo), select=earlier)
    tool('ui_select_view', repo=str(repo), view='history')
    wait(lambda: state()['detailView'] == 'WorkingCopy' and selected_binary('z.bin'))
    check('file status and history restore their one shared last file selection', state()['workingCopyState']['selectedUnstaged'] == ['z.bin'])
    check('page switching never changes the checked-out branch or HEAD', git('branch', '--show-current') == 'main' and git('rev-parse', 'HEAD') == latest)
except BaseException:
    failure = traceback.format_exc()
    if pid:
        try: (out / 'failure.png').write_bytes(tool('ui_screenshot'))
        except Exception: pass
        try: (out / 'failure-state.json').write_text(json.dumps(tool('ui_state'), ensure_ascii=False, indent=2), encoding='utf-8')
        except Exception: pass
    raise
finally:
    if pid:
        if os.name == 'posix':
            path = Path('/proc') / str(pid) / 'exe'
            if path.exists():
                assert path.resolve() == exe
                os.kill(pid, signal.SIGTERM)
                wait(lambda: not path.exists())
        else:
            command = f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{if($p.Path -ne '{str(exe).replace(chr(39), chr(39)*2)}'){{throw 'Unexpected test process'}};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(5000)){{$p.Kill();$p.WaitForExit()}}}}"
            subprocess.run(['powershell', '-NoProfile', '-EncodedCommand', base64.b64encode(command.encode('utf-16-le')).decode()], check=True, capture_output=True)
    if process and process.poll() is None:
        process.stdin.close()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
    if backup is not None: profile.write_bytes(backup)
    elif profile.exists(): profile.unlink()
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'records': records, 'failure': failure}, ensure_ascii=False, indent=2), encoding='utf-8')
    for attempt in range(30):
        try:
            remove_fixture(root, out, 'TreeGit search interaction ')
            break
        except PermissionError:
            if attempt == 29: raise
            time.sleep(.1)
    remove_fixture(temp, temp.parent, 'TreeGit search input temp ')
print(json.dumps({'passed': len(checks), 'failure': failure}))
