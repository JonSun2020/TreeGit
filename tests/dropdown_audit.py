"""Check popup choice, bound selection, and collapsed text in an isolated TreeGit."""

import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import traceback

from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile


LABELS = {
    'zh_CN': ('提交指纹', '作者', '提交信息', '路径', '变更内容'),
    'zh_TW': ('提交編號', '作者', '提交訊息', '路徑', '變更內容'),
    'en_US': ('SHA', 'Author', 'Message', 'Path', 'Content'),
}


class App:
    def __init__(self, executable, environment):
        self.process = subprocess.Popen([str(executable), '--mcp'], env=environment,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        self.executable = executable
        self.responses = queue.Queue()
        self.errors = []
        self.sequence = 0
        self.pid = None
        threading.Thread(target=self.read_responses, daemon=True).start()
        threading.Thread(target=self.read_errors, daemon=True).start()

    def read_responses(self):
        for line in self.process.stdout:
            self.responses.put(line)
        self.responses.put(None)

    def read_errors(self):
        for line in self.process.stderr:
            self.errors.append(line[-1500:])

    def initialize(self):
        self.rpc('initialize', {'protocolVersion': '2025-11-25'})
        self.pid = self.tool('ui_state')['processId']
        verify_process(self.pid, self.executable)

    def rpc(self, method, parameters):
        self.sequence += 1
        request = {'jsonrpc': '2.0', 'id': self.sequence, 'method': method, 'params': parameters}
        self.process.stdin.write(json.dumps(request) + '\n')
        self.process.stdin.flush()
        line = self.responses.get(timeout=40)
        assert line is not None, self.errors[-5:]
        result = json.loads(line)
        assert result.get('id') == self.sequence and 'error' not in result, result
        return result['result']

    def tool(self, tool, **arguments):
        result = self.rpc('tools/call', {'name': tool, 'arguments': arguments})
        assert not result.get('isError'), (tool, arguments, result)
        return json.loads(result['content'][0]['text'])

    def dropdown(self, action, index=0, value=0, window='main'):
        result = self.tool('ui_layout', action='test_dropdowns', text=action, button=str(index), value=value, window=window)
        return result['pickers'] if action == 'list' else result

    def close(self):
        try:
            if self.pid:
                close_owned_gui(self.executable, self.pid)
        finally:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.terminate()
                self.process.wait(timeout=5)


def wait(predicate, context, timeout=15):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        result = predicate()
        if result:
            return result
        time.sleep(.06)
    raise AssertionError(context)


def main():
    exe, output = (Path(value).resolve() for value in sys.argv[1:3])
    output.mkdir(parents=True, exist_ok=True)
    root = new_fixture(output, 'TreeGit dropdown fixture ')
    isolated = IsolatedProfile(exe, output)
    repo = root / 'Demo'
    repo.mkdir()
    environment = dict(os.environ, GIT_CONFIG_GLOBAL=str(root / 'gitconfig'), GIT_CONFIG_NOSYSTEM='1',
                       GIT_TERMINAL_PROMPT='0', TREEGIT_TEST_MENU_PROBE='1', NO_PROXY='127.0.0.1,localhost')
    app = None
    checks, selections, dialog_failures = [], [], []
    failure = None

    def git(*args):
        return subprocess.check_output(['git', '-C', str(repo), *args], env=environment,
                                       stderr=subprocess.PIPE, encoding='utf-8').strip()

    def tool(name, **args):
        return app.tool(name, repo=str(repo), **args)

    def state():
        return next((item for item in app.tool('ui_state')['repositories'] if Path(item['repo']) == repo), {})

    def checked(name, assertion, detail=None):
        assert assertion, (name, detail)
        checks.append(name)
        print('PASS', name, flush=True)

    def picker(name, window='main'):
        return wait(lambda: next((item for item in app.dropdown('list', window=window) if item['Name'] == name), None), name)

    def choose(control, option, window='main'):
        index = control['index']
        identity = control['Name'] if control['Name'] and control['type'] != 'BranchSelector' else f"{control['view']}[{index}]"
        app.dropdown('open', index, window=window)
        picked = wait(lambda: result if (result := app.dropdown('click', index, option, window))['ready'] else None,
                      (identity, option, 'popup option was not laid out'))
        settled = wait(lambda: result if not (result := app.dropdown('inspect', index, window=window))['open'] and result['caption'] else None,
                       (identity, option, 'dropdown did not close with a visible caption'))
        assert settled['selected'] == option, (identity, option, picked, settled)
        assert settled['caption'][0] == picked['expected'][0] and set(settled['caption']) <= set(picked['expected']), (identity, option, picked, settled)
        selections.append({'view': control['view'], 'control': identity, 'type': control['type'], 'option': option,
                           'caption': settled['caption'], 'popupCaption': picked['expected']})
        return settled

    def visit(label, window='main', only=None):
        controls = app.dropdown('list', window=window)
        if only:
            controls = [item for item in controls if item['Name'] in only]
        usable = [item for item in controls if item['enabled'] and item['count'] >= 2]
        checked(f'{label} exposes selectable dropdowns', usable, controls)
        for control in usable:
            identity = control['Name'] or f"{control['view']}[{control['index']}]"
            for option in option_indices(control):
                choose(control, option, window)
            checked(f'{label} {identity} shows its chosen option when closed', True)

    def option_indices(control):
        count = control['count']
        return range(count) if count <= 12 else (0, 1, count - 1)

    def dialog(label, command, separate=False, **args):
        window = 'active' if separate else 'main'
        opened = False
        try:
            if command.startswith('fixture_'):
                app.dropdown(command)
            else:
                tool('ui_command', command=command, **args)
            opened = True
            if label == 'Apply':
                app.dropdown('fixture_apply_whitespace')
            elif label == 'Repository advanced settings':
                tool('ui_command', command='repository_settings_advanced')
                app.tool('ui_layout', action='test_set', window='active', button='TreeGitSettingsSyncMerge', value=1)
            wait(lambda: any(item['enabled'] and item['count'] > 1 and
                             (separate or item['view'] == label) for item in app.dropdown('list', window=window)), label)
            if separate:
                visit(label, window)
            else:
                controls = [item for item in app.dropdown('list') if item['view'] == label and item['enabled'] and item['count'] > 1]
                for control in controls:
                    for option in option_indices(control):
                        choose(control, option)
                    checked(f'{label} dropdown {control["index"]} follows the chosen option', True)
        except AssertionError as error:
            dialog_failures.append({'dialog': label, 'error': str(error), 'inventory': app.dropdown('list', window=window)})
        finally:
            if opened:
                tool('ui_command', command='close_dialog' if separate else 'cancel_popup')

    try:
        git('init', '-b', 'main')
        git('config', 'user.name', 'TreeGit Dropdown Demo')
        git('config', 'user.email', 'dropdown@example.invalid')
        (repo / 'sensor.txt').write_text('baseline\n', 'utf-8')
        git('add', '.')
        git('commit', '-m', 'Initial sensor')
        git('branch', 'feature/sensor')
        (repo / 'calibration.txt').write_text('instrument_calibration_42\n' + 'sample\n' * 20, 'utf-8')
        git('add', '.')
        baseline = git('rev-parse', 'HEAD')
        for name in ('one', 'two'):
            git('config', '-f', '.gitmodules', f'submodule.{name}.path', f'component-{name}')
            git('config', '-f', '.gitmodules', f'submodule.{name}.url', str(root / f'component-{name}.git'))
            git('update-index', '--add', '--cacheinfo', f'160000,{baseline},component-{name}')
        git('add', '.gitmodules')
        git('commit', '-m', 'Calibrate the instrument')
        head = git('rev-parse', 'HEAD')
        git('tag', 'demo-v1')
        git('branch', 'feature/second')
        for key, value in (('branch.master', 'main'), ('branch.develop', 'feature/sensor'),
                           ('prefix.feature', 'feature/'), ('prefix.release', 'release/'), ('prefix.hotfix', 'hotfix/')):
            git('config', 'gitflow.' + key, value)
        for name in ('origin', 'backup'):
            git('remote', 'add', name, f'http://127.0.0.1:0/{root.name.rsplit(" ", 1)[-1]}/{name}.git')
            git('update-ref', f'refs/remotes/{name}/main', head)
            git('update-ref', f'refs/remotes/{name}/feature/sensor', 'HEAD^')
        git('config', 'branch.main.remote', 'origin')
        git('config', 'branch.main.merge', 'refs/heads/main')
        (repo / 'sensor.txt').write_text('pending\n', 'utf-8')
        before = (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'))
        profile = isolated.data / 'preference.json'
        profile.write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light', 'Check4UpdatesOnStartup': False,
                                      'EnableAutoFetch': False}), 'utf-8')
        app = App(exe, environment)
        app.initialize()
        tool('ui_open_repository')
        wait(lambda: state().get('graphCommits'), 'repository history did not load')
        for locale, zoom, theme in [('zh_CN', 1, 'Light'), ('zh_TW', 1.25, 'Dark'), ('en_US', 1.5, 'Blue')]:
            app.tool('ui_window', locale=locale, zoom=zoom, theme=theme, state='normal', width=1180, height=760)
            tool('ui_select_view', view='search')
            search = picker('SearchMethodPicker')
            checked(f'{locale} search lists five choices', search['count'] == 5, search)
            localized = wait(lambda: row if (row := app.dropdown('inspect', search['index']))['caption'] else None,
                             'language change left the current search caption empty')
            checked(f'{locale} current search caption refreshes with the language',
                    localized['selected'] >= 0 and localized['caption'] == [LABELS[locale][localized['selected']]], localized)
            closed = [choose(search, index) for index in (2, 0, 1, 4, 3, 2, 0)]
            checked(f'{locale} every search caption follows the popup selection and search model',
                    all(item['caption'] == [LABELS[locale][index]] and item['modelSelection'] == index
                        for index, item in zip((2, 0, 1, 4, 3, 2, 0), closed)), closed)
            checked(f'{locale} search width is stable when choices change',
                    max(item['width'] for item in closed) - min(item['width'] for item in closed) < 1 and
                    28 < closed[0]['width'] < 150, closed)
        search = picker('SearchMethodPicker')
        keys = app.dropdown('inspect', search['index'])['keyboard']
        app.dropdown('key', search['index'], keys['f4'])
        wait(lambda: app.dropdown('inspect', search['index'])['open'], 'F4 should open search type')
        app.dropdown('key', search['index'], keys['down'])
        app.dropdown('key', search['index'], keys['enter'])
        keyboard = wait(lambda: row if not (row := app.dropdown('inspect', search['index']))['open'] else None,
                        'Enter should close search type')
        checked('keyboard selection updates search type and collapsed caption',
                keyboard['selected'] == keyboard['modelSelection'] == 1 and keyboard['caption'] == ['Author'], keyboard)
        app.dropdown('key', search['index'], keys['f4'])
        wait(lambda: app.dropdown('inspect', search['index'])['open'], 'F4 should reopen search type')
        escaped = app.dropdown('key', search['index'], keys['escape'])
        checked('Escape closes without changing the selected search type',
                not escaped['open'] and escaped['selected'] == 1 and escaped['caption'] == ['Author'], escaped)
        for index, query in enumerate((head, 'TreeGit Dropdown Demo', 'Calibrate the instrument', 'calibration.txt', 'instrument_calibration_42')):
            tool('ui_search', query='')
            choose(search, index)
            tool('ui_search', query=query)
            result = wait(lambda: row if not (row := state()).get('searchQuerying') and row.get('searchResults') else None,
                          ('search never returned a result', index))
            checked(f'query uses dropdown-selected search method {index}', head in {row['sha'] for row in result['searchResults']}, result)
        tool('ui_search', query='')
        if '--search-only' not in sys.argv[3:]:
            app.tool('ui_window', zoom=1, theme='Light', width=1340, height=880)
            tool('ui_select_view', view='history')
            visit('History toolbar', only={'HistoryOrderPicker', 'HistoryBranchPicker'})
            wait(lambda: not state().get('graphLoading') and state().get('graphCommits'), 'history search finished switching graph modes')
            tool('ui_select_commit', revision=head)
            wait(lambda: 'calibration.txt' in (state().get('detailChanges') or []), 'history change list did not load')
            tool('ui_diff', action='select', source='history', file='calibration.txt')
            picker('DiffEncodingPicker')
            visit('History comparison', only={'DiffEncodingPicker'})
            app.tool('ui_layout', action='test_menu', text='diff_context_menu_main', value=1)
            try:
                picker('DiffContextPicker')
                visit('Comparison display options', only={'DiffContextPicker'})
            finally:
                app.tool('ui_layout', action='test_menu', text='diff_context_menu_main', value=0)
            wait(lambda: not any(item['Name'] == 'DiffContextPicker' for item in app.dropdown('list')), 'comparison options stayed open')
            tool('ui_select_view', view='commit')
            picker('ChangeSortOrderBox')
            visit('Working copy toolbar', only={'ChangeSortOrderBox', 'ChangeStatusFilterBox'})
            tool('ui_select_view', view='history')
            for label, command, arguments in (
                ('Reset', 'reset_dialog', {}), ('Merge', 'merge_dialog', {'revision': git('rev-parse', 'feature/sensor')}),
                ('Apply', 'apply_patch', {}), ('StashChanges', 'stash_dialog', {}),
                ('Fetch', 'fetch_dialog', {}), ('Pull', 'pull_dialog', {}), ('Push', 'push_dialog', {}),
                ('LFSFetch', 'fixture_lfs_fetch', {}), ('LFSPull', 'fixture_lfs_pull', {}), ('LFSPush', 'fixture_lfs_push', {}),
                ('PushTag', 'fixture_push_tag', {}), ('SetUpstream', 'fixture_upstream', {}),
                ('GitFlowStart', 'fixture_git_flow', {}), ('MergeMultiple', 'fixture_merge_multiple', {}),
                ('AddToIgnore', 'fixture_ignore', {}), ('UpdateSubmodules', 'fixture_submodule', {}),
                ('CherryPick', 'fixture_cherry_pick', {}), ('ExecuteCustomAction', 'fixture_custom_action', {}),
            ):
                dialog(label, command, **arguments)
            dialog('Repository credentials', 'credentials', separate=True)
            dialog('Repository advanced settings', 'repository_settings', separate=True)
            dialog('Conventional commit type', 'fixture_conventional', separate=True)
            dialog('Revision file encoding', 'fixture_revision_encoding', separate=True)
            dialog('Statistics branch', 'fixture_statistics', separate=True)
            dialog('SSH key type', 'fixture_ssh', separate=True)
            dialog('AI model', 'fixture_ai', separate=True)
            for page in ('general', 'appearance', 'git', 'signing', 'terminal', 'diff-merge'):
                dialog(f'Preferences {page}', 'preferences', separate=True, page=page)
            for index, label in ((0, 'Clone'), (1, 'OpenLocalRepository')):
                app.tool('ui_command', command='new_tab')
                app.tool('ui_layout', action='test_menu', text='welcome_action', value=index)
                try:
                    wait(lambda: any(item['view'] == label for item in app.dropdown('list')), label)
                    visit(label)
                finally:
                    app.tool('ui_command', command='cancel_popup')
                    app.tool('ui_command', command='close_tab')
        checked('view changes and dismissing dialogs preserve HEAD, the index and working files',
                before == (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')))
        if '--search-only' not in sys.argv[3:]:
            git('restore', '--', 'sensor.txt')
            git('checkout', '-b', 'fixture/conflict-side')
            (repo / 'sensor.txt').write_text('fixture remote\n', 'utf-8')
            git('commit', '-am', 'Remote sensor edit')
            git('checkout', 'main')
            (repo / 'sensor.txt').write_text('fixture local\n', 'utf-8')
            git('commit', '-am', 'Local sensor edit')
            merge = subprocess.run(['git', '-C', str(repo), 'merge', 'fixture/conflict-side'], env=environment,
                                   capture_output=True, text=True, encoding='utf-8')
            assert merge.returncode == 1 and git('ls-files', '--unmerged', '--', 'sensor.txt'), merge.stdout
            tool('ui_refresh')
            conflict_head = git('rev-parse', 'HEAD')
            wait(lambda: any(row['sha'] == conflict_head for row in state().get('graphCommits', [])), 'merge fixture refresh')
            conflict = ((repo / 'sensor.txt').read_bytes(), git('ls-files', '--unmerged'))
            dialog('Merge editor encoding', 'fixture_merge_editor', separate=True)
            checked('dismissing merge editor preserves the actual conflict and index',
                    conflict == ((repo / 'sensor.txt').read_bytes(), git('ls-files', '--unmerged')))
        assert not dialog_failures, dialog_failures
    except Exception:
        failure = traceback.format_exc()
        print(failure, flush=True)
    finally:
        try:
            if app:
                app.close()
        finally:
            crashes = [log.read_text('utf-8', errors='replace')[-8000:]
                       for log in sorted((isolated.data / 'crashes').glob('*.log'))[-2:]]
            if crashes and failure is None:
                failure = 'Unexpected exceptions were logged during the dropdown audit.'
            isolated.close()
            remove_fixture(root, output, 'TreeGit dropdown fixture ')
            (output / 'dropdown-results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks,
                'selections': selections, 'failure': failure, 'dialogFailures': dialog_failures, 'crashes': crashes,
                'stderr': app.errors[-8:] if app and failure else []}, ensure_ascii=False, indent=2), 'utf-8')
    if failure:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
