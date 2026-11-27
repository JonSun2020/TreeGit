"""Exercise history selection, the report dialog, and standalone HTML with an isolated repository."""

import json
import os
import re
from datetime import datetime
from pathlib import Path
import subprocess
import sys
import traceback

from dropdown_audit import App, wait
from qa_cleanup import new_fixture, remove_fixture
from qa_profile import IsolatedProfile


def main():
    exe, output = (Path(value).resolve() for value in sys.argv[1:3])
    profile = IsolatedProfile(exe, output)
    root = new_fixture(output, 'TreeGit report fixture ')
    environment = dict(os.environ, GIT_CONFIG_GLOBAL=str(root / 'global.gitconfig'),
                       GIT_CONFIG_NOSYSTEM='1', GIT_TERMINAL_PROMPT='0', GIT_DIFF_OPTS='--unified=1')
    repo = root / 'Anonymous demo'
    repo.mkdir()
    checks = []
    failure = None
    app = None

    def git(*args, directory=repo, payload=None, date=None):
        env = environment if date is None else dict(environment, GIT_AUTHOR_DATE=date, GIT_COMMITTER_DATE=date)
        return subprocess.run(['git', '-C', str(directory), *args], env=env, input=payload,
                              capture_output=True, check=True).stdout.decode('utf-8').strip()

    def check(name, condition):
        assert condition, name
        checks.append(name)
        print('PASS', name, flush=True)

    def view(path):
        app.tool('ui_open_repository', repo=str(path))
        app.tool('ui_select_view', repo=str(path), view='history')
        wait(lambda: any(Path(state['repo']) == path and state.get('graphCommits') and not state.get('graphLoading')
                         for state in app.tool('ui_state')['repositories']), 'History was not loaded')

    def branch_in_change_comparison(sha):
        app.tool('ui_select_commit', repo=str(repo), revision=sha)
        def resolved():
            state = next(item for item in app.tool('ui_state')['repositories'] if Path(item['repo']) == repo)
            return state['detailBranches'] if state['detailCommit'] == sha and not state['detailBranchesLoading'] else None
        result = wait(resolved, f'Change comparison did not resolve the branch for {sha}')
        name = result['primary']
        if name:
            name = name.removeprefix('refs/heads/').removeprefix('refs/remotes/')
        return name, result['inferred']

    def selector():
        return next((item for item in app.dropdown('list') if item['Name'] == 'CommitReportContext'), None)

    def dropdown(action, option=0):
        for _ in range(3):
            target = wait(selector, 'Report context dropdown missing')
            result = app.dropdown(action, target['index'], option)
            if result['count'] == 3:
                return result
        raise AssertionError('Report dropdown moved while reading history details')

    def open_export(commits, name, count, menu_caption='Export commit report…'):
        result = app.tool('ui_layout', action='test_commit_report', button=name, text=','.join(commits))
        assert result['selected'] == count and result['ContextLines'] == 20 and result['title'] == menu_caption, result
        assert result['reportFirst'] and result['reportIconIsColored'], result
        headers = result['menuHeaders']
        assert not any(term in caption.casefold() for caption in headers
                       for term in ['patch', '补丁', '修補檔', '補丁', '挑选', '揀選']), headers
        target = wait(selector, 'Report context dropdown missing')
        assert target['count'] == 3, target
        current = wait(lambda: row if (row := dropdown('inspect'))['caption'] == ['20'] else None,
                       ('Default context must be 20', dropdown('inspect')))
        assert current['selected'] == 0, current
        return headers

    def choose(option, caption):
        dropdown('open')
        expected = wait(lambda: row if (row := dropdown('click', option))['ready'] else None,
                        'Report context option was not laid out')
        actual = wait(lambda: row if not (row := dropdown('inspect'))['open'] and row['caption'] == [caption] else None,
                      'The closed dropdown did not display the chosen number')
        assert actual['selected'] == option and expected['expected'] == [caption], (actual, expected)

    def click(button):
        return app.tool('ui_layout', action='test_click', button=button)

    def finish(name, commits):
        click('ConfirmPopup')
        file = root / name
        def complete():
            if not file.exists():
                return None
            data = file.read_text(encoding='utf-8-sig')
            return data if data.endswith('</script></body></html>\n') else None
        html = wait(complete, f'The HTML report was not saved: {name}')
        wait(lambda: not any(item['Name'] == 'CommitReportContext' for item in app.dropdown('list')),
             'Successful export should close the report dialog')
        for sha in commits:
            assert f'id="commit-{sha}"' in html and f'data-commit="commit-{sha}"' in html, sha
        assert [int(value) for value in re.findall(r'<td class="commit-ordinal">(\d+)</td>', html)] == list(range(1, len(commits) + 1))
        assert html.count('<script') == 1 and str(root) not in html
        suggested = (root / (name + '.suggested')).read_text(encoding='utf-8')
        assert re.fullmatch(r'main_demo_\d{8}_\d{6}\.html', suggested), suggested
        title = Path(suggested).stem
        assert f'<title>{title}</title>' in html and f'<h1 class="report-title">{title}</h1>' in html
        return html

    def section(html, sha):
        return html.split(f'id="commit-{sha}">', 1)[1].split('</article>', 1)[0]

    def branch_in_report(html, sha):
        row = html.split(f'<tr data-commit="commit-{sha}">', 1)[1].split('</tr>', 1)[0]
        details = section(html, sha).split('<dd class="commit-branches">', 1)[1].split('</dd>', 1)[0]
        return tuple(re.findall(r'<span class="branch-badge">(.*?)</span>', block) for block in (row, details))

    def has_context(html, first, last):
        return all(f'class="source">sample-{line:03}</code>' in html for line in [first, last]) and \
            all(f'class="source">sample-{line:03}</code>' not in html for line in [first - 1, last + 1])

    def fingerprint():
        return (git('show-ref'), git('status', '--porcelain'), (repo / '.git' / 'index').read_bytes(),
                (repo / 'sensor.txt').read_bytes())

    try:
        git('init', '-q', '-b', 'main/demo')
        git('config', 'user.name', 'Anonymous QA & Test')
        git('config', 'user.email', 'anonymous@example.invalid')
        git('config', 'commit.gpgsign', 'false')
        git('config', 'core.autocrlf', 'false')
        git('config', 'diff.context', '2')
        git('config', 'diff.noPrefix', 'true')
        git('config', 'diff.suppressBlankEmpty', 'true')
        lines = [f'sample-{i:03}' for i in range(1, 241)]
        lines[118] = ''
        sensor = repo / 'sensor.txt'
        sensor.write_bytes(('\n'.join(lines) + '\n').encode())
        (repo / 'before & data.txt').write_bytes(b'rename only\n')
        (repo / 'record.bin').write_bytes(b'\x00\x01')
        (repo / 'removed.bin').write_bytes(b'\x00remove-me')
        (repo / 'before.bin').write_bytes(b'\x00rename-only fixture')
        git('add', '.')
        git('commit', '-qm', 'Initial fixture')
        initial = git('rev-parse', 'HEAD')

        lines[119] = '<script>fixture & text</script>'
        sensor.write_bytes(('\n'.join(lines) + '\n').encode())
        git('add', 'sensor.txt')
        git('commit', '-qm', 'Change sensor line', '-m', 'First paragraph\n\n<test> & second paragraph')
        changed = git('rev-parse', 'HEAD')
        git('switch', '-qc', 'side')
        (repo / 'side.txt').write_bytes(b'separate branch\n')
        git('add', 'side.txt')
        git('commit', '-qm', 'Side change')
        side = git('rev-parse', 'HEAD')
        git('branch', '-c', 'side', 'side-copy')
        git('branch', 'ff-copy', changed)
        git('switch', '-q', 'ff-copy')
        git('cherry-pick', '--ff', side)
        git('switch', '-q', 'main/demo')
        git('mv', 'before & data.txt', '\u53d8\u66f4 & data.txt')
        (repo / 'record.bin').write_bytes(b'\x00\x02')
        (repo / 'added.bin').write_bytes(b'\x00add!')
        git('rm', '-q', 'removed.bin')
        git('mv', 'before.bin', 'after.bin')
        git('add', 'record.bin', 'added.bin')
        git('commit', '-qm', 'Rename and update binary')
        renamed = git('rev-parse', 'HEAD')
        git('merge', '-q', '--no-ff', '-m', 'Join side branch', 'side')
        merged = git('rev-parse', 'HEAD')
        oid = git('hash-object', '-w', '--stdin', payload=b'target.txt')
        git('update-index', '--cacheinfo', f'120000,{oid},\u53d8\u66f4 & data.txt')
        git('commit', '-qm', 'Change file type')
        typed = git('rev-parse', 'HEAD')
        git('commit', '--allow-empty', '-qm', 'Empty checkpoint')
        empty = git('rev-parse', 'HEAD')

        shallow = root / 'Shallow demo'
        git('clone', '-q', '--depth=1', repo.as_uri(), str(shallow))
        git('switch', '-q', '--discard-changes', '-c', 'review/acquisition', changed)
        (repo / 'firmware').mkdir()
        controller = repo / 'firmware' / 'sampler.py'
        tail = '''

def stable(samples, tolerance):
    if not samples:
        return False
    return max(samples) - min(samples) <= tolerance


def read_sample(sensor):
    value, valid = sensor.read()
    if not valid:
        return None
    return value


def latest(batch):
    return batch[-1] if batch else None
'''
        controller.write_text('from collections import deque\n\nMAX_SAMPLES = 24\n\n\ndef calibrate(samples):\n    return sum(samples) / len(samples)\n' + tail, encoding='utf-8')
        settings = repo / 'firmware' / 'settings.toml'
        settings.write_text('[acquisition]\nrate_hz = 120\nwindow_ms = 8\n', encoding='utf-8')
        git('add', 'firmware')
        git('commit', '-qm', 'Add acquisition fixture')
        controller.write_text('from collections import deque\n\nMAX_SAMPLES = 40\n\n\ndef calibrate(samples):\n    return sum(samples) / max(len(samples), 1)\n\n\ndef recent():\n    return deque(maxlen=MAX_SAMPLES)\n' + tail, encoding='utf-8')
        settings.write_text('[acquisition]\nrate_hz = 240\nwindow_ms = 8\n# ' + 'fixture ' * 65 + '\n', encoding='utf-8')
        git('add', 'firmware')
        git('commit', '-qm', 'Tune sensor acquisition', '--author=Review Demo <review@example.invalid>',
            '-m', 'Increase the sample window and guard calibration against an empty sample set.')
        preview = git('rev-parse', 'HEAD')
        git('branch', '-m', 'review/renamed')
        imported = git('commit-tree', git('rev-parse', preview + '^{tree}'), '-p', preview, '-m', 'Imported checkpoint',
                       date='2001-01-01T00:00:00+00:00')
        git('branch', 'imported', imported)
        remote_only = git('commit-tree', git('rev-parse', initial + '^{tree}'), '-p', initial, '-m', 'Remote-only checkpoint',
                          date='2002-01-01T00:00:00+00:00')
        git('update-ref', 'refs/remotes/origin/diagnostics', remote_only)
        git('switch', '-q', 'main/demo')
        (root / 'existing.html').write_text('existing content', encoding='utf-8')
        sensor.write_bytes(sensor.read_bytes() + b'unstaged fixture\n')
        before = fingerprint()

        (profile.data / 'preference.json').write_text(json.dumps({'Locale': 'en_US', 'UseBundledGit': True,
                                                                  'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False}), encoding='utf-8')
        app = App(exe, environment)
        app.initialize()
        view(repo)

        single_menu = open_export([changed], 'default.html', 1)
        html = finish('default.html', [changed])
        check('default 20 lines and both sides of the hunk', has_context(html, 100, 140) and 'class="line add"' in html and
              'class="line remove"' in html and html.count('class="line ') == 42)
        check('full message, author, date, SHA and escaped diff text', 'Anonymous QA &amp; Test' in html and
              '<time ' in html and '&lt;test&gt; &amp; second paragraph' in html and
              '&lt;script&gt;fixture &amp; text&lt;/script&gt;' in html and
              f'<code class="sha" title="SHA">{changed}</code>' in html and initial in section(html, changed))
        check('self-contained HTML has a file tree and one hash-authorized script', '<style>' in html and
              'script-src \'sha256-' in html and f'href="#file-{changed}-0"' in html and f'id="file-{changed}-0"' in html)

        selection = [typed, initial, merged, changed, renamed]
        open_export(selection, 'multiple.html', 5)
        choose(1, '50')
        html = finish('multiple.html', selection)
        check('noncontiguous selection is numbered newest first in the commit picker', [sha for sha in
              (typed, merged, renamed, changed, initial)] == sorted(selection, key=lambda sha: html.index(f'data-commit="commit-{sha}"')) and
              f'id="commit-{side}"' not in html and f'id="commit-{empty}"' not in html)
        change = section(html, changed)
        check('50 context lines apply to each commit independently', has_context(change, 70, 170) and
              all(word not in change for word in ['separate branch', 'before &amp; data.txt']))
        check('root uses the empty tree and the merge uses its first parent',
              'Empty tree (initial commit)' in section(html, initial) and 'Compared with first parent' in section(html, merged) and
              renamed in section(html, merged) and side in section(html, merged) and 'side.txt' in section(html, merged) and
              'sensor.txt' not in section(html, merged))
        check('rename and Unicode path retain exact names in a mixed text and binary report',
              'before &amp; data.txt \u2192 \u53d8\u66f4 &amp; data.txt' in section(html, renamed) and
              'Binary file' in section(html, renamed) and 'record.bin' in section(html, renamed))
        binary_masks = re.findall(r'<section class="diff-file binary-file"[^>]+>(.*?)</section>', section(html, renamed), re.S)
        check('modified, added, deleted and renamed binary entries each show only the binary mask',
              len(binary_masks) == 4 and
              all(mask.strip() == '<div class="binary-mask"><span>Binary file</span></div>' for mask in binary_masks))
        check('two git patches for a type change stay together',
              section(html, typed).count('class="diff-file"') == 1 and 'deleted file mode 100644' in section(html, typed) and
              'new file mode 120000' in section(html, typed))

        open_export([changed], 'wide.html', 1)
        choose(2, '100')
        html = finish('wide.html', [changed])
        check('100 selected lines control the exported patch', has_context(html, 20, 220))

        open_export([empty], 'empty.html', 1)
        html = finish('empty.html', [empty])
        check('an empty commit has a metadata section and no phantom files', 'No file changes.' in html and '<details ' not in html)

        browser_commits = [preview, changed, empty, typed, merged, renamed, side, initial]
        shown_branches = {sha: branch_in_change_comparison(sha)[0] for sha in browser_commits}
        open_export(browser_commits, 'browser.html', len(browser_commits))
        html = finish('browser.html', browser_commits)
        review = section(html, preview)
        preview_row = html.split(f'<tr data-commit="commit-{preview}">', 1)[1].split('</tr>', 1)[0]
        preview_date = datetime.fromtimestamp(int(git('show', '-s', '--format=%ct', preview))).strftime('%Y/%m/%d %H:%M:%S')
        check('commit metadata and directory tree flank the file comparison', 'class="folder" open' in review and
              'Tune sensor acquisition' in review and 'class="commit-meta"' in review and 'aria-label="Changed files"' in review and
              '<dt>Context lines</dt><dd>20</dd>' in review and preview_date in preview_row and
              '<td title="Review Demo">Review Demo</td>' in preview_row and
              f'<code title="{preview}">{preview[:10]}</code>' in preview_row and
              'Old version' in review and 'New version' in review and
              '<span class="added" title="Added lines">+6</span>' in review and
              '<span class="removed" title="Removed lines">\u22122</span>' in review and
              'class="line add"' in review and 'class="line remove"' in review and '<code class="source">index ' not in review)
        check('multi-commit branch column and metadata match the actual change comparison after merge, copy and rename',
              shown_branches[preview] == 'review/renamed' and all(name and branch_in_report(html, sha) == ([name], [name])
                                                              for sha, name in shown_branches.items()))

        imported_name, inferred = branch_in_change_comparison(imported)
        multi_menu = open_export([imported, preview], 'unrecorded.html', 2)
        unknown = finish('unrecorded.html', [imported, preview])
        check('single and multi-commit menus use the same cherry-pick label and keep the HTML report',
              all('Cherry-pick…' in headers and 'Export commit report…' in headers for headers in (single_menu, multi_menu)))
        check('the newest commit is numbered 1 even if an older commit is its graph descendant',
              f'<tr data-commit="commit-{preview}"><td class="commit-ordinal">1</td>' in unknown and
              f'<tr data-commit="commit-{imported}"><td class="commit-ordinal">2</td>' in unknown)
        check('the report displays the same inferred branch as the change comparison when the reflog has no creation record',
              inferred and imported_name == 'imported' and branch_in_report(unknown, imported) == (['imported'], ['imported']))

        remote_name, remote_inferred = branch_in_change_comparison(remote_only)
        open_export([remote_only], 'remote.html', 1)
        remote_report = finish('remote.html', [remote_only])
        check('single-commit export matches the remote branch displayed by the change comparison',
              remote_inferred and remote_name == 'origin/diagnostics' and
              branch_in_report(remote_report, remote_only) == ([remote_name], [remote_name]))

        for locale, menu, heading in [
                ('zh_CN', '导出提交报告…', '变更文件'), ('zh_TW', '匯出提交報告…', '變更檔案')]:
            app.tool('ui_window', locale=locale)
            single_headers = open_export([changed], 'cancel', 1, menu)
            click('CancelPopup')
            name = f'localized-{locale}.html'
            headers = open_export([changed, renamed], name, 2, menu)
            (root / name).write_text('overwritten fixture', encoding='utf-8')
            html = finish(name, [changed, renamed])
            context_label = '上下文行数' if locale == 'zh_CN' else '上下文行數'
            binary_label = '二进制文件' if locale == 'zh_CN' else '二進位檔案'
            cherry_label = '遴选…' if locale == 'zh_CN' else '遴選…'
            check(f'{locale} localizes the menu and report', f'lang="{locale.replace("_", "-")}"' in html and heading in html and
                  cherry_label in single_headers and cherry_label in headers and
                  all(f'<span class="column-title">{caption}</span>' in html for caption in
                      ['序号' if locale == 'zh_CN' else '序號', '分支', '描述', '日期', '作者', '提交']) and
                  f'<dt>{context_label}</dt><dd>20</dd>' in html and
                  f'<div class="binary-mask"><span>{binary_label}</span></div>' in html)
        app.tool('ui_window', locale='en_US')

        open_export([changed], 'cancel', 1)
        choose(1, '50')
        click('ConfirmPopup')
        current = dropdown('inspect')
        check('cancelling the file picker preserves the selected context', current['selected'] == 1 and current['caption'] == ['50'])
        click('CancelPopup')
        check('export leaves HEAD, refs, index and worktree untouched', fingerprint() == before)

        view(shallow)
        open_export([empty], 'existing.html', 1)
        notifications = app.tool('ui_state')['activePage']['notifications']
        click('ConfirmPopup')
        wait(lambda: app.tool('ui_state')['activePage']['notifications'] > notifications and
             any(item['Name'] == 'CommitReportContext' and item['enabled'] for item in app.dropdown('list')),
             'Failed export should return to its choices')
        check('a missing shallow parent never replaces an existing report or leaves a temporary file',
              (root / 'existing.html').read_text(encoding='utf-8') == 'existing content' and not list(root.glob('.treegit-report-*.tmp')))
        click('CancelPopup')
        if browser := os.environ.get('TREEGIT_REPORT_CHROME'):
            app.close()
            app = None
            run = subprocess.run(['node', str(Path(__file__).with_name('commit_report_browser.mjs')), browser,
                                  str(root / 'browser.html'), str(root / 'browser-profile'),
                                  str(output / 'commit-report-preview.png')], capture_output=True, encoding='utf-8', timeout=75)
            print(run.stdout + run.stderr, flush=True)
            check('Chromium verified the five-row table, pinned headers, column drag and offline comparison', run.returncode == 0)
    except Exception:
        failure = traceback.format_exc()
        print(failure, flush=True)
    finally:
        try:
            if app is not None:
                app.close()
            profile.close()
            remove_fixture(root, output, 'TreeGit report fixture ')
        finally:
            (output / 'commit-report.json').write_text(
                json.dumps({'checks': checks, 'completed': len(checks), 'failure': failure, 'fixtureRemoved': not root.exists()}, indent=2),
                encoding='utf-8')
    return 1 if failure else 0


if __name__ == '__main__':
    sys.exit(main())
