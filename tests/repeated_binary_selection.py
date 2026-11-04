"""Repeated selection during a large binary read must converge without restarting the read."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit repeated binary selection '));repo=root/'repo';repo.mkdir();env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
def git(*args):subprocess.run(['git','-C',str(repo),*args],env=env,check=True,capture_output=True)
git('init','-b','main');git('config','user.name','Selection QA');git('config','user.email','selection@example.invalid')
file=repo/'large.bin';file.write_bytes(bytes(range(256))*65536);git('add','.');git('commit','-m','Large binary')
with file.open('r+b') as f:f.seek(65533);f.write(b'changed')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId'];saw_ready=False;states=[]
    for i in range(100):
        tool('ui_diff',{'repo':str(repo),'action':'select','file':'large.bin','source':'unstaged'})
        state=tool('ui_diff',{'repo':str(repo),'action':'state'});states.append(state.get('status'));saw_ready |= state.get('status')=='ready'
        time.sleep(.03)
    print(('PASS ' if saw_ready else 'FAIL ')+'large binary read finishes while the same selection is repeatedly requested',flush=True)
    assert saw_ready,states
    assert state['differentBytes']==7 and state['selectedOffset']==65533,state
    print('PASS repeated selection retains the correct snapshot bytes and offset',flush=True)
finally:
    (out/'repeated-selection-results.json').write_text(json.dumps({'states':locals().get('states'),'readyDuringRepeatedSelection':locals().get('saw_ready'),'repo':str(repo),'exe':str(exe)},indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
