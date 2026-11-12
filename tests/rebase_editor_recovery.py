"""Verify the displayed Local/Remote choices against real rebase index stages.

Run on an owned CI desktop with the normal portable build. Both Git rebase
backends reverse the native ours/theirs roles; the editor must keep its visible
Local/Remote meaning, preserve bytes and stop before explicitly continuing.
"""
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

assert os.environ.get('CI') == 'true' and os.name == 'posix'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
parent = Path(tempfile.gettempdir()).resolve()
root = new_fixture(parent, 'TreeGit rebase editor ')
repo = root / 'repo'
repo.mkdir()
env = {**os.environ, 'GIT_CONFIG_GLOBAL': str(root / 'global.gitconfig'),
       'GIT_CONFIG_NOSYSTEM': '1', 'GIT_EDITOR': 'true'}
checks = []
failure = None
process = None
pid = None
replies = queue.Queue()
sequence = 0
file = '配置 sample.txt'
path = repo / file


def git(*args, ok=True):
    result = subprocess.run([shutil.which('git'), '-C', str(repo), *args], env=env,
                            capture_output=True, timeout=30)
    if ok:
        assert result.returncode == 0, result.stderr.decode('utf-8', 'replace')
    return result


def text_git(*args):
    return git(*args).stdout.decode('utf-8').strip()


def check(label, condition):
    assert condition, label
    checks.append(label)
    print('PASS', label, flush=True)


def until(predicate):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.05)
    raise AssertionError('The requested rebase editor state did not settle')


def rpc(method, args):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence,
                                    'method': method, 'params': args}) + '\n')
    process.stdin.flush()
    result = json.loads(replies.get(timeout=45))
    assert result.get('id') == sequence and 'error' not in result, result
    return result['result']


def tool(name, args=None):
    result = rpc('tools/call', {'name': name, 'arguments': args or {}})
    assert not result.get('isError'), result
    if name == 'ui_screenshot':
        return base64.b64decode(result['content'][0]['data'])
    return json.loads(result['content'][0]['text'])


def action(name, **args):
    return tool('ui_merge_result', {'repo': str(repo), 'file': file,
                                    'action': name, **args})


def active_rebase():
    return (repo / '.git/rebase-merge').exists() or (repo / '.git/rebase-apply').exists()


def repo_state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']) == repo)


def document(rate):
    # Common lines separate two conflicts; retain BOM, CRLF and no final newline.
    return '\ufeff// 配置\r\nrate=' + str(rate) + '\r\n' + ''.join(
        'common line ' + str(i) + '\r\n' for i in range(12)) + 'last=' + str(rate)


