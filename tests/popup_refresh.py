"""User-triggered Git actions refresh their results even with file watching disabled."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit explicit popup audit '));fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))

def git(repo,*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()

def identity(repo):
    git(repo,'config','user.name','Popup QA');git(repo,'config','user.email','popup@example.invalid')

remote=root/'remote.git';remote.mkdir();git(remote,'init','--bare')
repo=root/'local';repo.mkdir();git(repo,'init','-b','main');identity(repo)
(repo/'file.txt').write_text('base\n');git(repo,'add','.');git(repo,'commit','-m','Base');git(repo,'remote','add','origin',str(remote));git(repo,'push','-u','origin','main');git(remote,'symbolic-ref','HEAD','refs/heads/main')
peer=root/'peer';peer.mkdir();git(peer,'clone',str(remote),'.');identity(peer)
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]

def tool(name,args=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':'tools/call','params':{'name':name,'arguments':args or {}}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    r=r['result'];assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)

def probe(target,operation,expected,**fields):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'popup_'+operation,'disableAutoRefresh':True,'expected':expected,**fields}),encoding='utf-8')
    tool('ui_command',{'repo':str(target),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Popup action timed out'
    r=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in r,r
    state=r['operationDetails'];records.append({'operation':operation,'expected':expected,'state':state})
    matched=all(value in state['tags'] if key=='tag' else state[key]==value for key,value in expected.items())
    check(operation+' updates the visible repository state without automatic refresh',matched)
    return state

try:
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':0,'method':'initialize','params':{'protocolVersion':'2025-11-25'}})+'\n');p.stdin.flush();assert 'error' not in json.loads(messages.get(timeout=45))
    tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId'];time.sleep(1)
    (peer/'incoming.txt').write_text('incoming\n');git(peer,'add','.');git(peer,'commit','-m','Incoming');git(peer,'push');incoming=git(peer,'rev-parse','HEAD')
    probe(repo,'fetch',{'behind':1,'ahead':0});check('fetch updates the actual remote-tracking ref',git(repo,'rev-parse','origin/main')==incoming)
    probe(repo,'pull',{'head':incoming,'behind':0,'ahead':0});check('pull advances the actual checked-out branch',git(repo,'rev-parse','HEAD')==incoming)
    (repo/'outgoing.txt').write_text('outgoing\n');git(repo,'add','.');git(repo,'commit','-m','Outgoing');outgoing=git(repo,'rev-parse','HEAD');tool('ui_refresh',{'repo':str(repo)});time.sleep(1)
    probe(repo,'push',{'ahead':0,'behind':0});check('push updates the actual server branch',git(remote,'rev-parse','main')==outgoing)
    probe(repo,'tag',{'tag':'qa-visible-tag'},tag='qa-visible-tag');check('tag creation writes the intended reference',git(repo,'rev-parse','qa-visible-tag^{}')==outgoing)
    probe(repo,'reset',{'head':incoming,'changes':1},revision=incoming);check('mixed reset preserves the outgoing file bytes',git(repo,'rev-parse','HEAD')==incoming and (repo/'outgoing.txt').read_text()=='outgoing\n')
    stash_repo=root/'stash-conflict';stash_repo.mkdir();git(stash_repo,'init','-b','main');identity(stash_repo)
    file=stash_repo/'file.txt';file.write_text('base\n');git(stash_repo,'add','.');git(stash_repo,'commit','-m','Base');file.write_text('stashed\n');git(stash_repo,'stash','push','-m','Must survive conflict');stash=git(stash_repo,'rev-parse','stash@{0}')
    file.write_text('conflicting current\n');git(stash_repo,'commit','-am','Current');tool('ui_open_repository',{'repo':str(stash_repo)});time.sleep(1)
    probe(stash_repo,'apply_stash',{'changes':1});check('failed stash application retains its stash and exposes the conflict',git(stash_repo,'rev-parse','stash@{0}')==stash and 'UU file.txt' in git(stash_repo,'status','--porcelain'))
    assert not failures,failures
finally:
    (out/'popup-refresh-results.json').write_text(json.dumps({'records':records,'failures':failures,'root':str(root),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
