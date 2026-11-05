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


config.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','GitInstallPath':r'C:\Program Files\Git\cmd\git.exe','Workspaces':[]}), 'utf-8')
c=Client()
try:
    c.tool('ui_window',{'width':1280,'height':800,'state':'normal'})
    c.tool('ui_command',{'command':'preferences','page':'appearance'})
    state=c.tool('ui_fonts',{'action':'state'})
    check('both dropdowns explicitly select default',state['controls']=={'interfaceValue':'','codeValue':''})
    (out/'default-fonts.png').write_bytes(c.tool('ui_screenshot',{'window':'active'}))
    lists={kind:c.tool('ui_fonts',{'action':'list','kind':kind})['fonts'] for kind in ['interface','code']}
    for kind,options in lists.items():
        check(kind+' has default plus installed font choices',len(options)>10 and options[0]['Family']=='' and bool(options[0]['Name']))
    names=[f['Family'] for f in lists['interface']]
    ui=next(name for name in ['Microsoft YaHei UI','Segoe UI','Arial'] if name in names)
    code=next(name for name in ['Consolas','Courier New','Lucida Console'] if name in names)
    state=c.tool('ui_fonts',{'action':'set','kind':'interface','font':ui})
    check('choosing the interface dropdown updates preference and rendered font',state['interfaceFont']==ui and state['controls']['interfaceValue']==ui and ui in state['renderedInterfaceFont'])
    state=c.tool('ui_fonts',{'action':'set','kind':'code','font':code})
    check('choosing the code dropdown updates preference and rendered font',state['codeFont']==code and state['controls']['codeValue']==code and code in state['renderedCodeFont'])
    (out/'selected-fonts.png').write_bytes(c.tool('ui_screenshot',{'window':'active'}))
    error=c.rpc('tools/call',{'name':'ui_fonts','arguments':{'action':'set','kind':'code','font':'Nonexistent QA Font 123456'}})
    check('unlisted font is rejected without changing selection',bool(error.get('isError')) and c.tool('ui_fonts',{'action':'state'})['codeFont']==code)
    c.tool('ui_command',{'command':'close_dialog'})
finally:c.close()
c=Client()
try:
    c.tool('ui_command',{'command':'preferences','page':'appearance'})
    state=c.tool('ui_fonts',{'action':'state'})
    check('both font selections survive restart',state['interfaceFont']==ui and state['codeFont']==code and state['controls']=={'interfaceValue':ui,'codeValue':code})
    for kind in ['interface','code']:
        state=c.tool('ui_fonts',{'action':'set','kind':kind,'font':''})
        check('restore '+kind+' default through the actual dropdown',state['controls'][kind+'Value']=='' and state[kind+'Font']=='')
    check('restoring both defaults restores bundled code font', 'JetBrains Mono' in state['renderedCodeFont'] and 'Segoe UI' in state['renderedInterfaceFont'])
    c.tool('ui_command',{'command':'close_dialog'})
finally:c.close()
preference=json.loads(config.read_text('utf-8-sig'));preference['DefaultFontFamily']='Segoe UI, Microsoft YaHei UI';config.write_text(json.dumps(preference),'utf-8')
c=Client()
try:
    c.tool('ui_command',{'command':'preferences','page':'appearance'})
    state=c.tool('ui_fonts',{'action':'state'})
    check('legacy custom fallback font list remains selected',state['interfaceFont']=='Segoe UI, Microsoft YaHei UI' and state['controls']['interfaceValue']==state['interfaceFont'])
    c.tool('ui_command',{'command':'close_dialog'})
finally:c.close()
(out/'verification.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'chosenInterfaceFont':ui,'chosenCodeFont':code},indent=2),'utf-8')
print('PASS font verification complete',len(checks),flush=True)