try:
    git('init', '-b', 'main')
    git('config', 'user.name', 'Rebase editor QA')
    git('config', 'user.email', 'rebase-editor@example.invalid')
    git('config', 'core.autocrlf', 'false')
    (repo / '.gitattributes').write_text('*.txt text eol=crlf\n', encoding='utf-8')
    path.write_bytes(document(10).encode('utf-8'))
    git('add', '.')
    git('commit', '-m', 'Base')
    base = text_git('rev-parse', 'HEAD')
    git('switch', '-c', 'incoming')
    path.write_bytes(document(30).encode('utf-8'))
    git('commit', '-am', 'Remote change')
    incoming = text_git('rev-parse', 'HEAD')
    git('switch', 'main')
    path.write_bytes(document(20).encode('utf-8'))
    git('commit', '-am', 'Local change')
    local = text_git('rev-parse', 'HEAD')
    local_index = git('show', 'HEAD:' + file).stdout
    remote_index = git('show', 'incoming:' + file).stdout
    process = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=(out / 'stderr.log').open('w'),
                               text=True, encoding='utf-8')
    threading.Thread(target=lambda: [replies.put(line) for line in process.stdout], daemon=True).start()
    rpc('initialize', {'protocolVersion': '2025-11-25'})
    pid = tool('ui_state')['processId']
    tool('ui_open_repository', {'repo': str(repo)})
    for backend in ('merge', 'apply'):
        for style in ('merge', 'diff3', 'zdiff3'):
            label = backend + '/' + style
            # Cover matching and different source EOF conventions in both
            # rebase backends, where native index stages reverse user roles.
            local_work = document(20) + ('\r\n' if style == 'zdiff3' else '')
            remote_work = document(30) + ('\r\n' if style == 'diff3' else '')
            git('reset', '--hard', base)
            git('switch', '-C', 'incoming', base)
            path.write_bytes(remote_work.encode('utf-8'))
            git('commit', '-am', 'Remote change ' + label)
            incoming = text_git('rev-parse', 'HEAD')
            remote_index = git('show', 'HEAD:' + file).stdout
            git('switch', '-C', 'main', base)
            path.write_bytes(local_work.encode('utf-8'))
            git('commit', '-am', 'Local change ' + label)
            local = text_git('rev-parse', 'HEAD')
            local_index = git('show', 'HEAD:' + file).stdout
            git('config', 'merge.conflictStyle', style)
            result = git('rebase', '--' + backend, 'incoming', ok=False)
            check(label + ': real rebase stops with both native index stages',
                  result.returncode != 0 and active_rebase() and
                  git('show', ':2:' + file).stdout == remote_index and
                  git('show', ':3:' + file).stdout == local_index)
            stopped_head = text_git('rev-parse', 'HEAD')
            before_index = text_git('ls-files', '-u')
            before_bytes = path.read_bytes()
            tool('ui_refresh', {'repo': str(repo)})
            until(lambda: repo_state()['workingCopyStatusKnown'] and repo_state()['changes'] > 0)
            tool('ui_command', {'repo': str(repo), 'command': 'merge_conflicts', 'file': file})
            dialog = until(lambda: next((d for d in tool('ui_state')['dialogs']
                                        if d['type'] == 'MergeConflictEditor' and d['merge']['editable']), None))
            check(label + ': visible Local and Remote retain their user meaning',
                  dialog['merge']['rebase'] and 'rate=20' in dialog['merge']['localSample'] and
                  'rate=30' in dialog['merge']['remoteSample'] and 'rate=10' in dialog['merge']['baseSample'])
            initial = action('read')
            check(label + ': both separated conflicts are detected', initial['unsolved'] == 2)
            for choice, expected in [('left', 'rate=20\r\n'), ('right', 'rate=30\r\n'),
                                     ('left_then_right', 'rate=20\r\nrate=30\r\n'),
                                     ('right_then_left', 'rate=30\r\nrate=20\r\n')]:
                action('resolve', region=0, choice=choice)
                chosen = action('read')
                check(label + ': ' + choice + ' follows visible panels',
                      chosen['unsolved'] == 1 and expected in chosen['text'])
                action('undo')
                check(label + ': undo ' + choice + ' restores the exact original result',
                      action('read')['text'] == initial['text'])
            check(label + ': previewing choices never changes worktree or index',
                  path.read_bytes() == before_bytes and text_git('ls-files', '-u') == before_index)
            for region in (0, 1):
                action('resolve', region=region, choice='left')
            check(label + ': complete local resolution preserves BOM, CRLF and final line',
                  action('read')['text'] == local_work.removeprefix('\ufeff'))
            assert action('save')['saved']
            until(lambda: not tool('ui_state')['dialogs'])
            check(label + ': save stages exact bytes without continuing the rebase',
                  path.read_bytes() == local_work.encode('utf-8') and
                  git('show', ':' + file).stdout == local_index and not text_git('ls-files', '-u') and
                  active_rebase() and text_git('rev-parse', 'HEAD') == stopped_head)
            assert tool('git_action', {'repo': str(repo), 'action': 'rebase_continue'})['isSuccess']
            check(label + ': explicit continuation preserves the local branch and upstream ancestry',
                  not active_rebase() and text_git('symbolic-ref', '--short', 'HEAD') == 'main' and
                  text_git('merge-base', incoming, 'HEAD') == incoming and
                  not text_git('status', '--porcelain') and git('show', 'HEAD:' + file).stdout == local_index)
    (out / 'final.png').write_bytes(tool('ui_screenshot'))
except BaseException:
    failure = traceback.format_exc()
    try:
        (out / 'failure.json').write_text(json.dumps(tool('ui_state'), ensure_ascii=False, indent=2), encoding='utf-8')
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
    remove_fixture(root, parent, 'TreeGit rebase editor ')
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks,
        'failure': failure, 'fixtureRemoved': not root.exists()}, ensure_ascii=False, indent=2), encoding='utf-8')
