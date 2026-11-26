"""Verify background fetch scheduling with local remotes in one isolated application."""
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
    profile = IsolatedProfile(exe, output)
    fixture = new_fixture(output, 'TreeGit fetch fixture ')
    settings = fixture / 'settings.json'
    trace = fixture / 'git-trace.jsonl'
    env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(fixture / 'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
    git_exe = shutil.which('git')
    client = None
    checks = []

    def git(*args):
        result = subprocess.run([git_exe, *map(str, args)], env=env, cwd=fixture,
                                capture_output=True, text=True, encoding='utf-8')
        assert result.returncode == 0, result.stderr
        return result.stdout.strip()

    def tracking(repo):
        return git('-C', repo, 'rev-parse', 'refs/remotes/origin/main')

    def wait_for_fetch(expected, *targets):
        deadline = time.monotonic() + 85
        while time.monotonic() < deadline:
            if all(tracking(repo) == expected for repo in targets):
                return
            time.sleep(1.5)
        raise AssertionError('A scheduled remote-tracking update did not finish')

    try:
        git('init', '-b', 'main', 'seed')
        git('-C', 'seed', 'config', 'user.name', 'TreeGit Demo')
        git('-C', 'seed', 'config', 'user.email', 'demo@example.invalid')
        git('-C', 'seed', 'commit', '--allow-empty', '-m', 'Start')
        git('clone', '--bare', 'seed', 'shared.git')
        repos = {name: fixture / name for name in ('enabled', 'repository-off', 'remote-off', 'no-remote')}
        for repo in repos.values():
            git('clone', 'shared.git', repo)
        (repos['repository-off'] / '.git/sourcegit.settings').write_text('{"BackgroundFetchEnabled":false}', 'utf-8')
        git('-C', repos['remote-off'], 'config', 'remote.origin.disableautofetch', 'true')
        git('-C', repos['no-remote'], 'remote', 'remove', 'origin')
        initial = tracking(repos['enabled'])
        (profile.data / 'preference.json').write_text(json.dumps({
            'Locale': 'en_US', 'GitInstallPath': git_exe, 'Check4UpdatesOnStartup': False,
            'EnableAutoFetch': True, 'AutoFetchInterval': 1,
        }), 'utf-8')
        client = Client(exe, {**env, 'TREEGIT_TEST_SETTINGS_FIXTURE': str(settings), 'GIT_TRACE2_EVENT': str(trace)})
        client.rpc('initialize', {'protocolVersion': '2025-11-25'})
        client.pid = client.tool('ui_state')['processId']
        verify_process(client.pid, exe)
        for repo in repos.values():
            client.tool('ui_open_repository', repo=repo.as_posix())
        wait_for(lambda: client.tool('ui_state')['repositories'],
                 lambda states: len(states) == len(repos) and all(
                     state['branch'] == 'main' and state['workingCopyStatusKnown'] and not state['graphLoading'] for state in states))

        git('-C', 'seed', 'commit', '--allow-empty', '-m', 'Remote change')
        git('-C', 'seed', 'push', str(fixture / 'shared.git'), 'main')
        updated = git('-C', 'seed', 'rev-parse', 'main')
        wait_for_fetch(updated, repos['enabled'])
        checks.append('eligible repository fetches its local remote on schedule')
        assert tracking(repos['repository-off']) == tracking(repos['remote-off']) == initial
        checks.append('repository and remote preferences suppress scheduled fetch')

        # Change the repository preference through the actual settings dialog.
        settings.write_text(json.dumps({'action': 'save', 'fields': {'BackgroundFetch': True}}), 'utf-8')
        client.tool('ui_command', command='repository_settings', repo=repos['repository-off'].as_posix())
        reply = settings.with_suffix('.json.result')
        wait_for(reply.exists, bool)
        assert not json.loads(reply.read_text('utf-8-sig'))['staysOpen']
        git('-C', repos['remote-off'], 'config', 'remote.origin.disableautofetch', 'false')
        client.tool('ui_refresh', repo=repos['remote-off'].as_posix())
        wait_for_fetch(updated, repos['repository-off'], repos['remote-off'])
        checks.append('enabling repository or remote settings reschedules without restarting')
        events = [json.loads(line) for line in trace.read_text('utf-8').split('\n')[:-1]]
        fetch_sessions = {event['sid'] for event in events if event.get('event') == 'start' and
                          'fetch' in event.get('argv', []) and '--verbose' in event['argv']}
        roots = {Path(event['worktree']).resolve() for event in events if event.get('event') == 'def_repo' and
                 event['sid'] in fetch_sessions and event.get('worktree')}
        assert roots == {repos[name] for name in ('enabled', 'repository-off', 'remote-off')}, roots
        checks.append('no scheduled Git fetch starts for a repository without remotes')
        assert all(git('-C', repo, 'rev-parse', 'HEAD') == initial for repo in repos.values())
        checks.append('background fetch preserves local HEADs')
    finally:
        try:
            if client:
                client.close()
        finally:
            profile.close()
            remove_fixture(fixture, output, 'TreeGit fetch fixture ')
            (output / 'auto-fetch-results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks,
                                                                       'fixtureRemoved': not fixture.exists()}, indent=2), 'utf-8')


if __name__ == '__main__':
    main()
