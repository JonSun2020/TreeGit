"""Actual Tools entry, isolated integration settings and confirm/cancel persistence."""
import json,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from qa_cleanup import new_fixture,remove_fixture,verify_process
from qa_process import close_owned_gui

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit integration settings ');repo=root/'Demo';repo.mkdir()
global_config=root/'gitconfig';global_config.write_text('[user]\n\tname = Existing Demo\n\temail = existing@example.invalid\n[core]\n\tautocrlf = false\n','utf-8')
original=global_config.read_bytes()
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','GIT_CONFIG_GLOBAL':str(global_config),'GIT_CONFIG_NOSYSTEM':'1'}
subprocess.run(['git','-C',str(repo),'init','-b','main'],env=env,capture_output=True,check=True)
checks=[];failure=None;client=None
class Client:
 def __init__(self):
  self.p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
  self.q=queue.Queue();self.seq=0;self.pid=None
  threading.Thread(target=lambda:[self.q.put(line) for line in self.p.stdout],daemon=True).start();self.rpc('initialize',{'protocolVersion':'2025-11-25'})
  self.pid=self.tool('ui_state')['processId'];verify_process(self.pid,exe)
 def rpc(self,method,params):
  self.seq+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.seq,'method':method,'params':params})+'\n');self.p.stdin.flush()
  result=json.loads(self.q.get(timeout=45));assert 'error' not in result,result;return result['result']
 def tool(self,name,**args):
  result=self.rpc('tools/call',{'name':name,'arguments':args});assert not result.get('isError'),result;return json.loads(result['content'][0]['text'])
 def close(self):
  if self.pid:close_owned_gui(exe,self.pid);self.pid=None
  self.p.stdin.close()
  try:self.p.wait(timeout=10)
  except subprocess.TimeoutExpired:self.p.terminate();self.p.wait(timeout=10)
def check(name,value):
 assert value,name
 checks.append(name);print('PASS',name,flush=True)
def wait(callback):
 for _ in range(160):
  result=callback()
  if result:return result
  time.sleep(.05)
 raise AssertionError('Expected settings state did not appear')
def state():return client.tool('ui_state')
def open_tools():
 route=client.tool('ui_layout',window='main',action='test_menu',text='preferences_open_from_tools')
 check('Last Tools entry opens the integration page',route['last'] and route['parameter']=='integration' and route['enabled'])
 wait(lambda:state()['dialogs'])
def close(button):
 client.tool('ui_layout',window='active',action='test_click',button=button)
 wait(lambda:not state()['dialogs'])
def edit_tool(index,path=''):
 client.tool('ui_layout',window='active',action='test_set',button='ExternalToolSelector',value=index)
 if index:client.tool('ui_layout',window='active',action='test_set',button='ExternalToolPath',text=path)
def trap_identity():client.tool('ui_layout',window='main',action='test_menu',text='preferences_identity_trap')
profile=exe.parent/'data/preference.json'
try:
 client=Client();client.tool('ui_open_repository',repo=str(repo))
 for locale in ['zh_CN','zh_TW','en_US']:
  for theme in ['Light','Dark','Blue']:
   client.tool('ui_window',locale=locale,theme=theme,zoom=1.5,width=1100,height=750)
   client.tool('ui_command',command='preferences',page='general');wait(lambda:state()['dialogs'])
   tabs=state()['layoutProbe']['dialogs'][0]['tabs'][0]
   check(locale+' '+theme+' general preferences excludes integrations',len(tabs['visibleHeaders'])==3)
   close('CancelPreferencesButton')
   before=json.loads(profile.read_text('utf-8'))
   open_tools();dialog=state()['layoutProbe']['dialogs'][0]
   check(locale+' '+theme+' tool dialog has no tab strip',not dialog['tabs'] and bool(dialog['content']))
   check(locale+' '+theme+' integration title matches its caption',dialog['header']==state()['dialogs'][0]['title'])
   fill,text={'Light':('#FFA7DFCB','#FF333333'),'Dark':('#FF16836D','#FFDFDFDF'),'Blue':('#FFB6D9FA','#FF333333')}[theme]
   check(locale+' '+theme+' actual confirmation label and fill '+str(dialog['confirm']),dialog['confirm']['fill'].upper()==fill and dialog['confirm']['text'].upper()==text)
   edit_tool(6,'not-saved-tool.exe');trap_identity();close('CancelPreferencesButton')
   after=json.loads(profile.read_text('utf-8'))
   check(locale+' '+theme+' cancel restores tool configuration',after['ExternalMergeToolType']==before['ExternalMergeToolType'] and after['ExternalMergeToolPath']==before['ExternalMergeToolPath'])
   open_tools();edit_tool(6,'accepted-tool.exe');trap_identity();close('SavePreferencesButton')
   after=json.loads(profile.read_text('utf-8'))
   check(locale+' '+theme+' confirm saves tool configuration and closes',after['ExternalMergeToolType']==6 and after['ExternalMergeToolPath']=='accepted-tool.exe')
   check(locale+' '+theme+' integration settings preserve hidden signing configuration',global_config.read_bytes()==original)
 client.close();client=Client();open_tools()
 check('integration settings survive restarting',json.loads(profile.read_text('utf-8'))['ExternalMergeToolPath']=='accepted-tool.exe')
 close('CancelPreferencesButton')
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
finally:
 if client:client.close()
 remove_fixture(root,out,'TreeGit integration settings ')
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},indent=2),'utf-8')
if failure:raise SystemExit(1)
