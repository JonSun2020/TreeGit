"""Regression: page dialogs lock tabs; closing a tab never exits the desktop."""
import base64,io,json,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from PIL import Image
from qa_cleanup import new_fixture,remove_fixture,verify_process
from qa_process import close_owned_gui

exe,out=(Path(v).resolve() for v in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit tab lifecycle ');repo=root/'Demo';repo.mkdir()
profile=exe.parent/'data/preference.json';profile.parent.mkdir(parents=True,exist_ok=True);assert not profile.exists()
profile.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False}),'utf-8')
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
q=queue.Queue();seq=0;p=None;pid=None;checks=[];failure=None
def git(*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env,stderr=subprocess.DEVNULL).decode('utf-8').strip()
def rpc(method,args):
 global seq
 seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':args})+'\n');p.stdin.flush();r=json.loads(q.get(timeout=40));assert 'error' not in r,r;return r['result']
def tool(name,**args):
 r=rpc('tools/call',{'name':name,'arguments':args});assert not r.get('isError'),r
 return base64.b64decode(r['content'][0]['data']) if name=='ui_screenshot' else json.loads(r['content'][0]['text'])
def probe(action='inspect',value=0,**args):return tool('ui_layout',action='test_menu',text=action,value=value,**args)
def tabs():return probe()['tabState']
def wait(f):
 for _ in range(150):
  r=f()
  if r:return r
  time.sleep(.05)
 raise AssertionError('Expected tab state did not appear')
def check(label,result,data=None):
 assert result,(label,data)
 checks.append(label)
