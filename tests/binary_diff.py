"""Exercise the shipped GUI's binary snapshots, navigation and information dialogs."""
import atexit
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

from qa_cleanup import new_fixture
from qa_process import close_owned_gui
assert os.environ.get('CI') == 'true', 'Run only in an owned CI application'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root_parent=Path(tempfile.gettempdir()).resolve()
root=new_fixture(root_parent,'TreeGit d ')
repo=root/'repo 中文';repo.mkdir()
checks = []
snapshot_root=root/'s';snapshot_root.mkdir()
snapshot_baseline = set(snapshot_root.glob('TreeGit-binary-*'))
owned_snapshots=set()

def git(*args):
    p = subprocess.run([shutil.which('git'), *args], cwd=repo, capture_output=True, text=True, encoding='utf-8')
    assert not p.returncode, p.stderr
    return p.stdout.strip()

git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit QA'); git('config', 'user.email', 'qa@example.invalid')
name = "固件 old's version.bin"
original = bytes(range(256)) * 512
(repo/name).write_bytes(original); git('add', '.'); git('commit', '-m', 'Initial binary')
initial = git('rev-parse', 'HEAD')
staged = bytearray(original); staged[23] ^= 0x40
(repo/name).write_bytes(staged); git('add', '.')
working = bytearray(staged); working[17] ^= 0x40; working[65533:65540] = b'abcdefg'; working.extend(b'\x00\xac\xff')
(repo/name).write_bytes(working)
env=os.environ.copy();env.update(TMPDIR=str(snapshot_root),TEMP=str(snapshot_root),TMP=str(snapshot_root))
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
seq = 0
gui_pid = None
def cleanup_application():
    if p.poll() is None:
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()
    if gui_pid:close_owned_gui(exe,gui_pid)
atexit.register(cleanup_application)

def rpc(method, params={}):
    global seq
    seq += 1; p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n'); p.stdin.flush()
    response = json.loads(p.stdout.readline()); assert 'error' not in response, response
    return response['result']

def tool(name, args={}):
    result = rpc('tools/call', {'name':name,'arguments':args}); assert not result.get('isError'), result
    return base64.b64decode(result['content'][0]['data']) if name == 'ui_screenshot' else json.loads(result['content'][0]['text'])

def diff(action='state', **args):
    result=tool('ui_diff', {'repo':str(repo), 'action':action, **args})
    if result.get('snapshotDirectory'):owned_snapshots.add(Path(result['snapshotDirectory']))
    owned_snapshots.update(snapshot_root.glob('TreeGit-binary-*'))
    return result

def check(label, condition):
    if not condition:
        observation={'label':label,'first':globals().get('state'),'following':[]}
        for _ in range(30):
            try:time.sleep(.1);observation['following'].append(diff())
            except AssertionError as error:observation['observationError']=str(error);break
        (out/'failed-comparison-observation.json').write_text(json.dumps(observation,ensure_ascii=False,indent=2),encoding='utf-8')
        (out/'failed-comparison.png').write_bytes(tool('ui_screenshot'))
    assert condition, label
    checks.append(label); print('PASS', label, flush=True)

def select(file=name, source='unstaged', revision=None, expected=None):
    for _ in range(100):
        try: diff('select', file=file, source=source); break
        except AssertionError: time.sleep(.1)
    else: raise AssertionError('Change list did not load')
    for _ in range(200):
        state = diff()
        expected_revision = '' if source == 'staged' else None if source == 'unstaged' else revision
        if state['status'] == 'ready' and state['file'] == file and state['newRevision'] == expected_revision and (state['differentBytes']==0 or state['selectedOffset']>=0):
            if expected is None or expected(state):return state
        if state['status'] == 'error': raise AssertionError(state)
        time.sleep(.1)
    raise AssertionError('Binary comparison did not load')

def refresh():
    tool('ui_refresh', {'repo':str(repo)}); time.sleep(.8)

