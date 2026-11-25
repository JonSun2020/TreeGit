"""Exercise real GUI startup with/without discovered system Git. Integration build only."""
import atexit,json,os,queue,shutil,subprocess,sys,tempfile,threading,time,traceback
from pathlib import Path
from qa_cleanup import new_fixture, remove_fixture, verify_local_runtime
from qa_process import close_owned_gui
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
if os.environ.get('TREEGIT_LOCAL_QA_ROOT'):verify_local_runtime(exe)
else:assert os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_OS')=='Windows', 'Destructive runtime tests require an owned CI runner'
root=new_fixture(out,'TreeGit Git runtime 中文 ');bundled=exe.parent/'git/cmd/git.exe'
checks=[];settings_samples=[];failure=None
def save():
 (out/'git-runtime-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'settingsSamples':settings_samples,'root':str(root),'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
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
bundle_version=git(repo,'version').removeprefix('git version ')
class Client:
 def __init__(self,no_system,saved=None,shell=0,force=False):
  data=exe.parent/'data';data.mkdir(exist_ok=True)
  (data/'preference.json').write_text(json.dumps({'Locale':'zh_CN','UseBundledGit':force,'GitInstallPath':str(saved) if saved else None,'ShellOrTerminalType':shell,'ShellOrTerminalPath':'Z:/old-computer/git-bash.exe','Workspaces':[]}),encoding='utf-8')
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
  close_owned_gui(exe,self.pid)
def check_settings(client,label,bundle_available):
 started=time.monotonic()
 client.tool('ui_command',{'command':'preferences','page':'git'})
 try:
  while time.monotonic()-started<15:
   layout=client.tool('ui_layout',{'action':'list','window':'active'})
   choice=next((b for b in layout['buttons'] if b['name']=='UseBundledGitChoice'),None)
   texts=layout['localizedTexts']
   version=next((t['text'] for t in texts if t['name']=='BundledGitVersionLabel'),None)
   missing=any(t['text'].strip()=='BundledGit' for t in texts)
   if choice and (choice['enabled'],version,missing)==(bundle_available,'- '+bundle_version if bundle_available else None,not bundle_available):
    break
   time.sleep(.05)
  else:raise AssertionError((label,choice,version,missing))
  settings_samples.append({'case':label,'available':bundle_available,'version':version,'readyMs':round(1000*(time.monotonic()-started))})
  check(label+' renders the bundled version and availability together',True)
 finally:client.tool('ui_command',{'command':'close_dialog'})
def scenario(label,no_system,saved,expected_bundled,operations=False,bundle_available=None):
 c=Client(no_system,saved)
 try:
  info=c.state['git'];check(label+' selects the expected runtime',info['bundled']==expected_bundled and bool(info['path']))
  if expected_bundled:check(label+' uses the current application directory',Path(info['path'])==bundled)
  if bundle_available is not None:check_settings(c,label,bundle_available)
  if operations:
   check('an unavailable saved Git Bash falls back to a usable Windows terminal',Path(c.state['terminal']['path']).is_file() and not c.state['terminal']['path'].lower().endswith('git-bash.exe'))
   c.tool('ui_open_repository',{'repo':str(repo)})
   raw=lambda args:c.tool('git_run',{'repo':str(repo),'arguments':args})
   (repo/'中文 file.txt').write_text('runtime commit\n',encoding='utf-8')
   check('bundled Git stages and commits without Git on PATH',c.tool('git_action',{'repo':str(repo),'action':'stage','paths':['中文 file.txt']})['isSuccess'] and c.tool('git_action',{'repo':str(repo),'action':'commit','message':'Bundled runtime commit'})['isSuccess'] and git(repo,'show','HEAD:中文 file.txt')=='runtime commit')
   check('bundled LFS is available without system LFS','3.8.1' in raw(['lfs','version'])['stdout'])
   check('bundled Git Flow is available without system Git Flow','git-flow-next' in raw(['flow','version'])['stdout'])
   ssh=raw(['-c','alias.treegit-runtime=!ssh -V','treegit-runtime']);check('bundled OpenSSH and OpenSSL run with a stripped PATH','OpenSSH' in ssh['stderr'] and 'OpenSSL' in ssh['stderr'])
   remote=root/'remote.git';git(root,'init','--bare',str(remote));git(remote,'symbolic-ref','HEAD','refs/heads/main');git(repo,'remote','add','origin',str(remote))
   check('bundled Git pushes real commits',raw(['push','-u','origin','main'])['isSuccess'] and git(remote,'rev-parse','main')==git(repo,'rev-parse','HEAD'))
   clone=root/'clone 中文';cloned=c.tool('git_clone',{'url':str(remote),'destination':str(clone),'recursive':False})
   check('bundled GUI clone works',cloned['isSuccess'] and (clone/'中文 file.txt').read_text('utf-8')=='runtime commit\n')
   check('a retained success panel does not make MCP report clone failure',cloned['progressVisible'] and git(clone,'rev-parse','HEAD')==git(repo,'rev-parse','HEAD'))
   check('the successful clone result can close without replaying the clone',c.tool('ui_command',{'command':'cancel_popup'})['cancelled'] and not c.tool('ui_state')['layoutProbe']['popups'])
 finally:c.close()
scenario('installed Git',False,None,False,bundle_available=True)
scenario('missing saved path',True,root/'old-computer/git.exe',True,True,bundle_available=True)
scenario('legacy automatically selected bundle detects a new system installation',False,bundled,False)
c=Client(False,saved=shutil.which("git"),force=True)
try:
 check('explicit bundled mode overrides an available saved system Git',c.state['git']['bundled'])
finally:c.close()
c=Client(True,shell=999)
try:check('an out-of-range saved terminal selection recovers',Path(c.state['terminal']['path']).is_file())
finally:c.close()
if len(sys.argv)>4:
 started=time.monotonic();scenario('hanging saved executable',True,Path(sys.argv[4]).resolve(),True)
 check('a stale hanging selection cannot stall startup',time.monotonic()-started<20)
manual=Path(sys.argv[3]).resolve() if len(sys.argv)>3 else None
if manual:
 c=Client(False,manual)
 try:check('a valid explicitly selected external Git is retained',Path(c.state['git']['path'])==manual)
 finally:c.close()
old_host=root/'old portable installation';old_host.mkdir();shutil.copytree(exe.parent/'git',old_host/'git')
old_git=old_host/'git/cmd/git.exe'
c=Client(True,old_git)
try:
 check('the disposable external Git is active before removal',Path(c.state['git']['path'])==old_git)
 c.tool('ui_open_repository',{'repo':str(repo)})
 held=old_git.with_suffix('.held');old_git.rename(held)
 try:
  result=c.tool('git_run',{'repo':str(repo),'arguments':['rev-parse','HEAD']})
  state=c.tool('ui_state')
  check('removing external Git while open switches runtime without restarting',result['isSuccess'] and result['stdout'].strip()==git(repo,'rev-parse','HEAD') and state['processId']==c.pid and Path(state['git']['path'])==bundled)
  data=exe.parent/'data'/'preference.json'
  for _ in range(30):
   saved=json.loads(data.read_text(encoding='utf-8-sig'))
   if saved.get('UseBundledGit'):break
   time.sleep(.1)
  check('automatic fallback persists the checkbox and preserves the external path',saved.get('UseBundledGit') and Path(saved['GitInstallPath'])==old_git)
 finally:held.rename(old_git)
finally:c.close()
(old_host/'TreeGit.exe').write_bytes(b'owned old portable installation marker')
c=Client(False,old_git)
try:check('a copied old bundle cannot hide a system installation',Path(c.state['git']['path'])!=old_git and not c.state['git']['bundled'])
finally:c.close()
scenario('a copied old bundle relocates to the current directory',True,old_git,True)
ssh=exe.parent/'git/usr/bin/ssh.exe';held=ssh.with_suffix('.held');ssh.rename(held)
try:
 scenario('system Git with incomplete unused bundle',False,None,False,bundle_available=False)
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
remove_fixture(root,out,'TreeGit Git runtime 中文 ')