def signature(t):return (t['order'],t['active'],t['displayed'],t['selected'])
def cancel():tool('ui_layout',action='test_click',button='CancelPopup');wait(lambda:tabs()['enabled'])
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args)
 (repo/'example.txt').write_text('unchanged demo\n','utf-8');git('add','.');git('commit','-m','Initial demo');head=git('rev-parse','HEAD')
 git('init','--bare',str(root/'remote.git'));git('remote','add','origin',str(root/'remote.git'))
 p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
 threading.Thread(target=lambda:[q.put(line) for line in p.stdout],daemon=True).start();rpc('initialize',{'protocolVersion':'2025-11-25'})
 pid=tool('ui_state')['processId'];verify_process(pid,exe)
 initial=tabs()
 for action in [0,7,8,9]*2:
  probe('tab_guard',action);check('last empty tab survives close entry '+str(action)+' attempt '+str(len(checks)),signature(tabs())==signature(initial) and tool('ui_state')['processId']==pid)
 tool('ui_command',command='new_tab');wait(lambda:len(tabs()['order'])==2);tool('ui_tabs',action='select',index=0)
 for locale in ['zh_CN','zh_TW','en_US']:
  for theme in ['Light','Dark','Blue']:
   zoom={'zh_CN':1,'zh_TW':1.25,'en_US':1.5}[locale]
   tool('ui_window',locale=locale,theme=theme,zoom=zoom)
   for index in range(5):
    for clicks in ['single','double']:
     chrome=probe('chrome_strip',index,button=clicks);check(locale+' '+theme+' tab row '+str(index)+' '+clicks+' never moves or maximizes',chrome['unchanged'] and not chrome['handled'],chrome)
   state=tabs();tab,icon=state['selectedTab'],state['closeIcon']
   check(locale+' '+theme+' close X has equal top/right spacing',abs((tab['right']-icon['right'])-(icon['y']-tab['y']))<=zoom)
   for index in range(5):
    before=tabs();probe('dialog_resize_open',index);wait(lambda:not tabs()['enabled']);opened=tabs();label=locale+' '+theme+' dialog '+str(index)
    mask=opened['modalMask'];check(label+' dims and disables the whole tab row',not opened['canManage'] and mask['visible'] and 0<=mask['bounds']['x']<=2 and opened['windowWidth']-2<=mask['bounds']['right']<=opened['windowWidth'],opened)
    def centered():
     dialogs=probe()['pageDialogs']
     if not dialogs:return False
     d,a=dialogs[0]['dialog'],dialogs[0]['area']
     return abs(d['x']+d['width']/2-a['x']-a['width']/2)<.6 and abs(d['y']+d['height']/2-a['y']-a['height']/2)<.6
    wait(centered);check(label+' first popup centers after immediate resize/move',centered())
    if index==0:
     image=Image.open(io.BytesIO(tool('ui_screenshot'))).convert('RGB');t=tabs();x=round(t['selectedTab']['x']+t['selectedTab']['width']*.7);y=round(t['modalMask']['bounds']['y']+t['modalMask']['bounds']['height'])
     pixels=[image.getpixel((x,row)) for row in range(y-2,y+3)]
     # The compositor may round an opaque preblend by one channel step.
     # A visible unmasked edge differs by tens of channel steps.
     check(label+' mask has no bright seam below selected tab',max(max(v[c] for v in pixels)-min(v[c] for v in pixels) for c in range(3))<=2,pixels)
    for action in range(10):
     probe('tab_guard',action);check(label+' blocks close/navigation entry '+str(action),signature(tabs())==signature(before) and tabs()['pages'][0]['popup']==opened['pages'][0]['popup'])
    tool('ui_tabs',action='select',index=1);tool('ui_tabs',action='close',index=0);tool('ui_tabs',action='dropdown')
    check(label+' blocks automation tab actions too',signature(tabs())==signature(before) and not tool('ui_state')['tabDropdownOpen'])
    cancel();check(label+' restores tabs on dismiss',tabs()['canManage'] and tabs()['opacity']==1)
   for mode in ['maximized','normal']:
    tool('ui_window',state=mode);probe('welcome_action',1);wait(lambda:not tabs()['enabled']);wait(centered)
    check(locale+' '+theme+' first dialog centers after '+mode,centered());cancel()
 check('process remains alive after repeated modal close attempts',tool('ui_state')['processId']==pid)
 tool('ui_tabs',action='close',index=0);wait(lambda:len(tabs()['order'])==1)
 check('closing one of several empty tabs removes just that tab',len(tabs()['order'])==1 and tabs()['pages'][0]['kind']=='Welcome')
 for action in [0,7,8,9]:
  tool('ui_open_repository',repo=str(repo));wait(lambda:tabs()['pages'][0]['kind']=='Repository');old=tabs()['active'];probe('tab_guard',action)
  wait(lambda:len(tabs()['order'])==1 and tabs()['pages'][0]['kind']=='Welcome')
  check('last repository becomes a fresh initial tab via entry '+str(action),tabs()['active']!=old and tabs()['pages'][0]['dirty']=='None' and tabs()['enabled'] and tool('ui_state')['processId']==pid)
  check('closing repository preserves HEAD and worktree '+str(action),git('rev-parse','HEAD')==head and not git('status','--porcelain'))
 tool('ui_open_repository',repo=str(repo));tool('ui_command',command='new_tab');tool('ui_open_repository',repo=str(repo))
 tool('ui_command',repo=str(repo),command='fetch_dialog');wait(lambda:not tabs()['enabled']);before=tabs()
 for action in [0,1,2,3,4,5,6,7,8,9]:probe('tab_guard',action);check('repository dialog blocks tab entry '+str(action),signature(tabs())==signature(before))
 cancel();probe('tab_guard',1);wait(lambda:len(tabs()['order'])==1);check('close others works after dialog cancellation',tabs()['enabled'])
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
 if pid:
  try:(out/'failure.png').write_bytes(tool('ui_screenshot'))
  except Exception:pass
finally:
 if pid:close_owned_gui(exe,pid)
 if p:
  p.stdin.close()
  try:p.wait(timeout=10)
  except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 if profile.exists():profile.unlink()
 remove_fixture(root,out,'TreeGit tab lifecycle ')
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'nativeInput':False},indent=2),'utf-8')
if failure:raise SystemExit(1)
print(json.dumps({'passed':len(checks),'failure':None,'nativeInput':False}))
