"""Distinguish complete clones from failures in requested submodule setup."""
import json,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from qa_cleanup import new_fixture,remove_fixture,verify_process
from qa_process import close_owned_gui

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit clone results ');source=root/'Source';child=root/'Child';remote=root/'Remote.git'
for p in [source,child]:p.mkdir()
config=root/'gitconfig';config.write_text('[protocol "file"]\n\tallow = always\n','utf-8')
env={**os.environ,'GIT_CONFIG_GLOBAL':str(config),'GIT_CONFIG_NOSYSTEM':'1'}
checks=[];failure=None;p=None;pid=None;seq=0;q=queue.Queue()
def git(repo,*args):
 result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert result.returncode==0,result.stderr;return result.stdout.strip()
def check(name,value):
 assert value,name
 checks.append(name);print('PASS',name,flush=True)
def rpc(method,params):
 global seq
 seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
 result=json.loads(q.get(timeout=60));assert 'error' not in result,result;return result['result']
def tool(name,args=None,error=False):
 result=rpc('tools/call',{'name':name,'arguments':args or {}});assert bool(result.get('isError'))==error,result;return json.loads(result['content'][0]['text'])
def clone(target,recursive,error=False):return tool('git_clone',{'url':str(remote),'destination':str(target),'recursive':recursive},error)
def close_result():return tool('ui_command',{'command':'cancel_popup'})['cancelled']
try:
 for repo in [source,child]:
  git(repo,'init','-b','main');git(repo,'config','user.name','TreeGit Demo');git(repo,'config','user.email','demo@example.invalid')
  (repo/'example.txt').write_text('Anonymous example\n','utf-8');git(repo,'add','.');git(repo,'commit','-m','Initial example')
 git(source,'submodule','add',str(child),'modules/child');git(source,'commit','-m','Add example submodule')
 git(source,'clone','--bare',str(source),str(remote))
 p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
 threading.Thread(target=lambda:[q.put(line) for line in p.stdout],daemon=True).start();rpc('initialize',{'protocolVersion':'2025-11-25'})
 pid=tool('ui_state')['processId'];verify_process(pid,exe)
 plain=root/'Without submodules';result=clone(plain,False)
 check('clone without requested submodules reports success',result['isSuccess'] and git(plain,'rev-parse','HEAD')==git(source,'rev-parse','HEAD'))
 check('success retains the result panel until explicitly closed',result['progressVisible'] and close_result())
 full=root/'With submodules';result=clone(full,True)
 check('recursive clone initializes the actual submodule',result['isSuccess'] and (full/'modules/child/example.txt').read_text('utf-8')=='Anonymous example\n')
 check('completed recursive clone result closes',close_result())
 git(source,'config','-f','.gitmodules','submodule.modules/child.url',str(root/'Missing.git'));git(source,'commit','-am','Unavailable example submodule');git(source,'push',str(remote),'main')
 partial=root/'Incomplete submodules';result=clone(partial,True,True)
 check('requested submodule failure is reported as incomplete',not result['isSuccess'])
 check('the successfully cloned main repository is preserved',git(partial,'rev-parse','HEAD')==git(source,'rev-parse','HEAD') and (partial/'example.txt').is_file())
 check('incomplete submodules remain visible for recovery',git(partial,'submodule','status').startswith('-'))
 check('partial clone result remains reviewable',result['progressVisible'])
 check('a partial clone can close its result without recloning',close_result())
 state=tool('ui_state')
 check('the available repository remains open after the incomplete setup',any(Path(r['repo'])==partial for r in state['repositories']))
 git(partial,'config','submodule.modules/child.url',str(child))
 repaired=tool('git_run',{'repo':str(partial),'arguments':['submodule','update','--init','--recursive']})
 check('the missing submodule can be completed without repeating the clone',repaired['isSuccess'] and (partial/'modules/child/example.txt').is_file())
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
finally:
 if pid:close_owned_gui(exe,pid)
 if p:
  p.stdin.close()
  try:p.wait(timeout=10)
  except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},indent=2),'utf-8')
 for attempt in range(50):
  try:remove_fixture(root,out,'TreeGit clone results ');break
  except PermissionError:
   if attempt==49:raise
   time.sleep(.1)
if failure:raise SystemExit(1)
