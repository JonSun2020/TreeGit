"""Native selection regressions for tracking-branch priority and sidebar colors."""
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import traceback

from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui

assert os.name == 'posix' and os.environ.get('CI') == 'true'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
parent = Path(tempfile.gettempdir()).resolve()
root = new_fixture(parent, 'TreeGit h ')
repo = root / 'repo'
repo.mkdir()
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root / 'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1')
fixture = os.environ.get('TREEGIT_BRANCH_FIXTURE')
manifest = json.loads(Path(fixture).read_text()) if fixture else None
target = None
checks = []
pid = None
process = None
failure = None
sequence = 0
responses = queue.Queue()

def git(*args):
    result = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8', timeout=30)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()

def check(name, value):
    assert value, name
    checks.append(name)
    print('PASS', name, flush=True)

def until(fn):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        value = fn()
        if value:
            return value
        time.sleep(.05)
    raise AssertionError('The expected desktop state did not settle')

def rpc(method, params):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params}) + '\n')
    process.stdin.flush()
    response = json.loads(responses.get(timeout=40))
    assert 'error' not in response, response
    return response['result']

def tool(name, **args):
    result = rpc('tools/call', {'name': name, 'arguments': args})
    assert not result.get('isError'), result
    return base64.b64decode(result['content'][0]['data']) if name == 'ui_screenshot' else json.loads(result['content'][0]['text'])

def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']) == repo)

def click(point, twice=False):
    assert Path(f'/proc/{pid}/exe').resolve() == exe
    subprocess.run(['xdotool', 'mousemove', str(point['x']), str(point['y']), 'sleep', '.15'], check=True, timeout=5)
    args = ['xdotool', 'click'] + (['--repeat', '2', '--delay', '120'] if twice else []) + ['1']
    subprocess.run(args, check=True, timeout=5)
    time.sleep(.6)

def scope_ready(search):
    current = state()
    scope = current['commitBranchHighlights']
    return current if current['searchActive'] == search and current['detailCommit'] == target and not current['detailBranchesLoading'] and scope and scope['sha'] == target else None

def verify_priority(current, name):
    scope = current['commitBranchHighlights']
    levels = {row['reference']: row['level'] for row in scope['local'] + scope['remote']}
    check(name + ' local tracking main is darker', levels == {'refs/heads/main': 2, 'refs/remotes/origin/main': 1})
    check(name + ' metadata and actual branch focus agree', current['detailBranches']['primary'] == 'refs/heads/main' and current['localBranchSelection'] == ['refs/heads/main'])

