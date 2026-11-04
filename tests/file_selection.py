"""Selected files must be literal filenames, never Git wildcard pathspecs."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time

exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit literal filename audit '));repo=root/'repo';repo.mkdir()
fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    p=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    assert p.returncode==0,p.stderr
    return p.stdout.strip()
git('init','-b','main');git('config','user.name','Filename QA');git('config','user.email','filename@example.invalid')
selected='a[1].txt';other='a1.txt'
names=[selected,other,'中文 文件.txt',' leading.txt','-option.txt',"O'Brien & spec.txt"]
for n in names:(repo/n).write_text('original '+n+'\n',encoding='utf-8')
git('add','.');git('commit','-m','Initial');initial=git('rev-parse','HEAD')
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
def native(operation,paths):
    result=Path(str(fixture)+'.result');result.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':operation,'paths':paths}),encoding='utf-8')
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
    assert ok,name
    checks.append(name);print('PASS',name,flush=True)
def changed_pair():
    git('reset','--hard','HEAD')
    (repo/selected).write_text('selected content\n',encoding='utf-8')
    (repo/other).write_text('unselected content\n',encoding='utf-8')
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    tool('ui_window',{'zoom':1,'width':1280,'height':800})
    changed_pair();native('stage',[selected])
    check('native stage changes only the selected bracket filename',git('diff','--cached','--name-only')==selected)
    check('native stage leaves the matching unselected file unstaged',git('diff','--name-only')==other)
    git('add','.');native('unstage',[selected])
    check('native unstage leaves the matching unselected file staged',git('diff','--cached','--name-only')==other)
    check('native unstage returns only the selected file to unstaged changes',git('diff','--name-only')==selected)
    changed_pair();native('discard',[selected])
    check('native discard restores the selected file',(repo/selected).read_text(encoding='utf-8')=='original '+selected+'\n')
    check('native discard preserves unselected file bytes',(repo/other).read_text(encoding='utf-8')=='unselected content\n')
    changed_pair();native('stash',[selected])
    check('selected-file stash preserves the unselected working file',(repo/other).read_text(encoding='utf-8')=='unselected content\n')
    check('selected-file stash contains only the chosen path',git('diff','--name-only','stash@{0}^','stash@{0}')==selected)
    changed_pair();native('stash_file',[selected])
    check('pathspec-file stash preserves the unselected working file',(repo/other).read_text(encoding='utf-8')=='unselected content\n')
    check('pathspec-file stash contains only the chosen path',git('diff','--name-only','stash@{0}^','stash@{0}')==selected)
    changed_pair();native('checkout_file',[selected])
    check('restore from a revision preserves the unselected file',(repo/other).read_text(encoding='utf-8')=='unselected content\n')
    changed_pair();native('patch',[selected]);patch=Path(str(fixture)+'.patch').read_text(encoding='utf-8')
    check('saved selected-file patch excludes the matching unselected file','unselected content' not in patch and 'selected content' in patch)
    git('reset','--hard','HEAD')
    (repo/selected).write_text('bracket history\n',encoding='utf-8');git('--literal-pathspecs','add','--',selected);git('commit','-m','Bracket only');bracket=git('rev-parse','HEAD')
    (repo/other).write_text('other history\n',encoding='utf-8');git('add','--',other);git('commit','-m','Other only')
    history=native('file_history',[selected])['operationDetails']
    check('native file history follows only the literal selected filename',history==[bracket,initial])
    for n in names[2:]:
        git('reset','--hard','HEAD');(repo/n).write_text('changed\n',encoding='utf-8');native('stage',[n])
        raw=subprocess.run(['git','-C',str(repo),'diff','--cached','--name-only','-z'],capture_output=True,env=env).stdout
        check('native stage preserves the exact path '+repr(n),raw.decode('utf-8').split('\0')==[n,''])
    (out/'file-selection-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
finally:
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid)],capture_output=True)
