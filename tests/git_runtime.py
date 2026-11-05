"""Exercise real GUI startup with/without discovered system Git. Integration build only."""
import atexit,json,os,queue,shutil,subprocess,sys,tempfile,threading,time,traceback
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit Git runtime 中文 '));bundled=exe.parent/'git/cmd/git.exe'
checks=[];failure=None
def save():
 (out/'git-runtime-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'root':str(root),'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
def record_error(kind,value,tb):
 global failure
 failure=''.join(traceback.format_exception(kind,value,tb));sys.__excepthook__(kind,value,tb)
sys.excepthook=record_error;atexit.register(save)
def cleanup_owned():
 script="$owned=Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '"+str(exe).replace("'","''")+"' }; foreach($p in $owned){Stop-Process -InputObject $p -ErrorAction SilentlyContinue; try{$null=$p.WaitForExit(10000)}catch{}}"
 subprocess.run(['powershell','-NoProfile','-Command',script],capture_output=True)
atexit.register(cleanup_owned)
def check(name,condition):
 assert condition,name
 checks.append(name);print('PASS',name,flush=True)
def git(repo,*args):
 r=subprocess.run([str(bundled),'-C',str(repo),*args],capture_output=True,text=True,encoding='utf-8',env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global'),'GIT_CONFIG_NOSYSTEM':'1'})
 assert r.returncode==0,r.stderr
 return r.stdout.strip()
repo=root/'repo with 空格';repo.mkdir();git(repo,'init','-b','main');git(repo,'config','user.name','Runtime QA');git(repo,'config','user.email','qa@example.invalid')
(repo/'file.txt').write_text('initial\n');git(repo,'add','.');git(repo,'commit','-m','Initial')
class Client:
 def __init__(self,no_system,saved=None):
  data=exe.parent/'data';data.mkdir(exist_ok=True)
  (data/'preference.json').write_text(json.dumps({'Locale':'zh_CN','GitInstallPath':str(saved) if saved else None,'Workspaces':[]}),encoding='utf-8')
  env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global'),'GIT_CONFIG_NOSYSTEM':'1','PATH':str(Path(os.environ['SystemRoot'])/'System32')}
  if no_system:env['TREEGIT_TEST_NO_SYSTEM_GIT']='1'
  else:env.pop('TREEGIT_TEST_NO_SYSTEM_GIT',None)
  self.p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
  self.q=queue.Queue();threading.Thread(target=lambda:[self.q.put(l) for l in self.p.stdout],daemon=True).start();self.n=0
  self.call('initialize',{'protocolVersion':'2025-11-25'});self.state=self.tool('ui_state');self.pid=self.state['processId']
 def call(self,method,params):
  self.n+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.n,'method':method,'params':params})+'\n');self.p.stdin.flush()
  r=json.loads(self.q.get(timeout=45));assert 'error' not in r,r;return r['result']
 def tool(self,name,args=None,error=False):
  r=self.call('tools/call',{'name':name,'arguments':args or {}});assert bool(r.get('isError'))==error,r
  return r if error else json.loads(r['content'][0]['text'])
 def close(self):
  self.p.stdin.close();self.p.wait(timeout=15)
  script="$p=Get-Process -Id "+str(self.pid)+"; if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'wrong test process'}; Stop-Process -InputObject $p; $null=$p.WaitForExit(10000)"
  subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
def scenario(label,no_system,saved,expected_bundled,operations=False):
 c=Client(no_system,saved)
 try:
  info=c.state['git'];check(label+' selects the expected runtime',info['bundled']==expected_bundled and bool(info['path']))
  if expected_bundled:check(label+' uses the current application directory',Path(info['path'])==bundled)
  if operations:
   c.tool('ui_open_repository',{'repo':str(repo)})
   raw=lambda args:c.tool('git_run',{'repo':str(repo),'arguments':args})
   (repo/'中文 file.txt').write_text('runtime commit\n',encoding='utf-8')
   check('bundled Git stages and commits without Git on PATH',c.tool('git_action',{'repo':str(repo),'action':'stage','paths':['中文 file.txt']})['isSuccess'] and c.tool('git_action',{'repo':str(repo),'action':'commit','message':'Bundled runtime commit'})['isSuccess'] and git(repo,'show','HEAD:中文 file.txt')=='runtime commit')
   check('bundled LFS is available without system LFS','3.8.1' in raw(['lfs','version'])['stdout'])
   check('bundled Git Flow is available without system Git Flow','git-flow-next' in raw(['flow','version'])['stdout'])
   ssh=raw(['-c','alias.treegit-runtime=!ssh -V','treegit-runtime']);check('bundled OpenSSH and OpenSSL run with a stripped PATH','OpenSSH' in ssh['stderr'] and 'OpenSSL' in ssh['stderr'])
   remote=root/'remote.git';git(root,'init','--bare',str(remote));git(remote,'symbolic-ref','HEAD','refs/heads/main');git(repo,'remote','add','origin',str(remote))
   check('bundled Git pushes real commits',raw(['push','-u','origin','main'])['isSuccess'] and git(remote,'rev-parse','main')==git(repo,'rev-parse','HEAD'))
   clone=root/'clone 中文';check('bundled GUI clone works',c.tool('git_clone',{'url':str(remote),'destination':str(clone),'recursive':False})['isSuccess'] and (clone/'中文 file.txt').read_text('utf-8')=='runtime commit\n')
 finally:c.close()
scenario('installed Git',False,None,False)
scenario('missing saved path',True,root/'old-computer/git.exe',True,True)
scenario('new system installation after bundled use',False,bundled,False)
if len(sys.argv)>4:
 started=time.monotonic();scenario('hanging saved executable',True,Path(sys.argv[4]).resolve(),True)
 check('a stale hanging selection cannot stall startup',time.monotonic()-started<20)
manual=Path(sys.argv[3]).resolve() if len(sys.argv)>3 else None
if manual:
 c=Client(False,manual)
 try:check('a valid explicitly selected external Git is retained',Path(c.state['git']['path'])==manual)
 finally:c.close()
old_host=root/'old portable installation';old_host.mkdir();shutil.copytree(exe.parent/'git',old_host/'git')
(old_host/'TreeGit.exe').write_bytes(b'owned old portable installation marker');old_git=old_host/'git/cmd/git.exe'
c=Client(False,old_git)
try:check('a copied old bundle cannot hide a system installation',Path(c.state['git']['path'])!=old_git and not c.state['git']['bundled'])
finally:c.close()
scenario('a copied old bundle relocates to the current directory',True,old_git,True)
ssh=exe.parent/'git/usr/bin/ssh.exe';held=ssh.with_suffix('.held');ssh.rename(held)
try:
 scenario('system Git with incomplete unused bundle',False,None,False)
 c=Client(True)
 try:
  check('an incomplete required bundle is not silently accepted',not c.state['git']['path'])
  error=c.tool('ui_open_repository',{'repo':str(repo)},error=True)
  check('missing runtime produces an actionable extraction message','git 目录' in json.dumps(error,ensure_ascii=False))
 finally:c.close()
finally:held.rename(ssh)
held=bundled.with_suffix('.held');bundled.rename(held);bundled.write_bytes(b'broken executable fixture')
try:
 c=Client(True)
 try:check('a corrupt bundled Git is rejected without a GUI crash',not c.state['git']['path'])
 finally:c.close()
finally:bundled.unlink();held.rename(bundled)
