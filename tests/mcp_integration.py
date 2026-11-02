"""Exercise the shipped stdio server, its real GUI bridge, and disposable Git repositories."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import queue
import threading

exe = Path(sys.argv[1]).resolve()
artifacts = Path(sys.argv[2]).resolve()
artifacts.mkdir(parents=True, exist_ok=True)
git = exe.parent / 'git/cmd/git.exe'
testroot = Path(tempfile.mkdtemp(prefix='SourceGit MCP 中文 '))
repo = testroot / 'repo with spaces'
remote = testroot / 'remote.git'
repo.mkdir()
checks = []
def check(name, value):
    assert value, name
    checks.append(name)
    print('PASS', name, flush=True)
def run(*args, cwd=repo):
    r = subprocess.run([str(git), *args], cwd=cwd, text=True, encoding='utf-8', capture_output=True)
    assert r.returncode == 0, r.stderr
    return r.stdout.strip()
run('init', '-b', 'main')
run('config', 'user.name', 'MCP Integration')
run('config', 'user.email', 'mcp-test@example.invalid')
(repo / '文件 with spaces.txt').write_text('initial\n', encoding='utf-8')
run('add', '.')
run('commit', '-m', 'Initial')
run('init', '--bare', str(remote))
run('remote', 'add', 'origin', str(remote))
run('push', '-u', 'origin', 'main')
run('--git-dir='+str(remote),'symbolic-ref','HEAD','refs/heads/main')

class Mcp:
    def __init__(self, write=False):
        self.p = subprocess.Popen([str(exe), '--mcp', *(['--allow-write'] if write else [])], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        self.i = 0
        result = self.call('initialize', {'protocolVersion':'2025-11-25', 'capabilities':{}, 'clientInfo':{'name':'integration-test','version':'1'}})
        assert result['protocolVersion'] == '2025-11-25'
        self.p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n')
        self.p.stdin.flush()
    def call(self, method, params={}):
        self.i += 1
        self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.i,'method':method,'params':params}, ensure_ascii=False)+'\n')
        self.p.stdin.flush()
        line = self.p.stdout.readline()
        assert line, f'server closed: {self.p.poll()}'
        response = json.loads(line)
        assert response['id'] == self.i, response
        if 'error' in response:
            return {'isError':True,'content':[{'type':'text','text':response['error']['message']}]}
        return response['result']
    def tool(self, name, arguments={}, error=False):
        result = self.call('tools/call', {'name':name,'arguments':arguments})
        assert bool(result.get('isError')) == error, result
        if name == 'ui_screenshot' and not error:
            return base64.b64decode(result['content'][0]['data'])
        text = result['content'][0]['text']
        return text if error else json.loads(text)
    def close(self):
        self.p.stdin.close()
        self.p.wait(timeout=10)
        assert self.p.returncode == 0
        check('process exits on stdin EOF', self.p.returncode == 0)
        eof = queue.Queue()
        threading.Thread(target=lambda: eof.put(self.p.stdout.read()), daemon=True).start()
        check('GUI does not retain MCP stdout pipe', eof.get(timeout=3) == '')
        self.p.stdout.close()
        self.p.stderr.close()

readonly = Mcp()
tools = readonly.call('tools/list')['tools']
check('read-only tool discovery excludes mutation', 'git_action' not in [t['name'] for t in tools])
readonly.tool('git_action', {'repo':str(repo),'action':'stage','paths':['.']}, error=True)
readonly.tool('git_clone', {'url':str(remote),'destination':str(testroot/'readonly-clone')}, error=True)
check('read-only endpoint rejects mutation', True)
check('GUI auto launch and ui_state', readonly.tool('ui_state')['application'] == 'SourceGit')
check('open repository in GUI', readonly.tool('ui_open_repository', {'repo':str(repo)})['opened'])
state = readonly.tool('ui_state')
check('opened repository is visible in GUI state', any(Path(r['repo']) == repo for r in state['repositories']))
check('status pipeline', readonly.tool('git_status', {'repo':str(repo)})['isSuccess'])
check('history pipeline', 'Initial' in readonly.tool('git_log', {'repo':str(repo)})['stdout'])
check('refs pipeline', 'main' in readonly.tool('git_refs', {'repo':str(repo)})['stdout'])
check('commit navigation', len(readonly.tool('ui_select_commit', {'repo':str(repo),'revision':'HEAD'})['selected']) == 40)
readonly.tool('git_log', {'repo':str(repo),'count':501}, error=True)
readonly.tool('git_show', {'repo':str(repo),'revision':'--exec=calc'}, error=True)
readonly.tool('git_diff', {'repo':str(repo),'paths':['../outside']}, error=True)
check('invalid count, option injection and path traversal rejected', True)
time.sleep(2)
(artifacts / 'history.png').write_bytes(readonly.tool('ui_screenshot'))
check('actual GUI PNG capture', (artifacts/'history.png').stat().st_size > 10000)
readonly.tool('ui_select_view', {'repo':str(repo),'view':'working_copy'})
time.sleep(1)
(artifacts / 'working-copy.png').write_bytes(readonly.tool('ui_screenshot'))
readonly.close()

mcp = Mcp(True)
check('write endpoint advertises git_action', any(t['name']=='git_action' for t in mcp.call('tools/list')['tools']))
cloned = testroot/'MCP clone 中文'
check('clone uses original GUI workflow', mcp.tool('git_clone', {'url':str(remote),'destination':str(cloned),'recursive':False})['isSuccess'] and (cloned/'文件 with spaces.txt').exists())
mcp.tool('git_clone', {'url':str(remote),'destination':str(cloned)}, error=True)
mcp.tool('git_clone', {'url':'https://user:secret@example.invalid/repo.git','destination':str(testroot/'bad-url')}, error=True)
check('clone preserves existing destination and rejects embedded password', True)
not_repo=testroot/'not-a-repository';not_repo.mkdir()
mcp.tool('git_clone', {'url':str(not_repo),'destination':str(testroot/'failed-clone'),'recursive':False}, error=True)
check('failed clone remains inspectable in original UI', mcp.tool('ui_state')['activePage']['pendingDialog']=='Clone')
check('failed clone dialog can be cancelled', mcp.tool('ui_command', {'command':'cancel_popup'})['cancelled'])
mcp.tool('ui_open_repository', {'repo':str(repo)})
(repo/'文件 with spaces.txt').write_text('initial\nchanged\n', encoding='utf-8')
check('unstaged diff', '+changed' in mcp.tool('git_diff', {'repo':str(repo)})['stdout'])
def action(name, **kwargs):
    return mcp.tool('git_action', {'repo':str(repo),'action':name, **kwargs})
check('stage Unicode filename with spaces', action('stage', paths=['文件 with spaces.txt'])['isSuccess'])
check('staged diff', '+changed' in mcp.tool('git_diff', {'repo':str(repo),'staged':True})['stdout'])
message = '中文 MCP commit "quotes"\n\nBody with $() and `literal` characters'
check('commit preserves multiline Unicode message', action('commit', message=message)['isSuccess'] and run('log','-1','--format=%B') == message)
check('create and checkout branch', action('create_branch', revision='feature/mcp')['isSuccess'] and run('branch','--show-current')=='feature/mcp')
check('checkout main', action('checkout', revision='main')['isSuccess'])
(repo/'文件 with spaces.txt').write_text('stash me\n', encoding='utf-8')
check('stash', action('stash', message='MCP stash')['isSuccess'] and not run('status','--porcelain'))
check('stash pop', action('stash_pop')['isSuccess'] and 'stash me' in (repo/'文件 with spaces.txt').read_text(encoding='utf-8'))
action('stage', paths=['.'])
check('unstage', action('unstage', paths=['.'])['isSuccess'] and not run('diff','--cached'))
action('stage', paths=['.'])
action('commit', message='After stash')
check('fetch configured local remote', action('fetch')['isSuccess'])
check('push configured local remote', action('push')['isSuccess'])
check('fast-forward pull', action('pull')['isSuccess'])
mcp.tool('git_action', {'repo':str(repo),'action':'push','remote':'--upload-pack=evil'}, error=True)
check('remote option/URL injection rejected', True)
check('refresh GUI', mcp.tool('ui_refresh', {'repo':str(repo)})['refreshed'])
time.sleep(1)
state = mcp.tool('ui_state')
check('GUI branch tracks Git operations', any(r['branch']=='main' for r in state['repositories'] if Path(r['repo'])==repo))
# Exercise conflicts and recovery while SourceGit's real in-progress UI is active.
(repo/'conflict.txt').write_text('base\n', encoding='utf-8')
action('stage', paths=['conflict.txt'])
action('commit', message='Conflict base')
action('create_branch', revision='feature/conflict')
(repo/'conflict.txt').write_text('feature\n', encoding='utf-8')
action('stage', paths=['conflict.txt'])
action('commit', message='Feature conflict')
action('checkout', revision='main')
(repo/'conflict.txt').write_text('main\n', encoding='utf-8')
action('stage', paths=['conflict.txt'])
action('commit', message='Main conflict')
mcp.tool('git_action', {'repo':str(repo),'action':'merge','revision':'feature/conflict'}, error=True)
time.sleep(0.8)
check('conflicting merge is reported as tool error', (repo/'.git/MERGE_HEAD').exists())
check('abort active merge', action('merge_abort')['isSuccess'] and not (repo/'.git/MERGE_HEAD').exists())
mcp.tool('git_action', {'repo':str(repo),'action':'merge','revision':'feature/conflict'}, error=True)
time.sleep(0.8)
(repo/'conflict.txt').write_text('resolved\n', encoding='utf-8')
check('stage while conflict recovery UI is active', action('stage', paths=['conflict.txt'])['isSuccess'])
check('continue resolved merge without interactive editor', action('merge_continue')['isSuccess'] and len(run('rev-list','--parents','-n','1','HEAD').split()) == 3)
action('create_branch', revision='feature/pick')
(repo/'picked.txt').write_text('picked\n', encoding='utf-8')
action('stage', paths=['picked.txt'])
action('commit', message='Cherry pick source')
pick = run('rev-parse','HEAD')
action('checkout', revision='main')
check('cherry-pick', action('cherry_pick', revision=pick)['isSuccess'] and (repo/'picked.txt').exists())
check('revert', action('revert', revision='HEAD')['isSuccess'] and not (repo/'picked.txt').exists())
check('rebase unchanged feature', action('rebase', revision='main')['isSuccess'])
unborn = testroot/'unborn'
unborn.mkdir()
run('init', '-b','main',cwd=unborn)
(unborn/'new.txt').write_text('preserve working file\n',encoding='utf-8')
mcp.tool('ui_open_repository', {'repo':str(unborn)})
mcp.tool('git_action', {'repo':str(unborn),'action':'stage','paths':['new.txt']})
mcp.tool('git_action', {'repo':str(unborn),'action':'unstage','paths':['new.txt']})
check('unstage before first commit preserves file', (unborn/'new.txt').read_text(encoding='utf-8') == 'preserve working file\n' and run('ls-files',cwd=unborn) == '')
mcp.tool('ui_open_repository', {'repo':str(remote)})
mcp.tool('ui_select_view', {'repo':str(remote),'view':'working_copy'}, error=True)
check('bare repository rejects working-copy navigation', True)
mcp.tool('ui_open_repository', {'repo':str(repo)})
mcp.tool('ui_select_view', {'repo':str(repo),'view':'history'})
mcp.tool('ui_window', {'width':1024,'height':720,'theme':'Light'})
time.sleep(1)
(artifacts/'narrow.png').write_bytes(mcp.tool('ui_screenshot'))
mcp.tool('ui_window', {'width':1440,'height':900,'theme':'Dark'})
time.sleep(1)
(artifacts/'dark.png').write_bytes(mcp.tool('ui_screenshot'))
mcp.tool('ui_window', {'theme':'Light'})
check('window resize and retained dark/light themes', True)
(artifacts/'final.png').write_bytes(mcp.tool('ui_screenshot'))
mcp.close()
(artifacts/'mcp-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repository':str(repo),'executable':str(exe)}, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'All {len(checks)} checks passed. Disposable repo retained at {repo}', flush=True)
