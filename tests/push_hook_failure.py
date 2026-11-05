"""A local pre-push rejection must never be retried as HTTP authentication."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
mode = 'action' if '--mcp-action' in sys.argv[3:] else 'run' if '--mcp-run' in sys.argv[3:] else 'native'
assert (exe.parent/'data').is_dir(), 'Use an isolated portable QA program copy'
root = Path(tempfile.mkdtemp(prefix='TreeGit push hook rejection '))
repo = root/'repo'
bare = root/'remote.git'
repo.mkdir()
bare.mkdir()
fixture = root/'fixture.json'
env = dict(os.environ, GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),
           GIT_CONFIG_NOSYSTEM='1', TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))

def git(*args, cwd=repo):
    p = subprocess.run(['git','-C',str(cwd),*args],env=env,capture_output=True,
                       text=True,encoding='utf-8',timeout=30)
    assert p.returncode == 0,p.stderr
    return p.stdout.strip()

git('init','-b','main')
git('config','user.name','Push hook QA')
git('config','user.email','push-hook@example.invalid')
git('init','--bare',cwd=bare)
(repo/'file.txt').write_text('initial\n','utf-8')
git('add','.')
git('commit','-m','Base')
git('remote','add','origin',str(bare))
git('push','-u','origin','main')
remote_before = git('rev-parse','refs/heads/main',cwd=bare)
(repo/'file.txt').write_text('pending push\n','utf-8')
git('add','.')
git('commit','-m','Pending push')
head = git('rev-parse','HEAD')
(repo/'file.txt').write_text('keep staged and working bytes\n','utf-8')
git('add','file.txt')
index = git('write-tree')
worktree = (repo/'file.txt').read_bytes()
counter = repo/'.git/push-hook-count'
hook = repo/'.git/hooks/pre-push'
hook.write_bytes(b'''#!/bin/sh
n=0
if test -f .git/push-hook-count; then n=$(cat .git/push-hook-count); fi
n=$((n+1))
echo "$n" > .git/push-hook-count
if test "$n" -le 2; then
 echo "fatal: Authentication failed for 'https://local-policy.example.invalid/repo/'" >&2
 exit 1
fi
exit 0
''')
p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,
                     creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue()
threading.Thread(target=lambda:[messages.put(x) for x in p.stdout],daemon=True).start()
seq = 0
pid = None
record = {'exe':str(exe),'root':str(root),'mode':mode}

def rpc(method, params):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n')
    p.stdin.flush()
    result = json.loads(messages.get(timeout=45))
    assert 'error' not in result,result
    return result['result']

def tool(name, args=None):
    r = rpc('tools/call',{'name':name,'arguments':args or {}})
    assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])

def push():
    if mode != 'native':
        name = 'git_action' if mode == 'action' else 'git_run'
        args = {'repo':str(repo),'action':'push'} if mode == 'action' else {'repo':str(repo),'arguments':['push','origin']}
        r = rpc('tools/call',{'name':name,'arguments':args})
        return json.loads(r['content'][0]['text'])['isSuccess']
    reply = Path(str(fixture)+'.result')
    reply.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_push_hook'}),'utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline = time.monotonic()+30
    while time.monotonic()<deadline:
        try:
            result = json.loads(reply.read_text('utf-8-sig'))
        except (FileNotFoundError,PermissionError,json.JSONDecodeError):
            time.sleep(.05)
            continue
        assert 'exception' not in result,result
        return result['operationResult']
    raise AssertionError('The native push did not finish')

try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    pid = tool('ui_state')['processId']
    tool('ui_open_repository',{'repo':str(repo)})
    success = push()
    record.update(success=success,hookCalls=int(counter.read_text()),
                  remoteUnchanged=git('rev-parse','refs/heads/main',cwd=bare)==remote_before,
                  headUnchanged=git('rev-parse','HEAD')==head,
                  indexUnchanged=git('write-tree')==index,
                  worktreeUnchanged=(repo/'file.txt').read_bytes()==worktree)
    assert not success and record['hookCalls']==1 and all(record[k] for k in
        ['remoteUnchanged','headUnchanged','indexUnchanged','worktreeUnchanged']),record
    hook.unlink()
    assert push() and git('rev-parse','refs/heads/main',cwd=bare)==head
    assert git('write-tree')==index and (repo/'file.txt').read_bytes()==worktree
    record['explicitRetrySucceeded'] = True
    record['passed'] = True
    print('PASS',mode,'push stops after one rejecting hook; only explicit retry changes the remote',flush=True)
finally:
    (out/'push-hook-failure-results.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),'utf-8')
    p.stdin.close()
    try:
        p.wait(timeout=15)
    except subprocess.TimeoutExpired:
        p.kill()
    if pid:
        literal = str(exe).replace("'","''")
        script = "$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong QA process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Owned QA GUI did not close'}};exit 0"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True,timeout=30)
