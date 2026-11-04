"""Native LFS push: unavailable remote, retained retry controls, then actual object transfer."""
import hashlib, json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit native LFS audit '));repo=root/'repo';repo.mkdir();bare=root/'remote.git';fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args,cwd=repo):
    r=subprocess.run(['git','-C',str(cwd),*args],env=env,capture_output=True,text=True,encoding='utf-8',timeout=30)
    assert r.returncode==0,r.stderr;return r.stdout.strip()
git('init','-b','main');git('config','user.name','LFS QA');git('config','user.email','lfs@example.invalid');git('lfs','install','--local');git('lfs','track','*.bin')
payload=bytes(range(256))*37;(repo/'firmware.bin').write_bytes(payload);git('add','.');git('commit','-m','LFS object');head=git('rev-parse','HEAD')
bare.mkdir();git('init','--bare',cwd=bare);git('config','lfs.transfer.maxretries','0');git('remote','add','origin',(root/'missing.git').as_uri())
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
replies=queue.Queue();threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;checks=[];records=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(replies.get(timeout=60));assert 'error' not in r,r;return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r;return json.loads(r['content'][0]['text'])
def probe():
    result=Path(str(fixture)+'.result');result.unlink(missing_ok=True);fixture.write_text(json.dumps({'action':'cancel','operation':'native_lfs_push'}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+45
    while not result.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result.exists(),'Native LFS popup timed out'
    r=json.loads(result.read_text('utf-8'));assert 'exception' not in r,r;records.append(r);return r
def check(name,value):
    assert value,name;checks.append(name);print('PASS',name,flush=True)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    failed=probe();details=failed['operationDetails']
    check('unavailable LFS remote reports a failed native popup',failed['operationResult'] is False and bool(details['log']))
    check('failed LFS push retains usable retry/cancel controls',not details['popupClosed'] and not details['busy'] and tool('ui_command',{'repo':str(repo),'command':'cancel_popup'})['cancelled'])
    check('failed LFS push preserves commit, index and working bytes',git('rev-parse','HEAD')==head and git('status','--porcelain')=='' and (repo/'firmware.bin').read_bytes()==payload)
    git('remote','set-url','origin',str(bare));tool('ui_refresh',{'repo':str(repo)})
    completed=probe();check('native LFS push can be retried after correcting the remote',completed['operationResult'] and completed['operationDetails']['popupClosed'])
    oid=hashlib.sha256(payload).hexdigest();objects=list((bare/'lfs/objects').rglob(oid))
    check('native LFS push transfers the exact payload without a Git pre-push hook',len(objects)==1 and objects[0].read_bytes()==payload)
    check('LFS push leaves Git references and working files unchanged',git('rev-parse','HEAD')==head and git('status','--porcelain')=='')
finally:
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
    (out/'lfs-workflow-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'records':records,'root':str(root)},ensure_ascii=False,indent=2),encoding='utf-8')