rpc('initialize', {'protocolVersion':'2025-11-25'})
tool('ui_open_repository', {'repo':str(repo)})
gui_pid = tool('ui_state')['processId']
tool('ui_window', {'width':1440, 'height':900, 'theme':'Blue', 'zoom':1})
state = select()
expected = sum(a != b for a, b in zip(staged, working)) + len(working)-len(staged)
check('unstaged binary compares against index, including changes across a 64 KiB boundary and append', state['differentBytes'] == expected and state['oldSize'] == len(staged) and state['newSize'] == len(working))
check('binary comparison automatically selects the first difference', state['selectedOffset'] == 17)
state = diff('next'); check('next difference navigates across page boundary', state['selectedOffset'] == 65533)
state = diff('next'); check('next difference reaches appended bytes', state['selectedOffset'] == len(staged))
state = diff('previous'); check('previous difference reaches the end of the preceding range', state['selectedOffset'] == 65539)
state = diff('last'); check('last difference selects the final added byte', state['selectedOffset'] == len(working)-1)
state = diff('goto', offset=16)
check('address jump reads the two matching snapshot offsets', bytes.fromhex(state['oldHex']) == staged[state['topOffset']:state['topOffset']+256] and bytes.fromhex(state['newHex']) == working[state['topOffset']:state['topOffset']+256])
(out/'binary-side-by-side.png').write_bytes(tool('ui_screenshot'))
tool('ui_window', {'width':1024, 'height':720}); time.sleep(.3)
(out/'binary-narrow.png').write_bytes(tool('ui_screenshot'))
check('narrow viewport adapts bytes per row', diff()['bytesPerRow'] in (4, 8))
tool('ui_window', {'theme':'Dark'}); time.sleep(.3)
(out/'binary-dark.png').write_bytes(tool('ui_screenshot'))
tool('ui_window', {'width':1440, 'height':900, 'theme':'Blue'})

diff('window')
comparison_window = None
for _ in range(200):
    state = diff()
    comparison_window = next((window for window in tool('ui_state')['dialogs']
                              if window['title'] == 'TreeGit · 二进制对比'), None)
    # Independent inspectors are intentionally constrained to the working area.
    # A 1024px CI desktop cannot accommodate the normal 1340px / 16-byte view.
    available = comparison_window['width'] if comparison_window else 0
    expected_columns = 16 if available >= 1170 else 8 if available >= 730 else 4
    if state['status'] == 'ready' and comparison_window and state['bytesPerRow'] == expected_columns: break
    time.sleep(.1)
(out/'independent-window.json').write_text(json.dumps(comparison_window, indent=2), encoding='utf-8')
area = comparison_window['workArea'] if comparison_window else {}
check('independent comparison keeps its native caption and frame inside the working area',
      comparison_window is not None
      and comparison_window['x'] >= area['x'] and comparison_window['y'] >= area['y']
      and comparison_window['x'] + comparison_window['width'] * comparison_window['scaling'] <= area['x'] + area['width']
      and comparison_window['y'] + comparison_window['height'] * comparison_window['scaling'] <= area['y'] + area['height'])
check('independent comparison uses the available screen width and preserves both snapshots',
      state['status'] == 'ready' and comparison_window is not None
      and state['bytesPerRow'] == expected_columns and state['differentBytes'] == expected
      and bytes.fromhex(state['oldHex']) == staged[state['topOffset']:state['topOffset']+256]
      and bytes.fromhex(state['newHex']) == working[state['topOffset']:state['topOffset']+256])
(out/'binary-independent.png').write_bytes(tool('ui_screenshot', {'window':'active'}))
tool('ui_window', {'zoom':2})
for _ in range(100):
    state=diff()
    if state['bytesPerRow'] in (4,8): break
    time.sleep(.1)
check('independent comparison adapts at high zoom without changing comparison results',state['status']=='ready' and state['bytesPerRow'] in (4,8) and state['differentBytes']==expected)
(out/'binary-independent-high-zoom.png').write_bytes(tool('ui_screenshot', {'window':'active'}))
tool('ui_window', {'zoom':1})
tool('ui_command', {'command':'close_dialog'})

for command in ['external_diff']:
    tool('ui_command', {'command':command})
    for _ in range(60):
        dialogs = tool('ui_state')['dialogs']
        if dialogs: break
        time.sleep(.1)
    check(command+' uses a normal unsupported information dialog', len(dialogs) == 1 and dialogs[0]['type'] == 'Alert' and dialogs[0]['message'] == '暂未支持')
    (out/(command+'.png')).write_bytes(tool('ui_screenshot', {'window':'active'}))
    tool('ui_command', {'command':'close_dialog'})

tool('ui_command', {'command':'check_updates'})
for _ in range(60):
    dialogs = tool('ui_state')['dialogs']
    if dialogs: break
    time.sleep(.1)
