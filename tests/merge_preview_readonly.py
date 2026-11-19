"""Opening/cancelling a merge preview must not write repository data or run drivers."""
from pathlib import Path
import hashlib,json,os,queue,subprocess,sys,tempfile,threading,time,traceback
from qa_cleanup import new_fixture,remove_fixture,verify_local_runtime
from qa_process import close_owned_gui
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
if os.name=='nt':verify_local_runtime(exe)
else:assert os.environ.get('CI')=='true' and not os.environ.get('GITHUB_ACTIONS')
parent=Path(tempfile.gettempdir()).resolve();root=new_fixture(parent,'TreeGit merge preview ');repo=root/'repo ; \u4e2d\u6587';repo.mkdir();checks=[];failures=[];failure=None;pid=None;p=None
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1','GIT_OPTIONAL_LOCKS':'0'}
def git(*args):
 r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
 assert r.returncode==0,r.stderr
 return r.stdout.strip()
def files(folder):return {str(p.relative_to(folder)):hashlib.sha256(p.read_bytes()).hexdigest() for p in folder.rglob('*') if p.is_file()}
def snapshot():
 gitdir=Path(git('rev-parse','--absolute-git-dir'));objects=Path(git('rev-parse','--path-format=absolute','--git-path','objects'))
 common=Path(git('rev-parse','--path-format=absolute','--git-common-dir'))
 return {'head':git('rev-parse','HEAD'),'refs':git('show-ref'),'index':hashlib.sha256((gitdir/'index').read_bytes()).hexdigest(),'config':hashlib.sha256((common/'config').read_bytes()).hexdigest(),'objects':files(objects),'work':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in repo.iterdir() if p.is_file() and p.name!='.git'}}
try:
 git('init','-b','main',*(['--object-format=sha256'] if '--sha256' in sys.argv else []),*(['--ref-format=reftable'] if '--reftable' in sys.argv else []));git('config','user.name','Preview QA');git('config','user.email','preview@example.invalid');git('config','gc.auto','0')
 (repo/'a.txt').write_text('one\ntwo\nthree\nfour\nfive\n');git('add','.');git('commit','-m','Base');git('checkout','-b','feature')
 (repo/'a.txt').write_text('one\ntwo\nthree\nremote\nfive\n');git('commit','-am','Remote');target=git('rev-parse','HEAD');git('checkout','main')
 (repo/'a.txt').write_text('one\nlocal\nthree\nfour\nfive\n');git('commit','-am','Local')
 if '--worktree' in sys.argv:
  worktree=root/'working tree ; \u4e2d\u6587';git('worktree','add','-b','preview-worktree',str(worktree));repo=worktree
 current=git('symbolic-ref','--short','HEAD')
 p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
 replies=queue.Queue();threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start();seq=0
 def rpc(name,args):
  global seq
  seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':name,'params':args})+'\n');p.stdin.flush();r=json.loads(replies.get(timeout=30));assert 'error' not in r,r;return r['result']
 def tool(name,args=None):
  r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r;return json.loads(r['content'][0]['text'])
 def state():return next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
 def preview():
  tool('ui_command',{'repo':str(repo),'command':'merge_dialog','revision':target})
  for _ in range(100):
   s=state();m=s.get('mergeDialog')
   if m and m['testing'] not in ['Testing','Disabled']:break
   time.sleep(.05)
  else:raise AssertionError('Preview did not settle')
  tool('ui_command',{'repo':str(repo),'command':'cancel_popup'});return m['testing']
 rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
 for _ in range(100):
  if state()['workingCopyStatusKnown'] and not state()['graphLoading']:break
  time.sleep(.05)
 before=snapshot();result=preview();after=snapshot()
 (out/'clean-preview-state.json').write_text(json.dumps({'testing':result,'changed':[k for k in before if before[k]!=after[k]],'objectCountBefore':len(before['objects']),'objectCountAfter':len(after['objects'])},indent=2),'utf-8')
 if before!=after:failures.append(('Opening a merge preview wrote data',[k for k in before if before[k]!=after[k]]))
 elif result!='NoConflicts':failures.append(('Clean preview result',result))
 else:checks.append('clean merge preview leaves refs HEAD index worktree and object storage unchanged')
 (repo/'.gitattributes').write_text('a.txt merge=preview-qa\n');git('add','.gitattributes');git('commit','-m','Custom driver')
 driver=root/'driver.py';driver.write_text("from pathlib import Path\nimport sys\n(Path.cwd()/'preview-side-effect.txt').write_text('should not run while previewing')\nsys.exit(1)\n")
 git('config','merge.preview-qa.driver','"'+sys.executable.replace('\\','/')+'" "'+str(driver).replace('\\','/')+'"')
 tool('ui_refresh',{'repo':str(repo)});time.sleep(.2);before=snapshot();result=preview();after=snapshot()
 (out/'custom-driver-state.json').write_text(json.dumps({'testing':result,'changed':[k for k in before if before[k]!=after[k]],'driverRan':(repo/'preview-side-effect.txt').exists()},indent=2),'utf-8')
 if before!=after:failures.append(('A merge preview ran a custom driver',[k for k in before if before[k]!=after[k]]))
 elif result!='UnknownError':failures.append(('Custom driver preview should report unknown',result))
 else:checks.append('custom merge drivers are not executed by read-only preview')
 rpc('tools/call',{'name':'git_action','arguments':{'repo':str(repo),'action':'merge','revision':target}})
 assert (repo/'preview-side-effect.txt').exists() and git('ls-files','-u')
 git('merge','--abort');(repo/'preview-side-effect.txt').unlink()
 checks.append('an explicitly requested real merge retains the configured driver')
 git('config','--unset','merge.preview-qa.driver')
 for key in ['merge.renormalize','remote.preview-qa.promisor']:
  git('config',key,'true');before=snapshot();result=preview();after=snapshot()
  assert before==after and result=='UnknownError',(key,result)
  checks.append(key+' does not run a write-capable preview');git('config','--unset',key)
 git('checkout','feature');(repo/'a.txt').write_text('one\nconflicting remote\nthree\nremote\nfive\n');git('commit','-am','Conflicting remote');target=git('rev-parse','HEAD');git('checkout',current)
 tool('ui_refresh',{'repo':str(repo)});time.sleep(.2);before=snapshot();result=preview();after=snapshot()
 assert before==after and result=='WillCauseConflicts',result
 checks.append('conflict preview reports conflicts without changing the object store or files')
 before=snapshot()
 for _ in range(20):
  tool('ui_command',{'repo':str(repo),'command':'merge_dialog','revision':target});tool('ui_command',{'repo':str(repo),'command':'cancel_popup'})
 for _ in range(100):
  if not list(parent.glob('TreeGit-merge-preview-*')):break
  time.sleep(.05)
 assert before==snapshot() and not list(parent.glob('TreeGit-merge-preview-*'))
 checks.append('twenty cancelled previews preserve state and leave no quarantine folders')
 assert not failures,failures
except BaseException:failure=traceback.format_exc();raise
finally:
 if p:
  p.stdin.close()
  try:p.wait(timeout=10)
  except subprocess.TimeoutExpired:p.kill();p.wait()
 if pid:close_owned_gui(exe,pid)
 remove_fixture(root,parent,'TreeGit merge preview ')
 (out/'merge-preview-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'fixtureRemoved':not root.exists()},indent=2),'utf-8')
