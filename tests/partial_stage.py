"""Select a diff chunk and click the actual stage/unstage UI buttons in the embedded overview."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit partial UI audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()
git('init','-b','main');git('config','user.name','Partial QA');git('config','user.email','partial@example.invalid');git('config','core.autocrlf','false')
selected='中文 a[1].txt';other='中文 a1.txt';original=''.join('line '+str(i)+'\n' for i in range(60));changed=original.replace('line 2\n','first selected change\n').replace('line 45\n','second unselected change\n');partial=original.replace('line 2\n','first selected change\n')
for name in [selected,other]:(repo/name).write_bytes(original.encode())
git('add','.');git('commit','-m','Base')
(repo/selected).write_bytes(changed.encode());(repo/other).write_bytes(b'unselected working change\n')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start()
seq=0;pid=None;checks=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
def native(operation,paths,value=None):
    result=Path(str(fixture)+'.result');result.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':operation,'paths':paths,'value':value}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        if result.exists():
            try:r=json.loads(result.read_text(encoding='utf-8'))
            except json.JSONDecodeError:time.sleep(.05);continue
            assert 'exception' not in r,r
            assert r['operationResult'],r
            return r
        time.sleep(.05)
    raise AssertionError('Native operation did not finish')
def check(name,ok):
    checks.append({'name':name,'passed':bool(ok)});print(('PASS ' if ok else 'FAIL ')+name,flush=True)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    native('native_partial_stage',[selected]);index=git('show',':'+selected)
    check('the embedded overview exposes a working partial-stage button',index==partial.strip())
    check('partial staging preserves the complete working file',(repo/selected).read_bytes()==changed.encode())
    check('partial staging preserves the matching unselected file in index and worktree',git('show',':'+other)==original.strip() and (repo/other).read_bytes()==b'unselected working change\n')
    check('the unselected hunk stays unstaged','second unselected change' in git('--literal-pathspecs','diff','--',selected) and 'first selected change' not in git('--literal-pathspecs','diff','--',selected))
    native('native_partial_unstage',[selected])
    check('the actual partial-unstage button restores the selected index content',git('show',':'+selected)==original.strip())
    check('partial unstaging preserves both working edits',(repo/selected).read_bytes()==changed.encode())
    lock=repo/'.git/index.lock'
    with lock.open('xb'):
        native('native_partial_stage',[selected])
    lock.unlink()
    check('a blocked partial stage preserves index and working bytes',git('show',':'+selected)==original.strip() and (repo/selected).read_bytes()==changed.encode())
    native('native_partial_stage',[selected])
    check('partial staging can be retried after releasing the index lock',git('show',':'+selected)==partial.strip())
    assert all(c['passed'] for c in checks),checks
finally:
    (out/'partial-stage-results.json').write_text(json.dumps({'checks':checks,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