check('check_updates opens the update window', len(dialogs) == 1 and dialogs[0]['type'] == 'SelfUpdate')
tool('ui_command', {'command':'close_dialog'})

state = select(source='staged')
check('staged binary compares HEAD against index', state['differentBytes'] == 1 and state['selectedOffset'] == 23)
git('commit', '-m', 'Staged byte change')
git('add', '.'); git('commit', '-m', 'Working binary changes')
renamed = '固件 new name.bin'; git('mv', name, renamed)
renamed_content = bytearray(working); renamed_content[29] ^= 0x80; (repo/renamed).write_bytes(renamed_content)
git('add', '.'); git('commit', '-m', 'Rename and update binary')
rename_commit = git('rev-parse', 'HEAD')
changed = bytearray(renamed_content); changed[35] ^= 0x80; (repo/renamed).write_bytes(changed); git('add', '.'); git('commit', '-m', 'Binary revision')
revision = git('rev-parse', 'HEAD'); refresh()
tool('ui_select_commit', {'repo':str(repo), 'revision':revision}); time.sleep(.6)
state = select(renamed, 'history', revision)
check('committed binary compares its two revisions', state['differentBytes'] == 1 and state['selectedOffset'] == 35)
tool('ui_select_commit', {'repo':str(repo), 'revision':rename_commit}); time.sleep(.6)
state = select(renamed, 'history', rename_commit)
check('renamed binary loads the original path from its parent revision', state['differentBytes'] == 1 and state['selectedOffset'] == 29 and state['oldSize'] == state['newSize'] == len(working))
tool('ui_select_commit', {'repo':str(repo), 'revision':initial}); time.sleep(.6)
state = select(name, 'history', initial)
check('initial commit compares an absent file against the committed binary', state['oldSize'] == 0 and state['newSize'] == len(original) and state['differentBytes'] == len(original))

second = '二次重命名.bin'; git('mv', renamed, second)
modified = bytearray(changed); modified[41] ^= 0x40; (repo/second).write_bytes(modified); refresh()
state = select(second,expected=lambda s:s['differentBytes']==1 and s['selectedOffset']==41)
check('staged rename with additional worktree changes uses the new index path', state['differentBytes'] == 1 and state['selectedOffset'] == 41)
git('add', '.'); git('commit', '-m', 'Rename and modify'); (repo/second).unlink(); refresh()
state = select(second,expected=lambda s:s['oldSize']==len(modified) and s['newSize']==0)
check('deleted binary retains the complete old side', state['oldSize'] == len(modified) and state['newSize'] == 0 and state['differentBytes'] == len(modified))
git('restore', second)
added = '新添加.bin'; (repo/added).write_bytes(b'\x00\x01\x02\xff'); refresh()
state = select(added)
check('untracked binary has an absent old side', state['oldSize'] == 0 and state['newSize'] == 4 and state['differentBytes'] == 4)
git('add', '.'); (repo/added).write_bytes(b''); refresh()
state = select(added,expected=lambda s:s['oldSize']==4 and s['newSize']==0)
check('empty worktree binary compares against its index content', state['oldSize'] == 4 and state['newSize'] == 0 and state['differentBytes'] == 4)
tool('ui_select_view', {'repo':str(repo), 'view':'history'})
snapshots_before_close=set(snapshot_root.glob('TreeGit-binary-*'))-snapshot_baseline
tool('ui_command', {'command':'close_tab'})
time.sleep(.3)
for _ in range(50):
    # Other open repositories and other QA processes may legitimately allocate
    # their own snapshots when this tab closes. Inspect this comparison's owners.
    leaked = {folder for folder in (owned_snapshots or snapshots_before_close) if folder.exists()}
    if not leaked: break
    time.sleep(.1)
check('closing the repository cleans its comparison snapshots', bool(owned_snapshots or snapshots_before_close) and not leaked)
p.stdin.close(); p.wait(timeout=10)
# A following scenario uses a different isolated snapshot directory. Close only
# this exact owned QA GUI so its old TEMP cannot leak into the next scenario.
close_owned_gui(exe,gui_pid)
(out/'binary-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe),'ownedSnapshotCount':len(owned_snapshots),'remainingSnapshots':[str(p) for p in owned_snapshots if p.exists()]},ensure_ascii=False,indent=2),encoding='utf-8')
print('PASSED',len(checks))