try:
    git('init', '-b', 'main')
    if manifest:
        git('bundle', 'unbundle', str(Path(fixture).with_suffix('.bundle')))
        for line in manifest['refs'].splitlines():
            reference, sha, *upstream = line.split()
            git('update-ref', reference, sha)
        target = manifest['userCommit']
        head = manifest['head']
    else:
        git('config', 'user.name', 'Branch highlight QA')
        git('config', 'user.email', 'branch-highlight@example.invalid')
        (repo / 'value.txt').write_text('base\n', encoding='utf-8')
        git('add', '.')
        git('commit', '-m', 'Base')
        base = git('rev-parse', 'HEAD')
        tree = git('rev-parse', 'HEAD^{tree}')
        # Imported commits have no local commit-origin reflog evidence.
        target = git('commit-tree', tree, '-p', base, '-m', 'Remote tip')
        head = git('commit-tree', tree, '-p', target, '-m', 'Local ahead')
        side = git('commit-tree', tree, '-p', base, '-m', 'Side branch')
        git('update-ref', 'refs/heads/main', head)
        git('update-ref', 'refs/remotes/origin/main', target)
        git('update-ref', 'refs/heads/feature/side', side)
    git('remote', 'add', 'origin', str(repo))
    git('config', 'branch.main.remote', 'origin')
    git('config', 'branch.main.merge', 'refs/heads/main')
    git('reset', '--hard', head)
    assert git('for-each-ref', '--format=%(upstream)', 'refs/heads/main') == 'refs/remotes/origin/main'
    assert set(git('for-each-ref', '--contains=' + target, '--format=%(refname)').splitlines()) == {'refs/heads/main', 'refs/remotes/origin/main'}
    before = git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')
    process = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=(out / 'stderr.log').open('w'), text=True, encoding='utf-8', env=env)
    threading.Thread(target=lambda: [responses.put(line) for line in process.stdout], daemon=True).start()
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    pid = tool('ui_state')['processId']
    tool('ui_open_repository', repo=str(repo))
    tool('ui_window', width=1400, height=900, zoom=1)
    tool('ui_select_view', repo=str(repo), view='history')
    tool('ui_graph', repo=str(repo), mode='all', showRemotes=True)
    until(lambda: not state()['graphLoading'] and state()['historyRows'])
    owned = [line.split()[0] for line in subprocess.check_output(['wmctrl', '-lp'], text=True).splitlines() if int(line.split()[2]) == pid]
    assert len(owned) == 1
    subprocess.run(['wmctrl', '-i', '-a', owned[0]], check=True)
    for theme in ['Blue', 'Light', 'Dark']:
        tool('ui_window', theme=theme)
        tool('ui_select_commit', repo=str(repo), revision=head)
        row = until(lambda: next((r for r in state()['historyRows'] if r['sha'] == target), None))
        click(row)
        verify_priority(until(lambda: scope_ready(False)), theme + ' history click')
        layout = tool('ui_layout')
        tints = {r['reference']: r['opacity'] for r in layout['sidebarCoverageTints']}
        check(theme + ' secondary background gains a slight tint', tints['refs/remotes/origin/main'] == .12 and tints['refs/heads/main'] == 0)
        (out / (theme + '-history.png')).write_bytes(tool('ui_screenshot'))
        tool('ui_select_view', repo=str(repo), view='search')
        tool('ui_search', repo=str(repo), method='sha', query=target)
        until(lambda: not state()['searchQuerying'] and state()['searchResultCount'] == 1)
        search_row = until(lambda: next((r for r in tool('ui_layout')['searchRows'] if r['sha'] == target), None))
        click(search_row['screen'])
        verify_priority(until(lambda: scope_ready(True)), theme + ' search single click')
        check(theme + ' search single click keeps the search page', state()['searchActive'] and state()['searchSelected'] == target)
        (out / (theme + '-search.png')).write_bytes(tool('ui_screenshot'))
        click(search_row['screen'], twice=True)
        verify_priority(until(lambda: scope_ready(False)), theme + ' search double click')
        check(theme + ' search double click reveals the history commit', state()['historySelectedCommits'] == [target])
        branch = next(b for b in state()['localBranchTargets'] if b['reference'] == 'refs/heads/main')
        click(branch)
        until(lambda: state()['commitBranchHighlights'] is None and state()['localBranchSelection'] == ['refs/heads/main'])
        layout = tool('ui_layout')
        selected = next(r for r in layout['sidebarManualSelections'] if r['reference'] == 'refs/heads/main')
        check(theme + ' manual branch selection uses the same dark layers', selected['selected'] and selected['opacity'] == 1 and selected['colors'][-2:] == [layout['selectedBranchBackground'], layout['primaryBranchBackground']])
        (out / (theme + '-manual.png')).write_bytes(tool('ui_screenshot'))
    check('all native selections preserve Git HEAD index and working files', before == (git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')))
except BaseException:
    failure = traceback.format_exc()
    try:
        (out / 'failure-state.json').write_text(json.dumps(tool('ui_state'), ensure_ascii=False, indent=2))
        (out / 'failure-layout.json').write_text(json.dumps(tool('ui_layout'), ensure_ascii=False, indent=2))
        (out / 'failure.png').write_bytes(tool('ui_screenshot'))
    except Exception:
        pass
    raise
finally:
    if process:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
    if pid:
        close_owned_gui(exe, pid)
    remove_fixture(root, parent, 'TreeGit h ')
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'failure': failure,
                                               'fixtureRemoved': not root.exists()}, ensure_ascii=False, indent=2), encoding='utf-8')
