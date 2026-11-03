"""Exercise the actual desktop working-copy overview, widths and full MCP writes."""
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit overview 中文 '))
repo = root / 'first repo'
other = root / 'second repo'
git = shutil.which('git')
checks = []

def local(*args, cwd=repo):
    p = subprocess.run([git, *args], cwd=cwd, capture_output=True, text=True, encoding='utf-8')
    assert p.returncode == 0, p.stderr
    return p.stdout.strip()

for folder in [repo, other]:
    folder.mkdir()
    local('init', '-b', 'main', cwd=folder)
    local('config', 'user.name', 'TreeGit QA', cwd=folder)
    local('config', 'user.email', 'qa@example.invalid', cwd=folder)
    (folder/'sample.txt').write_text('initial\n', encoding='utf-8')
    local('add', '.', cwd=folder)
    local('commit', '-m', 'Initial', cwd=folder)

class Client:
    def __init__(self, readonly=False):
        self.p = subprocess.Popen([str(exe), '--mcp', *(['--read-only'] if readonly else [])], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        self.seq = 0
        self.rpc('initialize', {'protocolVersion': '2025-11-25'})
    def rpc(self, method, params):
        self.seq += 1
        self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.seq,'method':method,'params':params})+'\n')
        self.p.stdin.flush()
        response = json.loads(self.p.stdout.readline())
        return response
    def tool(self, name, args={}, error=False):
        response = self.rpc('tools/call', {'name':name,'arguments':args})
        if error:
            assert 'error' in response or response.get('result',{}).get('isError'), response
            return response
        assert 'error' not in response, response
        r = response['result']
        assert not r.get('isError'), r
        return base64.b64decode(r['content'][0]['data']) if name == 'ui_screenshot' else json.loads(r['content'][0]['text'])
    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=10)
        assert self.p.returncode == 0

def check(name, ok=True):
    assert ok, name
    checks.append(name)
    print('PASS', name, flush=True)

c = Client()
names = [t['name'] for t in c.rpc('tools/list', {})['result']['tools']]
check('default connection exposes full Git reads/writes', all(n in names for n in ['git_run','git_clone','git_action']))
c.tool('ui_open_repository', {'repo':str(repo)})
time.sleep(1)
c.tool('ui_working_copy', {'repo':str(repo)})
def state():
    return next(r for r in c.tool('ui_state')['repositories'] if Path(r['repo']) == repo)
check('uncommitted row opens actual working-copy details beneath history', state()['workingCopySelected'] and state()['view'] == 0 and state()['detailView'] == 'WorkingCopy')
check('synthetic working-copy row is not a fake Git commit', state()['graphTotal'] == 1)
widths = {'graph':88,'description':510,'date':180,'author':140,'commit':110,'authorDate':170}
c.tool('ui_history_columns', {'repo':str(repo), **widths})
time.sleep(.2)
actual = c.tool('ui_history_columns', {'repo':str(repo)})
check('all six columns can be adjusted', all(abs(actual[k]-v) < 1 for k,v in widths.items()))
c.tool('ui_refresh', {'repo':str(repo)})
time.sleep(1)
check('graph and other widths survive refresh', all(abs(c.tool('ui_history_columns', {'repo':str(repo)})[k]-v) < 1 for k,v in widths.items()))
check('working-copy selection survives refresh', state()['workingCopySelected'])
c.tool('ui_open_repository', {'repo':str(other)})
time.sleep(.5)
check('widths are isolated per repository', c.tool('ui_history_columns', {'repo':str(other)})['graph'] != 88)
c.tool('ui_open_repository', {'repo':str(repo)})
check('returning to repository retains widths', c.tool('ui_history_columns', {'repo':str(repo)})['graph'] == 88)
c.tool('ui_history_columns', {'repo':str(repo),'graph':20}, error=True)
check('invalid widths are rejected')

def run(*arguments):
    result = c.tool('git_run', {'repo':str(repo),'arguments':list(arguments)})
    assert result['isSuccess'], result
    return result['stdout']

run('tag', 'test-tag')
run('tag', '-d', 'test-tag')
check('full Git tool creates and deletes tags', local('tag') == '')
run('branch', 'disposable')
run('branch', '-D', 'disposable')
check('full Git tool deletes branches', 'disposable' not in local('branch'))
(repo/'sample.txt').write_text('changed\n', encoding='utf-8')
run('add', '--', 'sample.txt')
check('staging works through full Git tool', local('diff','--cached','--name-only') == 'sample.txt')
run('restore', '--staged', '--', 'sample.txt')
check('unstaging preserves working file', (repo/'sample.txt').read_text() == 'changed\n' and not local('diff','--cached','--name-only'))
run('reset', '--hard', 'HEAD')
check('full Git tool supports explicit hard reset in disposable repo', (repo/'sample.txt').read_text() == 'initial\n')
(repo/'garbage.txt').write_text('temporary')
run('clean', '-fd')
check('full Git tool supports explicit clean in disposable repo', not (repo/'garbage.txt').exists())
(repo/'sample.txt').write_text('committed\n')
run('add', '.')
message = 'MCP quoted "message" 中文\n\nSecond line with spaces and \\ slash'
run('commit', '-m', message)
check('Git argument quoting preserves Unicode quotes and newlines', local('log','-1','--format=%B') == message)
remote = root/'remote.git'
local('init','--bare',str(remote))
run('remote','add','origin',str(remote))
run('push','--force','origin','main')
check('full Git tool supports force push to disposable remote', local('--git-dir='+str(remote),'rev-parse','main') == local('rev-parse','main'))
c.tool('ui_select_commit', {'repo':str(repo),'revision':'HEAD'})
time.sleep(.4)
check('selecting a real commit restores its details', not state()['workingCopySelected'] and state()['detailView'] == 'CommitDetail')
c.tool('ui_working_copy', {'repo':str(repo)})
time.sleep(.4)
(out/'uncommitted-overview.png').write_bytes(c.tool('ui_screenshot'))
saved = json.loads((repo/'.git/sourcegit.uistates').read_text(encoding='utf-8-sig'))
check('width settings are saved to repository state', saved['HistoryColumnWidths']['graph'] == 88)
c.tool('ui_history_columns', {'repo':str(repo),'reset':True})
check('reset returns graph to automatic width', c.tool('ui_history_columns', {'repo':str(repo)})['graph'] == 48)
c.tool('ui_window', {'width':1024,'height':720})
time.sleep(.5)
probe = c.tool('ui_state').get('layoutProbe')
if probe is not None:
    check('stage/unstage buttons fit within minimum-width panels', not probe['stageButtonsOutsidePanel'])
    centers = [p['y'] for p in probe['toolbarLabelCenters'] if p['y'] is not None]
    check('all toolbar labels share the same vertical center', max(centers)-min(centers) < 1)
    (out/'overview-minimum-width.png').write_bytes(c.tool('ui_screenshot'))
c.tool('ui_window', {'width':1440,'height':900})
readonly = Client(True)
readonly.tool('git_run', {'repo':str(repo),'arguments':['reset','--hard']}, error=True)
check('explicit read-only connection rejects generic Git tool')
readonly.close()
c.close()
(out/'overview-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe)}, ensure_ascii=False,indent=2),encoding='utf-8')
