"""Verify separate search, history and original complete commit pages.

Runs only in an isolated integration application using routed button clicks.
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
from qa_cleanup import new_fixture, remove_fixture, verify_process

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
    git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit Demo'); git('config', 'user.email', 'demo@example.invalid')
    for name in ['staged.txt', 'draft.txt']: (repo / name).write_text('initial\n', encoding='utf-8')
    git('add', '.'); git('commit', '-m', 'Initial demo'); initial = git('rev-parse', 'HEAD')
    remote=root/'remote.git';git('init','--bare',str(remote));git('remote','add','origin',str(remote));git('push','-u','origin','main')
    (repo/'.git/sourcegit.uistates').write_text(json.dumps({'EnableSignOffForCommit':False}),encoding='utf-8')
    (repo / 'staged.txt').write_text('ready change\n', encoding='utf-8'); (repo / 'draft.txt').write_text('keep unstaged\n', encoding='utf-8')
    earlier_untracked = repo / 'a-untracked.txt'
    earlier_untracked.write_text('untracked before the tracked file\n', encoding='utf-8')
    process = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=(out / 'mcp.log').open('w', encoding='utf-8'), text=True, encoding='utf-8', creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    threading.Thread(target=lambda: [responses.put(v) for v in process.stdout], daemon=True).start()
    rpc('initialize', {'protocolVersion': '2025-11-25'}); pid = tool('ui_state')['processId']; verify_process(pid, exe)
    tool('ui_open_repository', repo=str(repo)); tool('ui_select_view', repo=str(repo), view='history')
    wait(lambda: state()['workingCopyStatusKnown'] and state()['workingCopySelected'] and not state()['graphLoading'])
    check('a dirty repository tab selects the first tracked unstaged file before an untracked file',
          state()['workingCopyState']['selectedUnstaged'] == ['draft.txt'])
    earlier_untracked.unlink()
    def composition(): return (state().get('workingCopyState') or {}).get('composition')
    def composer_visible(): return bool(composition() and composition()['visible'])
    def commit_page(): tool('ui_select_view', repo=str(repo), view='commit'); wait(composer_visible)
    def draft(message): tool('ui_working_copy', repo=str(repo), message=message); commit_page()
    def stage_buttons():
        names = ['StageAllChanges', 'StageSelectedChanges', 'UnstageAllChanges', 'UnstageSelectedChanges']
        found = {button['name']: button['enabled'] for button in tool('ui_layout')['buttons'] if button['name'] in names}
        return tuple(found.get(name) for name in names)
    def folded_geometry():
        split = next(s for s in tool('ui_layout')['splitters'] if s['owner'] == 'Histories' and s['row'] == 1 and s['visible'])
        return split['actualRows'][2]
    probe('history_pending')
    wait(lambda: state()['workingCopySelected'])
    check('clicking uncommitted changes stays in history and only shows files/diffs', state()['view'] == 0 and not composer_visible() and state()['detailView'] == 'WorkingCopy')
    tool('ui_diff', repo=str(repo), action='select', source='unstaged', file='draft.txt')
    for locale, label, tabs in [('zh_CN', '无暂存文件', ['搜索', '日志 / 历史', '提交']),
            ('zh_TW', '無暫存檔案', ['搜尋', '記錄／歷史', '提交']), ('en_US', 'No staged files', ['Search', 'Log / History', 'Commit'])]:
        tool('ui_window', locale=locale, width=1280, height=820, zoom=1)
        # Exercise the actual history toggle and bottom tab handlers, not just VM navigation.
        probe('page_tab', 1); probe('history_pending'); probe('history_collapse', 1)
        wait(lambda: state()['detailsCollapsed'])
        check(locale + ' collapsed uncommitted details occupy only their header', folded_geometry() <= 29 and not composer_visible() and not any(b['name']=='StageAllChanges' for b in tool('ui_layout')['buttons']))
        if locale == 'zh_CN': (out/'history-collapsed.png').write_bytes(tool('ui_screenshot'))
        probe('page_tab', 0)
        tool('ui_search', repo=str(repo), query='Initial demo', method='message')
        wait(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
        tool('ui_search', repo=str(repo), select=initial); probe('page_tab', 1)
        wait(lambda: state()['detailsCollapsed'] and folded_geometry() <= 29)
        time.sleep(.25)
        check(locale + ' search round-trip keeps details folded and the file selected', state()['detailsCollapsed'] and state()['workingCopySelected'] and state()['workingCopyState']['selectedUnstaged'] == ['draft.txt'] and folded_geometry() <= 29)
        tool('ui_refresh', repo=str(repo)); wait(lambda: not state()['graphLoading'])
        check(locale + ' refresh does not unfold cached history details', state()['detailsCollapsed'] and folded_geometry() <= 29)
        probe('page_tab', 2); wait(composer_visible)
        check(locale + ' tab order is search, history, commit', [t['text'] for t in tool('ui_layout')['bottomTabs']] == tabs)
        check(locale + ' commit tab opens the complete original page', state()['view'] == 1 and tool('ui_layout')['bottomTabs'][2]['active'])
        for theme in ['Light', 'Dark', 'Blue']:
            tool('ui_window', theme=theme)
            wait(lambda: composition() and composition()['maskText'] == label)
            c=composition(); check(locale + ' ' + theme + ' empty-index editor and commit are disabled', not c['inputEnabled'] and not c['commitEnabled'] and c['maskVisible'], c)
        probe('page_tab', 1); wait(lambda: state()['view'] == 0)
        check(locale + ' opening commit does not change the collapsed history layout', state()['detailsCollapsed'] and folded_geometry() <= 29 and not composer_visible())
        probe('history_collapse', 0)
    tool('ui_window', locale='zh_CN', theme='Light')
    probe('commit_toolbar'); wait(composer_visible)
    wait(lambda: composition() and composition()['identity']=='TreeGit Demo <demo@example.invalid>' and composition()['pushTarget']=='origin/main')
    check('commit header shows the actual repository name and email', composition()['identity']=='TreeGit Demo <demo@example.invalid>')
    check('top-left commit button opens the same complete commit page', state()['view'] == 1 and tool('ui_layout')['bottomTabs'][2]['active'])
    draft('Preserved commit draft')
    check('draft cannot enable an empty-index commit', not composition()['commitEnabled'] and git('rev-parse', 'HEAD') == initial)
    tool('ui_diff', repo=str(repo), action='select', source='unstaged', file='draft.txt'); commit_page()
    wait(lambda: stage_buttons() == (True, True, False, False))
    check('empty index disables both unstage controls', stage_buttons() == (True, True, False, False))
    git('add', 'staged.txt'); tool('ui_refresh', repo=str(repo)); wait(lambda: composition() and composition()['commitEnabled'])
    check('staging enables editor and commit without losing the draft', composition()['inputEnabled'] and not composition()['maskVisible'] and composition()['inputText'] == 'Preserved commit draft')
    tool('ui_diff', repo=str(repo), action='select', source='unstaged', file='draft.txt'); commit_page()
    wait(lambda: stage_buttons() == (True, True, True, False))
    check('unselected staged files enable only unstage all', stage_buttons() == (True, True, True, False))
    tool('ui_diff', repo=str(repo), action='select', source='staged', file='staged.txt'); commit_page()
    wait(lambda: stage_buttons() == (True, False, True, True))
    check('unselected unstaged files enable only stage all', stage_buttons() == (True, False, True, True))
    git('add', 'draft.txt'); tool('ui_refresh', repo=str(repo))
    wait(lambda: stage_buttons() == (False, False, True, True))
    check('empty unstaged list disables both stage controls', stage_buttons() == (False, False, True, True))
    git('reset', '--', 'draft.txt'); tool('ui_refresh', repo=str(repo))
    git('reset', '--', 'staged.txt'); tool('ui_refresh', repo=str(repo)); wait(lambda: composition() and composition()['maskVisible'])
    check('unstaging restores the mask while preserving the draft', not composition()['inputEnabled'] and not composition()['commitEnabled'] and composition()['inputText'] == 'Preserved commit draft')
    git('add', 'staged.txt'); tool('ui_refresh', repo=str(repo)); wait(lambda: composition() and composition()['commitEnabled'])
    probe('page_tab',0);probe('commit_hidden');time.sleep(.15)
    check('hidden commit controls cannot submit from the search page',git('rev-parse','HEAD')==initial)
    probe('page_tab',2);wait(composer_visible)
    for locale in ['zh_CN', 'zh_TW', 'en_US']:
        tool('ui_window', locale=locale, width=1024, height=720, zoom=1)
        layout = tool('ui_layout')
        # The compact pickers also render transparent labels to measure the longest option.
        choice_texts = [t for t in layout['localizedTexts']
                        if t['context'] == 'ChangeListOption' and t['trimming'] == 'CharacterEllipsis']
        records.append({'locale': locale, 'dropdownText': choice_texts})
        button = next(b for b in layout['buttons'] if b['name'] == 'CommitStagedButton')['bounds']
        check(locale+' recessed separators keep equal nonzero side spacing',len(layout['commitSeparators'])==2 and all(c['columns'][0]>0 and abs(c['columns'][0]-c['columns'][2])<.1 and c['columns'][1]==3 for c in layout['commitSeparators']))
        check(locale + ' dropdown labels fit their actual rendered bounds', len(choice_texts)==2 and all(t['textWidth'] <= t['width']+.75 and t['textHeight'] <= t['height']+.75 for t in choice_texts), choice_texts)
        check(locale+' footer keeps sign-off visible', any(t['text'] == next(e.text for e in __import__('xml.etree.ElementTree',fromlist=['parse']).parse(Path(__file__).resolve().parents[1]/('src/Resources/Locales/'+locale+'.axaml')).getroot() if e.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Key')=='Text.WorkingCopy.SignOff') for t in layout['localizedTexts']))
        check(locale + ' commit button fits above the bottom tabs', button['x'] >= 0 and button['x']+button['width'] <= layout['window']['width'] and button['y']+button['height'] <= min(t['bounds']['y'] for t in layout['bottomTabs']), button)
        (out / (locale + '-complete-commit-page.png')).write_bytes(tool('ui_screenshot'))

    for locale, hint, templates_label in [('zh_CN','请输出提交说明...','提交模板'),
                                          ('zh_TW','請輸出提交說明...','提交範本'),
                                          ('en_US','Enter a commit message...','Commit templates')]:
        tool('ui_window', locale=locale, theme='Light', width=1280, height=820)
        draft('')
        wait(composer_visible)
        layout = wait(lambda: (g if any(t['text']==hint for t in g['localizedTexts']) else None) if (g:=tool('ui_layout')) else None)
        check(locale+' empty composer shows only the short localized hint', sum(t['text']==hint for t in layout['localizedTexts'])==1 and not composition()['commitEnabled'])
        (out/(locale+'-empty-draft-hint.png')).write_bytes(tool('ui_screenshot'))
        expected_headers = {
            'zh_CN': ['文件(F)', '操作(A)', '设置(S)', '工具(T)', '帮助(H)'],
            'zh_TW': ['檔案(F)', '操作(A)', '設定(S)', '工具(T)', '說明(H)'],
            'en_US': ['File (F)', 'Actions (A)', 'Settings (S)', 'Tools (T)', 'Help (H)'],
        }
        check(locale+' top bar places localized Settings before Tools',
              [item['text'] for item in probe()['headers']]==expected_headers[locale])
        for index in range(5):
            probe('reset');probe('enter',index);probe('press',index);probe('release',index)
            check(locale+' outside content closes menu '+str(index),not probe('outside_content')['menuOpen'])

        tool('ui_command',command='new_tab')
        wait(lambda:tool('ui_state')['activePage']['kind']=='Welcome')
        probe('enter',1);probe('press',1);probe('release',1)
        menus=probe()['headers']
        check(locale+' welcome disables all repository operations',all(not item['enabled'] for item in menus[1]['items']),menus[1]['items'])
        (out/(locale+'-welcome-disabled-menu.png')).write_bytes(tool('ui_screenshot'))
        tools_menu=probe('enter',3)['headers'][3]
        check(locale+' welcome keeps integration, MCP and SSH tools usable',
              len(tools_menu['items'])==3 and all(item['enabled'] for item in tools_menu['items']),tools_menu['items'])
        probe('outside_content');tool('ui_command',command='close_tab');tool('ui_open_repository',repo=str(repo))
        wait(lambda:tool('ui_state')['activePage']['kind']=='Repository')
        probe('enter',1);probe('press',1);probe('release',1)
        check(locale+' switching back restores repository menu operations', all(item['enabled'] for item in probe()['headers'][1]['items']))
        probe('outside_content')

        tool('ui_command',repo=str(repo),command='repository_settings')
        wait(lambda:any(dialog['type']=='RepositorySettings' for dialog in tool('ui_state')['dialogs']))
        tool('ui_command',command='repository_settings_advanced')
        for name in ['CommitLinks','SyncMerge','Signing','SubmoduleUpdate']:
            tool('ui_layout',window='active',action='test_set',button='TreeGitSettings'+name,value=1)
        layout=tool('ui_layout',window='active');records.append({'advancedLocale':locale,'layout':layout})
        ignored=next(t['text'] for t in layout['localizedTexts'] if t['name']=='RepositoryIgnorePath')
        check(locale+' ignore path uses native separators',ignored == str(repo / '.gitignore'),ignored)
        bars=[b for b in layout['scrollBars'] if b['visible'] and b['orientation']=='Vertical']
        bar=max(bars,key=lambda b:b['bounds']['x'])
        # Scrollbar paging buttons belong in the bar; inspect labeled settings
        # actions rather than treating scrollbar internals as overlapping rows.
        actions=[b for b in layout['buttons'] if b['inScrollArea'] and b['text']]
        (out/(locale+'-advanced-scrollbar.png')).write_bytes(tool('ui_screenshot',window='active'))
        check(locale+' advanced controls keep clear of the outer scrollbar',bool(actions) and all(b['bounds']['x']+b['bounds']['width']<=bar['bounds']['x']-1 for b in actions),{'actions':actions,'bar':bar})
        check(locale+' unused template editor is absent',not any(t['text']==templates_label for t in layout['localizedTexts']))
        (out/(locale+'-advanced-scrollbar.png')).write_bytes(tool('ui_screenshot',window='active'))
        tool('ui_command',command='close_dialog')

        tool('ui_select_commit',repo=str(repo),revision=initial)
        wait(lambda:state()['detailCommit']==initial)
        if state()['detailsCollapsed']:probe('history_collapse')
        wait(lambda:not state()['detailsCollapsed'])
        wait(lambda:any(t['open'] for t in probe('detail_tooltip',0)['detailTooltips']))
        check(locale+' detail tooltip is above its noninteractive button',all(t['placement']=='Top' and t['interactive'] is False for t in probe()['detailTooltips']))
        probe('history_collapse',1);wait(lambda:state()['detailsCollapsed'])
        check(locale+' folding closes both detail tooltips',not any(t['open'] for t in probe()['detailTooltips']))
        wait(lambda:any(t['open'] for t in probe('detail_tooltip',1)['detailTooltips']))
        (out/(locale+'-folded-detail-tooltip.png')).write_bytes(tool('ui_screenshot'))
        probe('history_collapse');wait(lambda:not state()['detailsCollapsed'])
        check(locale+' unfolding closes tooltips again',not any(t['open'] for t in probe()['detailTooltips']))
    draft('Preserved commit draft')
    tool('ui_window', locale='zh_CN', width=1280, height=820)
    probe('commit_staged'); wait(lambda: git('rev-parse', 'HEAD') != initial and composition() and not state()['workingCopyState']['committing'])
    check('actual commit button commits only the staged file', git('diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD') == 'staged.txt')
    check('commit preserves unstaged content', git('diff', '--name-only') == 'draft.txt' and (repo/'draft.txt').read_text() == 'keep unstaged\n')
    check('commit preserves the prepared message', git('log', '-1', '--format=%s') == 'Preserved commit draft')
    wait(lambda: composition() and composition()['maskVisible'])
    check('successful commit restores disabled empty-index state', not composition()['inputEnabled'] and not composition()['commitEnabled'] and not composition()['inputText'])
    check('unchecked sign-off does not add a trailer', 'Signed-off-by:' not in git('log','-1','--format=%B'))
    check('displayed identity matches the real commit identity',git('log','-1','--format=%cn <%ce>')==composition()['identity'])
    check('unchecked immediate push does not change the remote',git('--git-dir='+str(remote),'rev-parse','refs/heads/main')==initial)
    (out/'commit-empty-index.png').write_bytes(tool('ui_screenshot'))
    probe('page_tab', 1); probe('history_pending'); wait(lambda: state()['workingCopySelected'])
    check('uncommitted history still has no composer after a real commit', state()['view'] == 0 and not composer_visible())
    (out/'history-pending-files.png').write_bytes(tool('ui_screenshot'))
    (repo/'staged.txt').write_text('second ready change\n',encoding='utf-8');git('add','staged.txt');tool('ui_refresh',repo=str(repo));draft('Commit and push')
    wait(lambda: composition() and composition()['commitEnabled']);probe('commit_push_after',1);probe('commit_signoff',1);(out/'footer-checked.png').write_bytes(tool('ui_screenshot'));probe('commit_staged')
    wait(lambda: not state()['workingCopyState']['committing'] and git('--git-dir='+str(remote),'rev-parse','refs/heads/main')==git('rev-parse','HEAD'))
    check('immediate push checkbox pushes the new commit to the displayed target',git('log','-1','--format=%s')=='Commit and push' and composition()['pushTarget']=='origin/main')
    check('sign-off checkbox adds the configured repository identity', 'Signed-off-by: TreeGit Demo <demo@example.invalid>' in git('log','-1','--format=%B'))
    probe('commit_push_after',0);probe('commit_signoff',0)
    repo=root/'new-repository';repo.mkdir();git('init','-b','main');git('config','user.name','TreeGit Demo');git('config','user.email','demo@example.invalid')
    (repo/'first.txt').write_text('first commit content\n',encoding='utf-8')
    tool('ui_open_repository',repo=str(repo))
    wait(lambda: state()['workingCopyStatusKnown'] and state()['workingCopySelected'])
    check('an untracked-only repository tab selects the first untracked file',
          state()['workingCopyState']['selectedUnstaged'] == ['first.txt'])
    commit_page()
    wait(lambda: composition() and composition()['maskVisible']);check('new repositories also disable the empty-index composer',not composition()['inputEnabled'] and not composition()['commitEnabled'])
    git('add','first.txt');tool('ui_refresh',repo=str(repo));draft('First UI commit 中文');wait(lambda: composition() and composition()['commitEnabled']);probe('commit_staged')
    wait(lambda: composition() and not state()['workingCopyState']['committing'] and composition()['maskVisible'])
    check('complete commit page creates the first commit',git('rev-list','--count','HEAD')=='1' and git('log','-1','--format=%s')=='First UI commit 中文')
    untracked_repo = repo
    for current_only in (False, True):
        repo = root / ('clean-current' if current_only else 'clean-all')
        repo.mkdir()
        git('init', '-b', 'main')
        git('config', 'user.name', 'TreeGit Demo')
        git('config', 'user.email', 'demo@example.invalid')
        def dated_commit(label, date):
            subprocess.run([shutil.which('git'), '-C', str(repo), 'commit', '--allow-empty', '-m', label],
                           env={**env, 'GIT_AUTHOR_DATE': date, 'GIT_COMMITTER_DATE': date},
                           capture_output=True, check=True)
            return git('rev-parse', 'HEAD')
        main = dated_commit('Main tip', '2025-01-01T12:00:00Z')
        git('checkout', '-b', 'side')
        side = dated_commit('Newer other branch tip', '2025-01-02T12:00:00Z')
        git('checkout', 'main')
        (repo / '.git/sourcegit.uistates').write_text(json.dumps({'ShowOnlyGraphBranch': current_only}), encoding='utf-8')
        tool('ui_open_repository', repo=str(repo))
        expected = main if current_only else side
        wait(lambda: state()['workingCopyStatusKnown'] and state()['historySelectedCommits'] == [expected])
        check('a clean tab selects the current tip' if current_only else 'a clean all-branches tab selects the newest commit',
              git('branch', '--show-current') == 'main' and git('rev-parse', 'HEAD') == main)
        if not current_only:
            tool('ui_select_commit', repo=str(repo), revision=main)
            wait(lambda: state()['historySelectedCommits'] == [main])
            tool('ui_open_repository', repo=str(untracked_repo))
            tool('ui_open_repository', repo=str(repo))
            check('switching repository tabs keeps a previously selected commit',
                  state()['historySelectedCommits'] == [main])
except BaseException:
    failure = traceback.format_exc()
    if pid:
        try: (out / 'failure.png').write_bytes(tool('ui_screenshot'))
        except Exception: pass
        try: (out / 'failure-menu.json').write_text(json.dumps(probe(), ensure_ascii=False, indent=2), encoding='utf-8')
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
