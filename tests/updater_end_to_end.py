"""Upgrade only a disposable copy of a real TreeGit release; never a user installation."""
import base64, hashlib, json, os, queue, shutil, subprocess, sys, threading, time
from pathlib import Path

old, new, archive, out, probe = map(lambda value: Path(value).resolve(), sys.argv[1:6])
out.mkdir(parents=True, exist_ok=True)
root = out/'installation with spaces 中文'
assert not root.exists(), 'A fresh, disposable output directory is required'
root.mkdir()
for source in old.iterdir():
    if source.name in ('data','log','.treegit-update'): continue
    if source.is_dir(): shutil.copytree(source,root/source.name)
    else: shutil.copy2(source,root/source.name)
(root/'data').mkdir();(root/'log').mkdir()
pref={'Locale':'zh_CN','Theme':'Dark','DefaultFontFamily':'','MonospaceFontFamily':'','GitInstallPath':shutil.which('git'),'Check4UpdatesOnStartup':False,'Workspaces':[]}
(root/'data/preference.json').write_text(json.dumps(pref),'utf-8')
(root/'data/important.txt').write_text('personal settings stay here','utf-8')
(root/'log/important.txt').write_text('logs stay here','utf-8')
(root/'mcp-readwrite.json').write_text('custom MCP configuration stays here','utf-8')
exe=root/'TreeGit.exe'
expected=hashlib.sha256((new/'TreeGit.exe').read_bytes()).hexdigest()
before=hashlib.sha256(exe.read_bytes()).hexdigest()
expect_rollback='--expect-rollback' in sys.argv[6:]
checks=[]
hold_mcp='--keep-mcp' in sys.argv[6:]
held_clients=[]
def check(name,condition):
    assert condition,name
    checks.append(name);print('PASS',name,flush=True)
class Client:
    def __init__(self):
        self.p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
        self.q=queue.Queue();self.seq=0
        threading.Thread(target=lambda:[self.q.put(line) for line in self.p.stdout],daemon=True).start()
        self.rpc('initialize',{'protocolVersion':'2025-11-25'})
    def rpc(self,method,params):
        self.seq+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.seq,'method':method,'params':params})+'\n');self.p.stdin.flush()
        result=json.loads(self.q.get(timeout=45));assert 'error' not in result,result
        return result['result']
    def tool(self,name,args=None):
        result=self.rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result
        if name=='ui_screenshot':return base64.b64decode(result['content'][0]['data'])
        return json.loads(result['content'][0]['text'])
    def close(self):
        if self.p.poll() is None:self.p.stdin.close()
        self.p.wait(timeout=10)
def close_gui(pid):
    script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong owned test process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Test GUI did not exit'}}"
    subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
failure=None;client=None;pid=None
try:
    client=Client();pid=client.tool('ui_state')['processId']
    check('old GUI starts from the disposable installation',bool(pid))
    if hold_mcp:
        held_clients=[client,Client(),Client()]
        client=None
    else:client.close();client=None
    ref=out/'prepared.json'
    subprocess.run([str(probe),'--prepare',str(root),str(archive),str(ref)],check=True)
    prepared=json.loads(ref.read_text('utf-8'));plan=Path(prepared['planPath'])
    subprocess.run([str(probe),'--launch',str(plan),str(new/'TreeGit.exe'),str(pid)],check=True)
    if held_clients:
        for endpoint in held_clients:endpoint.p.wait(timeout=10)
        check('connected AI endpoints close cleanly so program files can be replaced',all(endpoint.p.returncode==0 for endpoint in held_clients))
    close_gui(pid);pid=None
    deadline=time.monotonic()+100
    while time.monotonic()<deadline:
        try: state=json.loads(plan.read_text('utf-8'))
        except (OSError,json.JSONDecodeError):time.sleep(.1);continue
        if state['State'] in ('Committed','RolledBack','NeedsRecovery') and not (root/'.treegit-update/active.json').exists():break
        time.sleep(.2)
    check('update finishes in the expected committed or restored state',state['State']==('RolledBack' if expect_rollback else 'Committed'))
    check('the installed binary is the correct new or restored release',hashlib.sha256(exe.read_bytes()).hexdigest()==(before if expect_rollback else expected))
    client=Client();ui=client.tool('ui_state');pid=ui['processId']
    check('the resulting GUI accepts normal MCP control',ui['application']=='TreeGit')
    (out/'updated.png').write_bytes(client.tool('ui_screenshot'))
    check('personal settings logs and MCP configuration remain intact',(root/'data/important.txt').read_text('utf-8')=='personal settings stay here' and (root/'log/important.txt').read_text('utf-8')=='logs stay here' and (root/'mcp-readwrite.json').read_text('utf-8')=='custom MCP configuration stays here')
    saved=json.loads((root/'data/preference.json').read_text('utf-8-sig'))
    check('the selected theme survives the replacement',saved['Theme']=='Dark')
    if not expect_rollback:
        deadline=time.monotonic()+15
        while time.monotonic()<deadline and (plan.parent/'stage').exists():time.sleep(.2)
        check('temporary update payload is cleaned after successful startup',not (plan.parent/'stage').exists() and (plan.parent/'backup/TreeGit.exe').exists())
except Exception as error:
    import traceback
    failure=traceback.format_exc();raise
finally:
    if client:client.close()
    for endpoint in held_clients:endpoint.close()
    if pid:close_gui(pid)
    (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'installation':str(root)},indent=2),'utf-8')
