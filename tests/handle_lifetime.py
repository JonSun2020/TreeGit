"""Compare active and idle Windows handles after repeated repository operations."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import traceback

from branch_refresh_performance import Client, wait_for
from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_process import owned_handle_types, owned_windows_descendants, process_sample
from qa_profile import IsolatedProfile


def main():
    assert os.name == 'nt' and os.environ.get('CI') == 'true', 'Use an isolated Windows test application'
    exe, output = (Path(arg).resolve() for arg in sys.argv[1:3])
    assert sys.argv[3:] in ([], ['--mixed-only'], ['--compact'])
    mixed_only = len(sys.argv) > 3
    compact = '--compact' in sys.argv[3:]
    output.mkdir(parents=True, exist_ok=True)
    profile = IsolatedProfile(exe, output)
    root = new_fixture(output, 'TreeGit handle fixture ')
    repo, temp = root / 'Demo', root / 'snapshots'
    repo.mkdir()
    temp.mkdir()
    env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1',
           'TMP': str(temp), 'TEMP': str(temp), 'GIT_TERMINAL_PROMPT': '0'}
    git_path = shutil.which('git')
    assert git_path, 'Windows Git is required for the disposable fixture'
    samples, completed = [], {}
    child = None
    failure = None
    started = time.monotonic()

    def git(*args):
        result = subprocess.run([git_path, '-C', str(repo), *args], env=env, capture_output=True,
                                text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        assert result.returncode == 0, result.stderr
        return result.stdout.strip()

    def state():
        return child.tool('ui_state')

    def local():
        return next((entry for entry in state()['repositories'] if Path(entry['repo']) == repo), {})

    def action(name, **args):
        return child.tool(name, repo=str(repo), **args)

    def save():
        report = output / 'handle-lifetime.json'
        pending = report.with_suffix('.tmp')
        pending.write_text(json.dumps({'mixedOnly': mixed_only, 'compact': compact, 'samples': samples,
                                       'completed': completed, 'failure': failure},
                                      indent=2), 'utf-8')
        pending.replace(report)

    def sample(label):
        # Take samples out of process, including while the GUI has no MCP traffic.
        observed = process_sample(exe, child.pid)
        counts = owned_handle_types(exe, child.pid, detailed=True)
        data = {'label': label, 'seconds': round(time.monotonic() - started, 2),
                'handles': observed['HandleCount'], 'privateBytes': observed['PrivateMemorySize64'],
                'workingSet': observed['WorkingSet64'], 'cpuSeconds': observed['CpuSeconds'], 'types': counts}
        if 'idle' in label:
            data['descendants'] = owned_windows_descendants(exe, child.pid)
        samples.append(data)
        save()
        print(json.dumps(data), flush=True)

    def idle(label, seconds):
        time.sleep(seconds)
        sample(f'{label}-idle{seconds}')

    def comparison():
        action('ui_diff', action='select', source='history', file='demo.bin')
        wait_for(lambda: action('ui_diff', action='state'), lambda row: row.get('status') == 'ready')
        action('ui_command', command='external_diff')
        wait_for(state, lambda s: any(dialog['type'] == 'InternalDiffWindow' for dialog in s['dialogs']))
        wait_for(lambda: child.tool('ui_layout', action='list', window='active'),
                 lambda s: any(pane['type'] == 'BinaryDiffView' for pane in s['panes']))
        result = wait_for(lambda: action('ui_diff', action='state'), lambda row: row.get('status') == 'ready')
        snapshot = Path(result['snapshotDirectory']).resolve()
        assert snapshot.is_relative_to(temp) and snapshot.is_dir(), result
        action('ui_command', command='close_dialog')
        wait_for(snapshot.exists, lambda result: not result)
        wait_for(state, lambda s: not s['dialogs'])

    def search():
        action('ui_search', query='Demo', method='message', currentBranchOnly=False)
        wait_for(local, lambda s: not s.get('searchQuerying') and s.get('searchResultCount') == 2)
        action('ui_search', select=head, open=True)
        wait_for(local, lambda s: not s.get('searchActive') and s.get('selectedCommits') == [head] and
                 s.get('detailCommit') == head and not s.get('graphLoading'))

    def refresh():
        revision = local()['workingCopyRefreshVersion']
        action('ui_refresh')
        wait_for(local, lambda s: s.get('workingCopyRefreshVersion', 0) > revision and
                 not s.get('graphLoading') and s.get('detailCommit') == head)

    def mixed(index):
        comparison()
        search()
        child.tool('ui_window', state='normal' if index % 2 == 0 else 'maximized')
        refresh()

    def phase(label, repeats, operation, rest):
        completed[label] = 0
        for index in range(repeats):
            operation(index)
            completed[label] += 1
            if (index + 1) % 10 == 0:
                sample(f'{label}-round{index + 1}')
        sample(f'{label}-end')
        idle(label, rest)

    try:
        git('init', '-b', 'main')
        git('config', 'user.name', 'TreeGit Demo')
        git('config', 'user.email', 'demo@example.invalid')
        (repo / 'demo.bin').write_bytes(bytes(range(256)) * 16)
        git('add', 'demo.bin')
        git('commit', '-m', 'Demo initial')
        (repo / 'demo.bin').write_bytes(bytes(range(256)) * 15 + bytes(reversed(range(256))))
        git('commit', '-am', 'Demo binary diff')
        head = git('rev-parse', 'HEAD')
        before = (head, git('ls-files', '--stage'), git('status', '--porcelain'))
        (profile.data / 'preference.json').write_text(json.dumps({'Locale': 'zh_CN', 'Theme': 'Light',
            'GitInstallPath': git_path, 'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False}), 'utf-8')
        child = Client(exe, env)
        child.rpc('initialize', {'protocolVersion': '2025-11-25'})
        child.pid = state()['processId']
        verify_process(child.pid, exe)
        child.tool('ui_window', width=1180, height=780, state='normal')
        sample('shell')
        action('ui_open_repository')
        wait_for(local, lambda s: bool(s) and not s.get('graphLoading'))
        action('ui_select_commit', revision=head)
        wait_for(local, lambda s: not s.get('graphLoading') and s.get('detailCommit') == head and s.get('detailChanges'))

        phase('warmup', 6, mixed, 30)
        if not mixed_only:
            phase('comparison', 15, lambda _: comparison(), 30)
            phase('search', 15, lambda _: search(), 30)
            phase('refresh', 15, lambda _: refresh(), 30)
        for index in range(1, 4):
            phase(f'mixed{index}', 15 if compact else 30, mixed, 30 if compact else 45)
        idle('mixed-final', 30 if compact else 60)
        assert before == (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain'))
        tabs = child.tool('ui_tabs', action='list')['tabs']
        tab = next(entry for entry in tabs if entry['repo'] and Path(entry['repo']) == repo)
        child.tool('ui_tabs', action='close', index=tab['index'])
        wait_for(local, lambda s: not s)
        idle('repo-closed', 30 if compact else 45)
        assert not samples[-1]['descendants'], 'Closing the repository retained child processes'
    except Exception:
        failure = traceback.format_exc()
        print(failure, flush=True)
    finally:
        try:
            if child:
                child.close()
        finally:
            profile.close()
            remove_fixture(root, output, 'TreeGit handle fixture ')
            save()
    if failure:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
