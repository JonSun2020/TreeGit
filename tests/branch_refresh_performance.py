"""Measure Git process count and history readiness in an isolated TreeGit window."""
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_process import close_owned_gui, process_sample
from qa_profile import IsolatedProfile


class Client:
    def __init__(self, executable, env):
        self.executable = executable
        self.process = subprocess.Popen(
            [str(executable), '--mcp', '--read-only'], env=env, encoding='utf-8',
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.responses = queue.Queue()
        self.sequence = 0
        self.pid = None
        threading.Thread(target=self.read_responses, daemon=True).start()

    def read_responses(self):
        for line in self.process.stdout:
            self.responses.put(json.loads(line))

    def rpc(self, method, params):
        self.sequence += 1
        request = dict(jsonrpc='2.0', id=self.sequence, method=method, params=params)
        self.process.stdin.write(json.dumps(request) + '\n')
        self.process.stdin.flush()
        result = self.responses.get(timeout=45)
        assert result['id'] == self.sequence and 'error' not in result, result
        return result['result']

    def tool(self, name, **arguments):
        result = self.rpc('tools/call', dict(name=name, arguments=arguments))
        assert not result.get('isError'), result
        return json.loads(result['content'][0]['text'])

    def close(self):
        try:
            if self.pid is not None:
                close_owned_gui(self.executable, self.pid)
        finally:
            self.process.stdin.close()
            self.process.wait(timeout=15)


def wait_for(read, predicate, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = read()
        if predicate(state):
            return state
        time.sleep(.035)
    raise AssertionError('TreeGit did not reach the expected repository state')


def command_counts(trace, start):
    names = []
    logs = {'graph': 0, 'parentAbbreviations': 0, 'other': 0}
    local_config = 0
    for line in trace.read_text('utf-8').splitlines()[start:]:
        try:
            event = json.loads(line)
        except ValueError:
            continue
        if event.get('event') == 'cmd_name':
            names.append(event['name'])
        argv = event.get('argv', []) if event.get('event') == 'start' else []
        if 'config' in argv and '--local' in argv and '-l' in argv:
            local_config += 1
        if 'log' in argv:
            if any(arg.startswith('--format=%H%x00%P%x00%D') for arg in argv):
                logs['graph'] += 1
            elif '--no-walk=unsorted' in argv and '--abbrev=10' in argv:
                logs['parentAbbreviations'] += 1
            else:
                logs['other'] += 1
    counts = {name: names.count(name) for name in ('branch', 'config', 'log', 'rev-list', 'status', 'version')}
    return {**counts, **logs, 'localConfig': local_config}


def main():
    exe, out = (Path(value).resolve() for value in sys.argv[1:3])
    reference = '--reference' in sys.argv[3:]
    auto_discover = '--auto-discover' in sys.argv[3:]
    profile = IsolatedProfile(exe, out)
    fixture = new_fixture(out, 'TreeGit branch refresh fixture ')
    result = {}
    client = None
    try:
        repo = fixture / 'Demo'
        repo.mkdir()
        git_path = os.environ.get('TREEGIT_PERF_GIT') or shutil.which('git')
        assert git_path and Path(git_path).is_file(), 'Fixture Git executable is missing'
        env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(fixture / 'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}

        def git(*args, input_text=None):
            command = subprocess.run(
                [git_path, '-C', str(repo), *args], input=input_text.encode('utf-8') if input_text else None,
                env=env, capture_output=True,
            )
            assert command.returncode == 0, command.stderr.decode('utf-8', errors='replace')
            return command.stdout.decode('utf-8').strip()

        result['gitVersion'] = git('--version')
        git('init', '-b', 'main')
        git('config', 'user.name', 'TreeGit Demo')
        git('config', 'user.email', 'demo@example.invalid')
        commits = []
        for index in range(1, 321):
            message = f'Demo change {index}'
            previous = f'from :{index - 1}\n' if index > 1 else ''
            commits.append(f'commit refs/heads/main\nmark :{index}\n'
                           f'committer TreeGit Demo <demo@example.invalid> {1700000000 + index} +0000\n'
                           f'data {len(message)}\n{message}\n{previous}\n')
        git('fast-import', '--quiet', input_text=''.join(commits) + 'done\n')
        git('remote', 'add', 'origin', 'https://example.invalid/demo.git')
        head = git('rev-parse', 'HEAD')
        parent = git('rev-parse', 'HEAD^')
        tree = git('rev-parse', 'HEAD^{tree}')
        remote = git('commit-tree', tree, '-p', parent, '-m', 'Remote divergence')
        names = ['main', *(f'demo/topic-{index:02d}' for index in range(32))]
        alternate = names[1]
        refs = []
        for name in names:
            refs.append(f'update refs/heads/{name} {parent if name == alternate else head}\n')
            refs.append(f'update refs/remotes/origin/{name} {remote}\n')
            git('config', f'branch.{name}.remote', 'origin')
            git('config', f'branch.{name}.merge', f'refs/heads/{name}')
        git('update-ref', '--stdin', input_text=''.join(refs))
        (profile.data / 'preference.json').write_text(json.dumps({
            'Locale': 'zh_CN', 'Theme': 'Light', 'GitInstallPath': '' if auto_discover else git_path,
            'Check4UpdatesOnStartup': False, 'EnableAutoFetch': False,
            'Workspaces': [{'Name': 'Performance demo', 'IsActive': True, 'RestoreOnStartup': True,
                            'Repositories': [repo.as_posix()], 'ActiveIdx': 0}],
        }), 'utf-8')
        trace, startup = fixture / 'git-trace.jsonl', fixture / 'startup.json'
        # Only TreeGit's Git processes write this trace.
        app_env = {**env, 'GIT_TRACE2_EVENT': str(trace), 'TREEGIT_STARTUP_TRACE': str(startup)}
        started = time.perf_counter()
        client = Client(exe, app_env)
        client.rpc('initialize', {'protocolVersion': '2025-11-25'})
        client.pid = client.tool('ui_state')['processId']
        verify_process(client.pid, exe)

        def state():
            return next(iter(client.tool('ui_state')['repositories']), {})

        def settled(name, ahead, behind, total=320):
            return wait_for(state, lambda s: (
                s.get('branch') == name and s.get('ahead') == ahead and s.get('behind') == behind
                and (s.get('graphTotal') or 0) >= total and not s.get('graphLoading')
                and s.get('workingCopyStatusKnown')
            ))

        initial = settled('main', 1, 1)
        graph_ready = round((time.perf_counter() - started) * 1000, 1)
        assert len(initial['loadedBranches']) >= 2 * len(names)
        assert any(t['branch'] == alternate and t['status'] == '1↓'
                   for row in initial['graphCommits'] for t in row['tracking'])
        phases = {row['phase']: row['elapsedMs'] for row in json.loads(startup.read_text('utf-8'))}
        sample = process_sample(exe, client.pid)
        result['initial'] = {
            'openedMs': phases['opened'], 'graphReadyMs': graph_ready,
            'workingSetMiB': round(sample['WorkingSet64'] / 1048576, 2),
            'commands': command_counts(trace, 0),
        }

        def change(name, operation, completed):
            start = len(trace.read_text('utf-8').splitlines())
            tick = time.perf_counter()
            operation()
            completed()
            elapsed = round((time.perf_counter() - tick) * 1000, 1)
            time.sleep(.7)
            result[name] = {'elapsedMs': elapsed, 'commands': command_counts(trace, start)}

        change('checkout', lambda: git('switch', alternate), lambda: settled(alternate, 0, 1))
        change('upstreamMoves', lambda: git('update-ref', f'refs/remotes/origin/{alternate}', parent),
               lambda: settled(alternate, 0, 0))
        change('tagOnly', lambda: git('tag', 'visual-check', parent),
               lambda: wait_for(state, lambda s: any('visual-check' in row['refs'] for row in s['graphCommits'] or [])))
        if not reference:
            if auto_discover:
                assert result['initial']['commands']['version'] == 1, result
            assert result['initial']['commands']['localConfig'] == 0, result
            assert result['initial']['commands']['rev-list'] == 1, result
            assert result['checkout']['commands']['rev-list'] == 1, result
            assert result['upstreamMoves']['commands']['rev-list'] == 0, result
            for name in ('checkout', 'upstreamMoves', 'tagOnly'):
                assert result[name]['commands']['graph'] == 1, result

        # Preserve current-branch rendering and tracked status under both graph modes.
        client.tool('ui_graph', repo=repo.as_posix(), mode='current', showRemotes=True)
        git('update-ref', f'refs/remotes/origin/{alternate}', remote)
        wait_for(state, lambda s: (
            s.get('graphMode') == 1 and not s.get('graphLoading') and s.get('behind') == 1
            and any(row['sha'] == remote for row in s.get('graphCommits') or [])
        ))
        git('update-ref', '-d', f'refs/remotes/origin/{alternate}')
        settled(alternate, 0, 0, total=319)
        unicode_branch = '演示/分支'
        git('switch', '-c', unicode_branch)
        settled(unicode_branch, 0, 0, total=319)
        result['verifiedStates'] = ['diverged', 'behind', 'in-sync', 'tag-only refresh', 'current graph upstream',
                                    'deleted upstream', 'Unicode branch']
        result['executableBytes'] = exe.stat().st_size
        (out / 'branch-refresh.json').write_text(json.dumps(result, indent=2), 'utf-8')
        print(json.dumps(result), flush=True)
    finally:
        if client:
            client.close()
        profile.close()
        remove_fixture(fixture, out, 'TreeGit branch refresh fixture ')


if __name__ == '__main__':
    main()
