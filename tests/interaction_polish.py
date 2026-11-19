"""Actual routed tab drops and owner-bounded window movement in an isolated GUI."""
import base64,json,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from qa_cleanup import new_fixture,remove_fixture,verify_process

exe,out=(Path(v).resolve() for v in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit interaction fixture ');repo=root/'Demo';repo.mkdir()
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
profile=exe.parent/'data/preference.json';profile.parent.mkdir(parents=True,exist_ok=True);assert not profile.exists()
profile.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False,'UseFixedTabWidth':True}),encoding='utf-8')
checks=[];failure=None;process=None;pid=None;seq=0;q=queue.Queue()
def git(*args,cwd=repo,success=True):
 p=subprocess.run(['git','-C',str(cwd),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert not success or p.returncode==0,p.stderr;return p.stdout.strip()
def rpc(method,params):
 global seq
 seq+=1;process.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');process.stdin.flush();r=json.loads(q.get(timeout=45));assert r.get('id')==seq and 'error' not in r,r;return r['result']
def tool(name,**args):
 r=rpc('tools/call',{'name':name,'arguments':args});assert not r.get('isError'),r
 return base64.b64decode(r['content'][0]['data']) if name=='ui_screenshot' else json.loads(r['content'][0]['text'])
def probe(action='inspect',value=0,**args):return tool('ui_layout',action='test_menu',text=action,value=value,**args)
def wait(f):
 for _ in range(200):
  value=f()
  if value:return value
  time.sleep(.08)
 raise AssertionError('Expected interface state did not appear')
def check(label,condition,data=None):
 assert condition,(label,data);checks.append(label);print('PASS',label,flush=True)
def tabs():return probe()['tabState']
def selected(s,page):return s['active']==s['selected']==s['displayed']==page and s['selectedContainers']==[page]
def marker_inside(s):return s['markerVisible'] and s['marker']['width']>=3 and s['marker']['x']>=s['viewport']['x']-1 and s['marker']['right']<=s['viewport']['right']+1
def drag(action,source,target,side):return probe(action,source,button=str(target)+','+side)['tabState']
def owned():return probe('owner_inspect')
def inside(d):
 a=d['owner'];return d['x']>=a['x']-1 and d['y']>=a['y']-1 and d['x']+d['width']<=a['x']+a['width']+1 and d['y']+d['height']<=a['y']+a['height']+1
def same(a,b):return abs(a['x']-b['x'])<=1 and abs(a['y']-b['y'])<=1
def exercise(label,width,height):
 initial=wait(lambda:owned() if tool('ui_state')['dialogs'] else None)
 check(label+' starts inside its owner',inside(initial) and (not os.environ.get('TREEGIT_LOCAL_QA_ROOT') or probe()['localWindowsPrivate'] is True),initial)
 body=probe('owner_body_drag',4);check(label+' body does not start dragging',same(initial,body))
 moved=probe('owner_drag',4);check(label+' title movement remains within owner',inside(moved),moved)
 lost=probe('owner_capture_lost',4);check(label+' lost capture stops movement',same(moved,lost))
 for corner in range(4):
  result=probe('owner_drag',corner);check(label+' corner '+str(corner)+' is clamped to owner',inside(result),result)
 tool('ui_window',width=1024,height=640)
 result=wait(lambda:(v if inside(v) else None) if (v:=owned()) else None)
 check(label+' follows owner resizing',inside(result),result)
 tool('ui_window',width=width,height=height)
def close():tool('ui_command',command='close_dialog');wait(lambda:not tool('ui_state')['dialogs'])

try:
 git('init','-b','main');git('config','user.name','TreeGit Demo');git('config','user.email','demo@example.invalid')
 (repo/'src').mkdir();(repo/'src/example.txt').write_text('initial\n',encoding='utf-8');(repo/'example.bin').write_bytes(bytes(range(32)))
 git('add','.');git('commit','-m','Initial example');base=git('rev-parse','HEAD')
 (repo/'src/example.txt').write_text('updated\n',encoding='utf-8');(repo/'example.bin').write_bytes(bytes(range(31,-1,-1)))
 git('add','.');git('commit','-m','Update example');head=git('rev-parse','HEAD');git('remote','add','origin','https://example.invalid/demo.git')
 original=[head,git('ls-files','--stage'),git('status','--porcelain')]
 log=(out/'mcp.log').open('w',encoding='utf-8');process=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
 threading.Thread(target=lambda:[q.put(line) for line in process.stdout],daemon=True).start();rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];verify_process(pid,exe)
 tool('ui_window',width=1280,height=820,state='normal',zoom=1)
 for _ in range(2):tool('ui_command',command='new_tab')
 for theme in ['Light','Dark','Blue']:
  tool('ui_window',theme=theme)
  for source,target,side in [(0,2,'after'),(2,0,'before'),(1,1,'after'),(0,1,'after'),(2,1,'before')]:
   before=tabs();page=before['order'][source];hover=drag('tab_drag_over',source,target,side)
   check(theme+' insertion marker '+str((source,target,side)),marker_inside(hover) and hover['order']==before['order'],hover)
   if source==0 and target==2:(out/(theme+'-tab-insertion.png')).write_bytes(tool('ui_screenshot'))
   expected=before['order'].copy();expected.pop(source);slot=target+(side=='after');expected.insert(slot-(slot>source),page)
   drag('tab_drop',source,target,side);after=wait(lambda:s if selected(s:=tabs(),page) else None)
   check(theme+' exact insertion order '+str((source,target,side)),after['order']==expected,after)
   check(theme+' selected tab matches displayed content '+str((source,target,side)),selected(after,page),after)
   check(theme+' drop removes the insertion marker',not after['markerVisible'])
  before=tabs();drag('tab_drag_over',0,2,'after');leave=drag('tab_drag_leave',0,2,'after')
  check(theme+' cancelled drag clears marker without changing order',not leave['markerVisible'] and leave['order']==before['order'])
  external=drag('tab_drag_external',0,2,'after');check(theme+' unrelated drop is rejected',not external['markerVisible'] and external['order']==before['order'])
 for _ in range(7):tool('ui_command',command='new_tab')
 tool('ui_window',width=1024);last=len(tabs()['order'])-1;tool('ui_tabs',action='select',index=last)
 before=tabs();hover=drag('tab_drag_over',0,last,'after');check('overflow insertion marker stays inside the visible tab viewport',marker_inside(hover),hover)
 page=before['order'][0];drag('tab_drop',0,last,'after');after=wait(lambda:s if selected(s:=tabs(),page) else None)
 check('overflow drop selects the moved tab at the end',after['order'][-1]==page and selected(after,page))
 tool('ui_command',command='close_tab');after=wait(lambda:s if len((s:=tabs())['order'])==len(before['order'])-1 else None)
 check('closing the moved tab keeps visual selection synchronized',page not in after['order'] and selected(after,after['active']),after)
 tool('ui_open_repository',repo=str(repo));wait(lambda:any(Path(r['repo']).resolve()==repo and not r['graphLoading'] for r in tool('ui_state')['repositories']))
 tool('ui_select_commit',repo=str(repo),revision=head)
 commands=['preferences','about','mcp_connection','ssh_keys','repository_settings','repository_settings_advanced','repository_settings_remotes','credentials','file_history','directory_history','git_logs','interactive_rebase']
 for locale,zoom,width,height in [('zh_CN',1,1280,820),('en_US',1.5,1024,700),('zh_TW',2,1100,720)]:
  tool('ui_window',locale=locale,zoom=zoom,width=width,height=height)
  for command in commands:
   args={'command':command,'repo':str(repo)}
   if command in ['repository_settings_advanced','repository_settings_remotes']:tool('ui_command',command='repository_settings',repo=str(repo));wait(lambda:tool('ui_state')['dialogs'])
   if command in ['file_history','directory_history']:args['file']='src/example.txt' if command=='file_history' else 'src'
   if command=='interactive_rebase':args['base']=base
   tool('ui_command',**args);exercise(locale+' '+command,width,height)
   if command in ['preferences','repository_settings_advanced']:(out/(locale+'-'+command+'.png')).write_bytes(tool('ui_screenshot',window='active'))
   close()
 tool('ui_window',locale='zh_CN',zoom=1,width=1280,height=820)
 tool('ui_command',command='repository_settings',repo=str(repo));wait(lambda:tool('ui_state')['dialogs']);tool('ui_command',command='repository_settings_remotes');parent=owned()
 tool('ui_layout',window='active',action='test_click',button='RepositorySettingsCredentials');wait(lambda:len(tool('ui_state')['dialogs'])==2);child=owned()
 check('nested credential dialog uses its immediate owner',same(child['owner'],parent) and abs(child['owner']['width']-parent['width'])<2,child)
 for corner in range(4):check('nested dialog corner '+str(corner),inside(probe('owner_drag',corner)))
 tool('ui_command',command='close_dialog');close()
 tool('ui_select_commit',repo=str(repo),revision=head);tool('ui_diff',repo=str(repo),action='select',file='example.bin',source='history');wait(lambda:tool('ui_diff',repo=str(repo),action='state').get('status')=='ready')
 tool('ui_diff',repo=str(repo),action='window');exercise('native binary comparison',1280,820)
 before=owned();probe('owner_maximize');maximized=wait(lambda:owned())
 check('owned binary inspector keeps its maximized state',maximized['state']=='Maximized',maximized)
 check('maximized binary inspector remains inside its owner',inside(maximized),maximized)
 probe('owner_restore');restored=wait(lambda:r if (r:=owned())['state']=='Normal' and abs(r['width']-before['width'])<=2 and abs(r['height']-before['height'])<=2 else None)
 check('owned binary inspector restores its original size',restored['state']=='Normal' and abs(restored['width']-before['width'])<=2 and abs(restored['height']-before['height'])<=2,restored)
 check('native inspector has no custom frame padding',restored['padding']=='0,0,0,0',restored)
 full_width=restored['maximum']['width']
 tool('ui_window',width=1024,height=640);wait(lambda:r if (r:=owned())['maximum']['width']<full_width-100 else None)
 reopened=probe('owner_hide_show');check('hidden and reopened inspector remains within its owner',inside(reopened),reopened)
 tool('ui_window',width=1280,height=820)
 expanded=wait(lambda:r if (r:=owned())['maximum']['width']>=full_width-2 else None)
 check('reopened inspector recovers the larger owner size limit',inside(expanded) and expanded['maximum']['width']>=full_width-2,expanded)
 close()
 conflict=root/'Conflict';conflict.mkdir()
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args,cwd=conflict)
 (conflict/'example.txt').write_text('base\n',encoding='utf-8');git('add','.',cwd=conflict);git('commit','-m','Base',cwd=conflict);git('switch','-c','side',cwd=conflict)
 (conflict/'example.txt').write_text('remote\n',encoding='utf-8');git('commit','-am','Remote example',cwd=conflict);git('switch','main',cwd=conflict)
 (conflict/'example.txt').write_text('local\n',encoding='utf-8');git('commit','-am','Local example',cwd=conflict);git('merge','side',cwd=conflict,success=False)
 tool('ui_open_repository',repo=str(conflict));wait(lambda:any(Path(r['repo']).resolve()==conflict and not r['graphLoading'] for r in tool('ui_state')['repositories']))
 tool('ui_command',command='merge_conflicts',repo=str(conflict),file='example.txt');exercise('merge conflict editor',1280,820);close()
 check('all read-only dialog checks preserve the original repository',original==[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')])
 assert len(checks)==423,len(checks)
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
 if pid:
  try:(out/'failure.png').write_bytes(tool('ui_screenshot',window='active'));(out/'failure-state.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2),encoding='utf-8')
  except Exception:pass
finally:
 if pid:
  verify_process(pid,exe)
  command=f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{$null=$p.CloseMainWindow();if(-not $p.WaitForExit(5000)){{$p.Kill();$p.WaitForExit()}}}}"
  subprocess.run(['powershell','-NoProfile','-EncodedCommand',base64.b64encode(command.encode('utf-16-le')).decode()],check=True,capture_output=True)
 if process:
  process.stdin.close()
  try:process.wait(timeout=5)
  except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
 if profile.exists():profile.unlink()
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
 remove_fixture(root,out,'TreeGit interaction fixture ')
if failure:raise SystemExit(1)
print(json.dumps({'passed':len(checks),'failure':None,'nativeInput':False}))
