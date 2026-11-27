"""Compare Welcome's status reads while revealing a previously hidden repository."""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from branch_refresh_performance import Client, wait_for
from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_profile import IsolatedProfile


def main():
    exe, output = (Path(value).resolve() for value in sys.argv[1:3])
    reference = '--reference' in sys.argv[3:]
    profile = IsolatedProfile(exe, output)
    root = new_fixture(output, 'TreeGit welcome status fixture ')
    trace = root / 'git-trace.jsonl'
    env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
    git = shutil.which('git')
    checks = []
    metrics = {}
    client = None

    def status_paths():
        events = []
        if trace.exists():
            for line in trace.read_text('utf-8').splitlines():
                try:
                    events.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
        statuses = {event['sid'] for event in events if event.get('event') == 'start'
                    and 'status' in event.get('argv', []) and '--porcelain=v2' in event['argv']}
        return [Path(event['worktree']).resolve() for event in events if event.get('event') == 'def_repo'
                and event['sid'] in statuses and event.get('worktree')]

    try:
        seed = root / 'seed'
        subprocess.run([git, 'init', '-b', 'main', str(seed)], env=env, check=True, capture_output=True)
        subprocess.run([git, '-C', str(seed), '-c', 'user.name=TreeGit Demo', '-c', 'user.email=demo@example.invalid',
                        'commit', '--allow-empty', '-m', 'Welcome fixture'], env=env, check=True, capture_output=True)
        front = [root / f'Front {i}' for i in range(3)]
        hidden = [root / f'Hidden {i:02d}' for i in range(36)]
        for path in front + hidden:
            shutil.copytree(seed, path)
        (hidden[-1] / 'untracked.txt').write_text('Welcome fixture\n', 'utf-8')

        def node(path):
            return {'Id': path.as_posix(), 'Name': path.name, 'IsRepository': True}

        (profile.data / 'preference.json').write_text(json.dumps({
            'Locale': 'en_US', 'GitInstallPath': git, 'EnableAutoFetch': False, 'Check4UpdatesOnStartup': False,
            'RepositoryNodes': [*map(node, front), {'Id': 'fixtures', 'Name': 'Archived fixtures', 'IsRepository': False,
                                                  'IsExpanded': False, 'SubNodes': list(map(node, hidden))}],
        }), 'utf-8')
        tick = time.perf_counter()
        client = Client(exe, {**env, 'GIT_TRACE2_EVENT': str(trace), 'TREEGIT_TEST_MENU_PROBE': '1'})
        client.rpc('initialize', {'protocolVersion': '2025-11-25'})
        launch = client.tool('ui_state')
        client.pid = launch['processId']
        verify_process(client.pid, exe)
        assert launch['activePage']['kind'] == 'Welcome' and not launch['repositories']
        wait_for(status_paths, lambda paths: set(front).issubset(paths))
        metrics['frontRowsReadyMs'] = round((time.perf_counter() - tick) * 1000, 1)
        if reference:
            wait_for(status_paths, lambda paths: set(hidden).issubset(paths), timeout=55)
        else:
            time.sleep(1)
        initial = status_paths()
        metrics['initialStatusCommands'] = len(initial)
        if not reference:
            assert len(initial) == len(front) and set(initial) == set(front), initial
        checks.append('Welcome queries the initially visible repositories')
        if reference:
            return
        client.tool('ui_layout', action='test_set', button='SearchBox', text=hidden[-1].name)
        client.tool('ui_layout', action='test_menu', text='welcome_expand_group', value=0)
        wait_for(status_paths, lambda paths: hidden[-1] in paths)
        wait_for(lambda: client.tool('ui_layout')['welcomeRows'],
                 lambda rows: any(row['name'] == hidden[-1].name and row['branch'] == 'main' and row['changes'] == 1 for row in rows))
        metrics['statusCommandsAfterSearch'] = len(status_paths())
        if not reference:
            assert set(status_paths()) == {*front, hidden[-1]}
        checks.append('expanding a filtered group loads and renders the matching repository status')
    finally:
        try:
            if client:
                client.close()
        finally:
            profile.close()
            remove_fixture(root, output, 'TreeGit welcome status fixture ')
            (output / 'welcome-status-results.json').write_text(json.dumps({**metrics, 'passed': len(checks), 'checks': checks,
                                                                           'fixtureRemoved': not root.exists()}, indent=2), 'utf-8')


if __name__ == '__main__':
    main()
