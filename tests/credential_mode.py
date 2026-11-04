"""Repository authentication mode must follow external/MCP configuration edits."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit credential mode audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
subprocess.run(['git','init',str(repo)],check=True,capture_output=True,env=env)
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
    result=native('credential_mode',[])
    check('repository credential mode follows both configuration changes',result['operationDetails']==[False,True,False])
    assert all(c['passed'] for c in checks),result
finally:
    (out/'credential-mode-results.json').write_text(json.dumps({'checks':checks,'repo':str(repo),'result':locals().get('result')},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
