"""Observe actual binary snapshots during rapid index/worktree/delete transitions."""
import json,os,queue,subprocess,sys,tempfile,threading,time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit binary refresh audit '));repo=root/'repo';repo.mkdir();snapshots=root/'snapshots';snapshots.mkdir()
env=os.environ.copy();env.update(TEMP=str(snapshots),TMP=str(snapshots),GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',timeout=20);assert not r.returncode,r.stderr;return r.stdout.strip()
git('init','-b','main');git('config','user.name','Binary refresh QA');git('config','user.email','binary@example.invalid')
name='refresh.bin';payload=bytes(range(256))*273;(repo/name).write_bytes(payload);git('add','.');git('commit','-m','Initial binary')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
q=queue.Queue();threading.Thread(target=lambda:[q.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];owned=set()
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(q.get(timeout=30));assert 'error' not in r,r;return r['result']
def tool(name,args=None,error=False):
    r=rpc('tools/call',{'name':name,'arguments':args or {}})
    if error and r.get('isError'):return None
    assert not r.get('isError'),r;return json.loads(r['content'][0]['text'])
def verify(label,old,new,source='unstaged'):
    first=next((i for i,(a,b) in enumerate(zip(old,new)) if a!=b),min(len(old),len(new)))
    count=sum(a!=b for a,b in zip(old,new))+abs(len(old)-len(new));begin=time.monotonic();observations=[];state={}
    tool('ui_refresh',{'repo':str(repo)})
    while time.monotonic()-begin<15:
        selected=tool('ui_diff',{'repo':str(repo),'action':'select','file':name,'source':source},True)
        state=tool('ui_diff',{'repo':str(repo),'action':'state'}) if selected else {}
        owned.update(snapshots.glob('TreeGit-binary-*'))
        if state.get('status')=='error':raise AssertionError(state)
        if state.get('status')=='ready' and state.get('differentBytes',0)>0:
            assert state['selectedOffset']>=0,{'label':label,'error':'Ready was reported before initial navigation completed','state':state}
        actual=tuple(state.get(k) for k in ['oldSize','newSize','differentBytes','selectedOffset'])
        if state.get('status')=='ready' and actual==(len(old),len(new),count,first):
            top=state['topOffset'];assert bytes.fromhex(state['oldHex'])==old[top:top+256] and bytes.fromhex(state['newHex'])==new[top:top+256],state
            records.append({'label':label,'settledSeconds':round(time.monotonic()-begin,3),'earlyReadyStates':observations});print('PASS',label,records[-1]['settledSeconds'],flush=True);return
        if state.get('status')=='ready':observations.append({'seconds':round(time.monotonic()-begin,3),'sizesAndOffset':actual})
        time.sleep(.03)
    raise AssertionError({'label':label,'last':state,'earlyReadyStates':observations})
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    for turn in range(6):
        old=(repo/name).read_bytes();changed=bytearray(old);changed[(turn*7919+23)%len(changed)]^=0x80;(repo/name).write_bytes(changed)
        verify('worktree modification '+str(turn),old,bytes(changed))
        git('add',name);old=bytes(changed);changed[(turn*7919+41)%len(changed)]^=0x40;(repo/name).write_bytes(changed)
        verify('index updated while file stays selected '+str(turn),old,bytes(changed))
        git('add',name);git('commit','-m','Iteration '+str(turn));(repo/name).unlink()
        verify('committed then deleted while file stays selected '+str(turn),bytes(changed),b'')
        git('restore',name)
    tool('ui_command',{'repo':str(repo),'command':'close_tab'})
    for _ in range(100):
        if not any(f.exists() for f in owned):break
        time.sleep(.1)
    assert owned and not any(f.exists() for f in owned),'Owned snapshots were not disposed'
    print('PASS isolated comparison snapshot cleanup',len(owned),flush=True)
finally:
    (out/'binary-refresh-results.json').write_text(json.dumps({'passed':len(records),'records':records,'ownedSnapshots':len(owned),'remainingSnapshots':[str(f) for f in owned if f.exists()],'root':str(root)},ensure_ascii=False,indent=2),'utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
