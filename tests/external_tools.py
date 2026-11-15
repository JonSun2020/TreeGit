"""Verify real Git index and file state through the external-tool command path."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import traceback
from qa_cleanup import new_fixture, remove_fixture

exe, out = (Path(arg).resolve() for arg in sys.argv[1:3])
verify_state_change = '--recovery-state-change' in sys.argv[3:]
filename = next((arg for arg in sys.argv[3:] if not arg.startswith('--')), "-sample O'Brien 中文.txt")
assert all(not arg.startswith('--') or arg == '--recovery-state-change' for arg in sys.argv[3:])
ci_profile = None
if verify_state_change:
    from qa_profile import IsolatedProfile
    ci_profile = IsolatedProfile(exe, out)
root = new_fixture(out, 'TreeGit external tools ')
temporary = new_fixture(Path('/tmp') if os.name == 'posix' else Path(tempfile.gettempdir()), 'TreeGit ext tmp ')
(root / 'treegit-owned-test').write_text('Owned disposable test repository\n')
repo = root / "repository O'Brien 中文"
repo.mkdir()
env = {**os.environ, 'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'),
       'XDG_CONFIG_HOME': str(root / 'config'), 'XDG_CACHE_HOME': str(root / 'cache'),
       'TMPDIR': str(temporary), 'TMP': str(temporary), 'TEMP': str(temporary)}
for path in [root / 'config', root / 'cache']: path.mkdir()
checks, records, failure = [], [], None

def check(name, value):
    assert value, name
    checks.append(name)

def git(*args, expect=0):
    result = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True)
    assert result.returncode == expect, (args, result.stdout, result.stderr)
    return result.stdout.strip()

def tool_program(name):
    path = shutil.which(name)
    if path: return path
    # Portable Git includes the same standard command-line programs on Windows.
    git_exe = Path(shutil.which('git')).resolve()
    for parent in list(git_exe.parents)[:4]:
        candidate = parent / 'usr/bin' / (name + '.exe')
        if candidate.is_file(): return str(candidate)
    raise AssertionError('Standard test utility unavailable: ' + name)

def invoke(name, program, arguments, success, kind='merge', keep_recovery=False):
    plan = root / 'plan.json'
    plan.write_text(json.dumps({'executable': program, 'arguments': arguments, 'kind': kind}), encoding='utf-8')
    report = out / (name + '.json')
    result = subprocess.run([str(exe), '--integration-external-tool', str(repo), filename, str(plan), str(report)],
                            env=env, capture_output=True, timeout=40)
    check(name + ' produces an actual result', result.returncode == 0 and report.exists())
    details = json.loads(report.read_text())
    check(name + ' expected adapter result', details.get('success') is success)
    check(name + ' TreeGit remains the first adapter', details['builtinFirst'])
    if not keep_recovery:
        check(name + ' releases its temporary merge session', not list(temporary.glob('TreeGit-external-merge-*')))
        check(name + ' leaves no redundant recovery draft', not list(repo.glob('.treegit-merge-draft-*')))
    records.append(details)

def conflict():
    git('reset', '--hard', 'main')
    git('merge', '--no-edit', 'feature/demo', expect=1)
    check('fixture has a real unresolved index', bool(git('ls-files', '--unmerged')))

try:
    git('init', '-b', 'main')
    git('config', 'user.name', 'TreeGit review')
    git('config', 'user.email', 'demo@example.invalid')
    git('config', 'core.autocrlf', 'false')
    (repo / filename).write_bytes(b'original\n')
    (repo / '.gitattributes').write_bytes(b'*.txt conflict-marker-size=11\n')
    git('add', '.'); git('commit', '-m', 'Initial')
    git('checkout', '-b', 'feature/demo')
    (repo / filename).write_bytes(b'remote change\n'); git('commit', '-am', 'Remote')
    git('checkout', 'main')
    (repo / filename).write_bytes(b'local change\n'); git('commit', '-am', 'Local')
    original_head = git('rev-parse', 'HEAD')
    conflict()
    original_index = git('ls-files', '--unmerged')
    original_bytes = (repo / filename).read_bytes()
    invoke('no-save', tool_program('true'), '', False)
    check('no-save preserves conflict index', git('ls-files', '--unmerged') == original_index)
    check('no-save preserves working bytes', (repo / filename).read_bytes() == original_bytes)
    invoke('cancel', tool_program('false'), '', False)
    check('cancel preserves conflict index', git('ls-files', '--unmerged') == original_index)
    check('cancel preserves working bytes', (repo / filename).read_bytes() == original_bytes)
    invoke('cancel-after-edit', tool_program('cp'), '"$LOCAL" "$MERGED"; false', False)
    check('a nonzero tool exit does not stage its edited result', git('ls-files', '--unmerged') == original_index)
    check('an unsuccessful tool keeps its edited draft', (repo / filename).read_bytes() == b'local change\n')
    (repo / filename).write_bytes(original_bytes)
    marker = root / 'unresolved.txt'
    marker.write_bytes(b'edited header\n' + original_bytes)
    marker_path = str(marker).replace('\\', '/')
    invoke('markers-remain', tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
    check('custom-size markers prevent staging', git('ls-files', '--unmerged') == original_index)
    check('an unresolved edited draft is preserved', (repo / filename).read_bytes() == marker.read_bytes())
    for label, encoding in [('partial-marker', 'utf-8'), ('utf16-le-marker', 'utf-16-le'), ('utf16-be-marker', 'utf-16-be'), ('utf32-marker', 'utf-32-le')]:
        marker.write_bytes(('合并结果\n' * 400 + '<' * 11 + ' Local\nlocal change\n').encode(encoding))
        invoke(label, tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
        check(label + ' preserves the unresolved index', git('ls-files', '--unmerged') == original_index)
        check(label + ' preserves the edited draft', (repo / filename).read_bytes() == marker.read_bytes())
    marker.write_bytes(b'A' * 65575 + b'\n' + b'<' * 11 + b' Local\n')
    invoke('marker-across-buffers', tool_program('cp'), '"' + marker_path + '" "$MERGED"', False)
    check('a marker crossing the streaming buffer also prevents staging', git('ls-files', '--unmerged') == original_index)
    check('the large unresolved draft is preserved', (repo / filename).read_bytes() == marker.read_bytes())
    invoke('saved-local', tool_program('cp'), '"$LOCAL" "$MERGED"', True)
    check('saved result resolves actual index', not git('ls-files', '--unmerged'))
    check('saved bytes match the selected result', (repo / filename).read_bytes() == b'local change\n')
    check('no operation creates an unexpected commit', git('rev-parse', 'HEAD') == original_head)
    git('merge', '--abort')
    before = [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')]
    invoke('diff-success', tool_program('true'), '"$LOCAL" "$REMOTE"', True, 'diff')
    exported = root / 'compared remote.txt'
    invoke('diff-correct-files', tool_program('cp'), '"$REMOTE" "' + str(exported).replace('\\', '/') + '"', True, 'diff')
    check('external comparison receives the requested revision bytes', exported.read_bytes() == b'local change\n')
    invoke('diff-failure', tool_program('false'), '"$LOCAL" "$REMOTE"', False, 'diff')
    check('diff does not mutate Git or files', before == [git('rev-parse', 'HEAD'), git('ls-files', '--stage'), git('status', '--porcelain')])
    if verify_state_change:
        conflict()
        previous_bytes = (repo / filename).read_bytes()
        invoke('state-changed-after-edit', tool_program('cp'), '"$LOCAL" "$MERGED"; git add -- "$MERGED"; false',
               False, keep_recovery=True)
        check('recovery does not overwrite the file after the index changed', (repo / filename).read_bytes() == previous_bytes)
        check('recovery respects the external operation on the real index', not git('ls-files', '--unmerged') and git('show', ':' + filename) == 'local change')
        drafts = list(repo.glob('.treegit-merge-draft-*'))
        check('the complete edited draft is available for manual recovery', len(drafts) == 1 and drafts[0].read_bytes() == b'local change\n')
        records_on_disk = list(temporary.glob('TreeGit-external-merge-*/draft-*.json'))
        check('manual recovery keeps its original file mapping', len(records_on_disk) == 1)
        mapping = {key.lower(): value for key, value in json.loads(records_on_disk[0].read_text(encoding='utf-8')).items()}
        check('the recovery mapping identifies both real files', Path(mapping['file']).resolve() == (repo / filename).resolve() and Path(mapping['recovery']).resolve() == drafts[0])
        check('recovery has not created a commit', git('rev-parse', 'HEAD') == original_head)
except BaseException:
    failure = traceback.format_exc()
    raise
finally:
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'records': records, 'failure': failure}, ensure_ascii=False, indent=2), encoding='utf-8')
    remove_fixture(root, out, 'TreeGit external tools ')
    remove_fixture(temporary, temporary.parent, 'TreeGit ext tmp ')
    if ci_profile: ci_profile.close()
print(json.dumps({'passed': len(checks), 'failure': failure}))
