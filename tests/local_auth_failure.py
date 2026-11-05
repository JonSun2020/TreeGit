"""A local rejecting hook must not be replayed as if remote credentials had failed."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe = Path(sys.argv[1]).resolve()
mode = 'action' if '--mcp-action' in sys.argv[3:] else 'run' if '--mcp-run' in sys.argv[3:] else 'native'
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
assert (exe.parent/'data').is_dir(), 'Use an isolated portable application copy'
root = Path(tempfile.mkdtemp(prefix='TreeGit local hook rejection '))
repo = root/'repo'; repo.mkdir(); fixture = root/'fixture.json'
env = {**os.environ, 'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),
       'GIT_CONFIG_NOSYSTEM':'1', 'TREEGIT_TEST_SETTINGS_FIXTURE':str(fixture)}

def git(*args):
    result = subprocess.run(['git','-C',str(repo),*args], env=env, capture_output=True,
                            text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()

git('init','-b','main'); git('config','user.name','Local hook QA')
git('config','user.email','local-hook@example.invalid')
(repo/'file.txt').write_text('base\n','utf-8'); git('add','.'); git('commit','-m','Base')
head = git('rev-parse','HEAD')
(repo/'file.txt').write_text('staged change\n','utf-8'); git('add','file.txt')
index = git('write-tree'); draft = 'Keep the rejected local commit'
hook = repo/'.git/hooks/pre-commit'; counter = repo/'.git/local-hook-count'
hook.write_bytes(b'''#!/bin/sh
n=0
if test -f .git/local-hook-count; then n=$(cat .git/local-hook-count); fi
n=$((n+1))
echo "$n" > .git/local-hook-count
if test "$n" -le 2; then
  echo "fatal: Authentication failed for 'https://local-policy.example.invalid/repo/'" >&2
  exit 1
fi
exit 0
''')
p = subprocess.Popen([str(exe),'--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, text=True, encoding='utf-8', env=env,
                     creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue()
threading.Thread(target=lambda:[messages.put(line) for line in p.stdout], daemon=True).start()
seq = 0; pid = None; record = {'exe':str(exe),'repo':str(repo),'mode':mode}

def rpc(method, params):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n')
    p.stdin.flush(); reply = json.loads(messages.get(timeout=45))
    assert 'error' not in reply, reply
    return reply['result']

def tool(name, args=None):
    result = rpc('tools/call',{'name':name,'arguments':args or {}})
    assert not result.get('isError'), result
    return json.loads(result['content'][0]['text'])

def commit():
    if mode != 'native':
        name = 'git_action' if mode == 'action' else 'git_run'
        arguments = {'repo':str(repo),'action':'commit','message':draft} if mode=='action' else {'repo':str(repo),'arguments':['commit','-m',draft]}
        result = rpc('tools/call',{'name':name,'arguments':arguments})
        return json.loads(result['content'][0]['text'])
    result_file = Path(str(fixture)+'.result'); result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_commit',
                                  'draft':draft,'autoStage':False}), 'utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline = time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline: time.sleep(.03)
    assert result_file.exists(), 'The native commit did not finish'
    result = json.loads(result_file.read_text('utf-8')); assert 'exception' not in result, result
    return result['operationDetails']

try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    pid = tool('ui_state')['processId']; tool('ui_open_repository',{'repo':str(repo)})
    details = commit()
    record.update(details=details, hookCalls=int(counter.read_text().strip()),
                  headUnchanged=git('rev-parse','HEAD')==head,
                  indexUnchanged=git('write-tree')==index)
    assert record['hookCalls']==1 and record['headUnchanged'] and record['indexUnchanged'], record
    if mode=='native': assert details['draft']==draft and not details['busy'], details
    else: assert not details['isSuccess'], details
    hook.unlink(); details = commit()
    assert git('rev-parse','HEAD')!=head, details
    if mode=='native': assert details['draft']=='' and not details['busy'], details
    else: assert details['isSuccess'], details
    record['passed']=True
    print('PASS',mode,'local authentication-like hook error stops once, preserves staged data; explicit retry succeeds',flush=True)
finally:
    (out/'local-auth-failure-results.json').write_text(json.dumps(record,indent=2),'utf-8')
    p.stdin.close()
    try: p.wait(timeout=10)
    except subprocess.TimeoutExpired: p.kill(); p.wait()
    if pid:
        literal = str(exe).replace("'", "''")
        script = ("$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){"
                  "if($p.Path -ne '"+literal+"'){throw 'Unexpected owned app'};"
                  "$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){"
                  "$p.Kill();$p.WaitForExit();throw 'Owned QA desktop did not exit normally'}};exit 0")
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
