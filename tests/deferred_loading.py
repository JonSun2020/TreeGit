"""Verify real GUI behavior using only disposable repositories and the owned test executable."""
import base64, json, os, queue, subprocess, sys, threading, time
from pathlib import Path

base=Path(__file__).resolve().parent
exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
config=exe.parent/'data/preference.json'
repo=out/'temporary-repository';repo.mkdir(exist_ok=True)
def git(*args):
    subprocess.run(['git','-C',str(repo),*args],check=True,capture_output=True)
git('init','-b','main');git('config','user.name','Theme QA');git('config','user.email','theme@example.invalid')
if not (repo/'example.txt').exists():
    (repo/'example.txt').write_text('theme rendering verification\n','utf-8');git('add','.');git('commit','-m','Theme QA initial commit')
checks=[];captures=[]
def check(label,condition):
    assert condition,label
    checks.append(label);print('PASS',label,flush=True)
class Client:
    def __init__(self):
        env=os.environ.copy();env.pop('DOTNET_ROOT',None)
        self.p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
        self.q=queue.Queue();threading.Thread(target=lambda:[self.q.put(line) for line in self.p.stdout],daemon=True).start();self.seq=0;self.pid=None
        self.rpc('initialize',{'protocolVersion':'2025-11-25'})
        self.pid=self.tool('ui_state')['processId']
    def rpc(self,name,args):
        self.seq+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.seq,'method':name,'params':args})+'\n');self.p.stdin.flush()
        result=json.loads(self.q.get(timeout=45));assert 'error' not in result,result
        return result['result']
    def tool(self,name,args=None):
        result=self.rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result
        if name=='ui_screenshot':return base64.b64decode(result['content'][0]['data'])
        return json.loads(result['content'][0]['text'])
    def close(self):
        self.p.stdin.close();self.p.wait(timeout=15)
        if self.pid:
            script="$p=Get-Process -Id "+str(self.pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'}; if(-not $p.CloseMainWindow()){throw 'QA window did not accept close'}; if(-not $p.WaitForExit(15000)){throw 'QA app did not exit'}}"
            subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)


os.environ['GIT_CONFIG_GLOBAL']=str(out/'isolated.gitconfig');os.environ['GIT_CONFIG_NOSYSTEM']='1';os.environ['TREEGIT_STARTUP_TRACE']=str(out/'startup-trace.json')
paths=[]
for i in range(8):
    folder=out/('repository-'+str(i));folder.mkdir(exist_ok=True)
    def run(*args):
        subprocess.run(['git','-C',str(folder),*args],check=True,capture_output=True)
    run('init','-b','main');run('config','user.name','Deferred QA');run('config','user.email','deferred@example.invalid')
    if not (folder/'sample.txt').exists():
        (folder/'sample.txt').write_text('repository '+str(i)+'\n','utf-8');run('add','.');run('commit','-m','Initial commit')
    paths.append(str(folder).replace('\\','/'))
def prefs(saved,active):
    config.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','GitInstallPath':r'C:\Program Files\Git\cmd\git.exe','Workspaces':[{'Name':'Startup QA','IsActive':True,'RestoreOnStartup':True,'Repositories':saved,'ActiveIdx':active}]}),'utf-8')
prefs(paths,6)
c=Client()
try:
    tabs=c.tool('ui_tabs',{'action':'list'})['tabs']
    check('all saved repository tabs appear before full initialization',len(tabs)==8 and len({t['repo'] for t in tabs})==8)
    check('the saved selected repository is loaded first',tabs[6]['selected'] and tabs[6]['loaded'])
    check('hidden repository initialization is deferred',any(not t['loaded'] for t in tabs if not t['selected']))
    moved=c.tool('ui_tabs',{'action':'move','index':2,'targetIndex':0})['tabs']
    expected=[paths[2],paths[0],paths[1],*paths[3:7]]
    check('moving a pending tab retains all saved tabs',len(moved)==8 and moved[0]['repo']==paths[2])
    c.tool('ui_tabs',{'action':'close','index':7})
    tabs=c.tool('ui_tabs',{'action':'select','index':4})['tabs']
    check('selecting a pending tab loads it immediately',tabs[4]['selected'] and tabs[4]['loaded'])
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        tabs=c.tool('ui_tabs',{'action':'list'})['tabs']
        if all(t['loaded'] for t in tabs):break
        time.sleep(.15)
    check('remaining tabs load progressively',len(tabs)==7 and all(t['loaded'] for t in tabs))
    check('background completion keeps the selected tab',tabs[4]['selected'] and not any(t['repo']==paths[7] for t in tabs))
    (out/'loaded-tabs.png').write_bytes(c.tool('ui_screenshot'))
    phases=json.loads((out/'startup-trace.json').read_text('utf-8'));names=[p['phase'] for p in phases]
    check('window opens after selected repo and before background repo completion',names.index('active-repository-loaded')<names.index('opened')<names.index('deferred-repository-loaded'))
    check('only the intended repository is loaded before the first window',names[:names.index('opened')].count('active-repository-loaded')==1)
finally:c.close()
saved=json.loads(config.read_text('utf-8-sig'))['Workspaces'][0]
check('closing a pending tab removes it from saved workspace',saved['Repositories']==expected and saved['ActiveIdx']==4)
c=Client()
try:
    tabs=c.tool('ui_tabs',{'action':'list'})['tabs']
    check('restart preserves selected tab and pending tabs in order',len(tabs)==7 and tabs[4]['selected'] and [t['repo'] for t in tabs]==expected)
finally:c.close()
missing=str(out/'missing-repository').replace('\\','/')
prefs([paths[0],missing],0)
c=Client()
try:
    deadline=time.monotonic()+10
    while time.monotonic()<deadline:
        tabs=c.tool('ui_tabs',{'action':'list'})['tabs']
        if tabs[1]['error']:break
        time.sleep(.1)
    check('a failed hidden tab reports its error',bool(tabs[1]['error']))
    check('missing hidden repository does not prevent selected repository use',c.tool('ui_tabs',{'action':'list'})['tabs'][0]['loaded'] and c.tool('ui_state')['activePage']['kind']=='Repository')
    c.tool('ui_tabs',{'action':'close','index':1})
    check('failed hidden tab can be closed',len(c.tool('ui_tabs',{'action':'list'})['tabs'])==1)
finally:c.close()
prefs([missing],0)
c=Client()
try:
    c.tool('ui_tabs',{'action':'close','index':0})
    check('closing the only failed repository leaves a usable new tab',c.tool('ui_state')['activePage']['kind']=='Welcome')
finally:c.close()
(out/'verification.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repositories':paths},indent=2),'utf-8')
print('PASS deferred loading complete',len(checks),flush=True)
