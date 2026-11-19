"""Routed pointer checks for tab-contained dialogs; no native desktop input."""
import base64,io,json,math,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from PIL import Image
from qa_cleanup import new_fixture,remove_fixture,verify_process

exe,out=(Path(v).resolve() for v in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit page dialog fixture ');repo=root/'Demo';repo.mkdir()
profile=exe.parent/'data/preference.json' if os.name=='nt' else root/'config/SourceGit/preference.json'
profile.parent.mkdir(parents=True,exist_ok=True);assert not profile.exists()
profile.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False}),encoding='utf-8')
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','XDG_CONFIG_HOME':str(root/'config'),'XDG_CACHE_HOME':str(root/'cache'),'GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
q=queue.Queue();sequence=0;process=None;pid=None;checks=[];failure=None
def rpc(method,args):
 global sequence
 sequence+=1;process.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':args})+'\n');process.stdin.flush();d=json.loads(q.get(timeout=40));assert d.get('id')==sequence and 'error' not in d,d;return d['result']
def tool(name,**args):
 d=rpc('tools/call',{'name':name,'arguments':args});assert not d.get('isError'),d
 return base64.b64decode(d['content'][0]['data']) if name=='ui_screenshot' else json.loads(d['content'][0]['text'])
def probe(action='inspect',value=0):return tool('ui_layout',action='test_menu',text=action,value=value)
def wait(f):
 for _ in range(150):
  d=f()
  if d:return d
  time.sleep(.08)
 raise AssertionError('Expected dialog layout did not appear')
def check(label,condition,data=None):
 assert condition,(label,data)
 checks.append(label);print('PASS',label,flush=True)
def geometry():
 values=probe()['pageDialogs'];return values[0] if values else None
def inside(g):
 d,a=g['dialog'],g['area'];return d['x']>=a['x']-.5 and d['y']>=a['y']-.5 and d['right']<=a['right']+.5 and d['y']+d['height']<=a['y']+a['height']+.5
def same(a,b):return abs(a['dialog']['x']-b['dialog']['x'])<.5 and abs(a['dialog']['y']-b['dialog']['y'])<.5
def cancel():tool('ui_layout',action='test_click',button='CancelPopup');wait(lambda:not probe()['pageDialogs'])
def luminance(color):
 rgb=[int(color[-6:][i:i+2],16)/255 for i in [0,2,4]]
 return sum(c*(v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4) for c,v in zip([.2126,.7152,.0722],rgb))
def toolbar_contrast(theme):
 buttons=probe()['welcomePresentation']
 expected='#FF0747A6' if theme=='Blue' else '#FF16836D'
 bg=luminance('#2F2F2F' if theme=='Dark' else '#F4F5F7')
 def valid(b):
  fg=luminance(b['labelColor'])
  return [c.upper() for c in b['iconColors']]==[expected] and b['contentOpacity']==1 and (max(fg,bg)+.05)/(min(fg,bg)+.05)>=4.5
 icon_centers=[b['iconCenter']['y'] for b in buttons];label_centers=[b['label']['y']+b['label']['height']/2 for b in buttons]
 return len(buttons)==5 and all(valid(b) for b in buttons) and max(icon_centers)-min(icon_centers)<1 and max(label_centers)-min(label_centers)<1
def drag_has_no_residue():
 before=geometry()['dialog'];after=probe('dialog_drag',0)['pageDialogs'][0]['dialog']
 pixels=tool('ui_screenshot');image=Image.open(io.BytesIO(pixels)).convert('RGB')
 # The former top edge is now outside the moved dialog, on empty welcome content.
 left=math.ceil(max(before['x']+16,after['right']+24));right=math.floor(before['right']-16);y=round(before['y'])
 assert right-left>100,(before,after)
 reference=[image.getpixel((x,y+5)) for x in range(left,right)]
 changed=max(sum(image.getpixel((x,row))!=reference[x-left] for x in range(left,right)) for row in range(y-1,y+2))
 return changed==0,pixels,{'left':left,'right':right,'y':y,'differentPixels':changed}
def exercise(label,inputs=True):
 before=wait(geometry);check(label+' starts inside this tab',inside(before),before)
 if before.get('form'):
  form,dialog=before['form'],before['dialog']
  check(label+' content has balanced left and right spacing',abs((form['x']-dialog['x'])-(dialog['right']-form['right']))<1,before)
 after=probe('dialog_drag',4)['pageDialogs'][0]
 check(label+' title moves the dialog without moving the app',inside(after) and not same(before,after),after)
 lost=probe('dialog_capture_lost',0)['pageDialogs'][0];check(label+' losing capture stops dragging',same(after,lost))
 body=probe('dialog_body_drag',4)['pageDialogs'][0];check(label+' dragging the body does not move the dialog',same(lost,body))
 if inputs:
  selected=probe('dialog_input_drag',4)['pageDialogs'][0];check(label+' dragging an input does not move its dialog',same(lost,selected))
 for corner in range(4):
  g=probe('dialog_drag',corner)['pageDialogs'][0];check(label+' corner '+str(corner)+' stays inside the tab',inside(g),g)

try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:subprocess.check_call(['git','-C',str(repo),*args],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 (repo/'example.txt').write_text('demo\n',encoding='utf-8');subprocess.check_call(['git','-C',str(repo),'add','.'],env=env);subprocess.check_call(['git','-C',str(repo),'commit','-m','Initial demo'],env=env,stdout=subprocess.DEVNULL)
 subprocess.check_call(['git','init','--bare',str(root/'remote.git')],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);subprocess.check_call(['git','-C',str(repo),'remote','add','origin',str(root/'remote.git')],env=env)
 (repo/'example.txt').write_text('demo changed\n',encoding='utf-8')
 with (out/'mcp.log').open('w',encoding='utf-8') as log:process=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
 threading.Thread(target=lambda:[q.put(v)for v in process.stdout],daemon=True).start();rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];verify_process(pid,exe)
 if os.environ.get('TREEGIT_LOCAL_QA_ROOT'):check('local QA windows stay hidden and do not activate',probe()['localWindowsPrivate'] is True)
 advanced=probe()['headers'][2]['items'][-1]
 check('welcome page disables repository-only advanced operations',advanced['text']=='高级操作' and not advanced['enabled'],advanced)
 for locale in ['zh_CN','zh_TW','en_US']:
  tool('ui_window',locale=locale,theme='Light',zoom=1,width=1280,height=820)
  for index,kind in [(0,'clone'),(1,'open'),(2,'create'),(3,'group'),(4,'scan')]:
   probe('welcome_action',index);wait(geometry);exercise(locale+' '+kind,index!=4);cancel()
  separator=probe()['welcomeSeparators'][0];bar,line=separator['content'],separator['separator']
  # Different control templates can round opposite edges at fractional display scaling.
  check(locale+' separator aligns with the centered toolbar content',abs((line['y']+line['height']/2)-(bar['y']+bar['height']/2))<=1,separator)
  check(locale+' separator uses a narrow recessed groove',separator['grooveWidth']==1.5 and line['width']==28)
  for theme,color in [('Light','#FF0052CC'),('Dark','#FF79B8FF'),('Blue','#FF0052CC')]:
   tool('ui_window',theme=theme)
   if locale=='zh_CN':(out/(theme+'-welcome.png')).write_bytes(tool('ui_screenshot'))
   tool('ui_command',command='create_repository');wait(geometry)
   clean,pixels,edge=drag_has_no_residue();check(locale+' '+theme+' dragging leaves no stationary top edge',clean,edge)
   frame=geometry();header,outline,background=('#FF1F1F1F','#FF606060','#FF252525') if theme=='Dark' else ('#FFEBECF0','#FFB3BAC5','#FFF4F5F7')
   check(locale+' '+theme+' popup frame matches preferences',frame['headerColor'].upper()==header and frame['frameColor'].upper()==outline and frame['frameBackground'].upper()==background and not frame['hasShadow'],frame)
   check(locale+' '+theme+' welcome icons use the header color and labels stay readable',toolbar_contrast(theme))
   (out/(locale+'-'+theme+'-dragged.png')).write_bytes(pixels)
   tool('ui_layout',action='test_set',button='CreateRepositoryGitHub',value=1)
   presentation=wait(lambda:next((p for p in probe()['createPresentation'] if p['label'] and p['label']['height']>0),None))
   b,t=presentation['button'],presentation['label']
   check(locale+' '+theme+' verify caption is centered and uses the confirm style',presentation['samePrimaryStyle'] and presentation['buttonAlignment']=='Center' and abs(b['y']+b['height']/2-t['y']-t['height']/2)<.6,presentation)
   line,label,token=presentation['linkUnderline'],presentation['linkLabel'],presentation['tokenInput']
   gap=line['y']-label['y']-label['height']
   check(locale+' '+theme+' token help has a thin continuous underline with half the gap',presentation['linkColor'].upper()==color and presentation['linkUnderlineColor'].upper()==color and abs(line['width']-label['width'])<1 and .75<=gap<=2 and 0<line['height']<1,presentation)
   check(locale+' '+theme+' token help sits directly below the token field',0<=label['y']-token['y']-token['height']<=10,presentation)
   (out/(locale+'-'+theme+'-create.png')).write_bytes(tool('ui_screenshot'))
   tool('ui_layout',action='test_click',button='ClosePopup');wait(lambda:not probe()['pageDialogs']);check(locale+' '+theme+' title close dismisses only the popup',tool('ui_state')['processId']==pid)
  tool('ui_window',theme='Light');tool('ui_open_repository',repo=str(repo));wait(lambda:any(Path(r['repo']).resolve()==repo and not r['graphLoading'] for r in tool('ui_state')['repositories']))
  check(locale+' repository exposes advanced operations',probe()['headers'][2]['items'][-1]['enabled'])
  for command in ['create_branch','create_tag','fetch_dialog','stash_dialog','apply_patch']:
   tool('ui_command',repo=str(repo),command=command);wait(geometry);exercise(locale+' '+command,command in ['create_branch','create_tag','apply_patch']);cancel()
  for theme in ['Light','Dark','Blue']:
   tool('ui_window',theme=theme)
   for command,expected in [('create_branch',['BranchAllowOverwrite']),('push_dialog',['PushSkipHooks','PushForce'])]:
    tool('ui_command',repo=str(repo),command=command);g=wait(geometry);label=locale+' '+theme+' inline '+command
    check(label+' exposes its checkboxes without an advanced dropdown',g['advancedExpanders']==0 and g['advancedCheckBoxes']==expected,g)
    check(label+' uses the shared title without a duplicate body title',bool(g['title']) and not g['legacyTitleVisible'],g)
    exercise(label,command=='create_branch')
    (out/(locale+'-'+theme+'-'+command+'-inline.png')).write_bytes(tool('ui_screenshot'))
    cancel()
  tool('ui_window',theme='Light')
  for index,kind in [(0,'cherry-pick'),(1,'revert')]:
   probe('popup_commit_action',index);g=wait(geometry)
   check(locale+' '+kind+' has a localized draggable title',bool(g['title']) and g['header'] is not None,g)
   check(locale+' '+kind+' has one title without the old body heading',not g['legacyTitleVisible'],g)
   exercise(locale+' '+kind,False);cancel()
  tool('ui_command',command='new_tab')
 # Page dialogs are modal to the tab strip; close them before navigating.
 tool('ui_command',command='create_repository');wait(geometry);probe('dialog_drag',4);old=geometry();old_index=next(t['index'] for t in tool('ui_tabs',action='list')['tabs'] if t['selected'])
 tool('ui_command',command='new_tab');check('open dialog prevents creating or switching tabs',same(old,geometry()) and next(t['index'] for t in tool('ui_tabs',action='list')['tabs'] if t['selected'])==old_index)
 cancel();tool('ui_command',command='new_tab');tool('ui_command',command='create_repository');wait(geometry)
 new=geometry();check('a new tab has its own dialog position',not same(old,new));cancel();tool('ui_tabs',action='select',index=old_index)
 check('a dismissed dialog restores the tab strip',probe()['tabState']['enabled'])
 tool('ui_command',command='create_repository');wait(geometry)
 probe('dialog_drag',1);tool('ui_window',width=1050,height=700);wait(lambda:geometry() and inside(geometry()));check('shrinking the window clamps the dragged dialog',inside(geometry()))
 cancel();tool('ui_window',width=1280,height=820)
 for zoom in [1.5,2]:
  tool('ui_window',zoom=zoom);tool('ui_command',command='create_repository');wait(geometry);exercise('zoom '+str(zoom));cancel()
 tool('ui_window',zoom=1);tool('ui_command',command='create_repository');wait(geometry);g=geometry();d,a=g['dialog'],g['area']
 check('opening a new dialog resets it to the tab center',abs(d['x']+d['width']/2-a['x']-a['width']/2)<.6 and abs(d['y']+d['height']/2-a['y']-a['height']/2)<.6,g)
 if '--operations' in sys.argv[3:]:
  cancel();tool('ui_open_repository',repo=str(repo))
  def operation():
   panels=probe()['operationPanels'];return panels[0] if panels else None
  def operation_inside(g):
   p,a=g['panel'],g['area'];return p['x']>=a['x'] and p['y']>=a['y'] and p['right']<=a['right']+.5 and p['y']+p['height']<=a['y']+a['height']+.5
  for locale in ['zh_CN','zh_TW','en_US']:
   for theme in ['Light','Dark','Blue']:
    tool('ui_window',locale=locale,theme=theme,width=1280,height=820)
    tool('ui_command',repo=str(repo),command='fetch_dialog');wait(geometry);probe('operation_probe',0)
    initial=wait(operation);check(locale+' '+theme+' progress starts below the tab toolbar',operation_inside(initial) and abs(initial['panel']['y']-initial['area']['y']-72)<1,initial)
    check(locale+' '+theme+' running can cancel and cannot close',initial['cancelEnabled'] and not initial['closeEnabled'],initial)
    bar,cancel_button=initial['barBounds'],initial['cancelBounds']
    check(locale+' '+theme+' compact cancel aligns with the determinate bar',not initial['indeterminate'] and abs(cancel_button['width']-60)<1 and abs(bar['y']+bar['height']/2-cancel_button['y']-cancel_button['height']/2)<1,initial)
    check(locale+' '+theme+' progress uses the header color',initial['progressColor'].upper()==('#FF0747A6' if theme=='Blue' else '#FF16836D'),initial)
    flowing=wait(lambda:g if (g:=operation()) and g['progress']>initial['progress'] and g['sweeping'] and (g['sweepWidth'] or 0)>0 else None)
    check(locale+' '+theme+' activity is clipped to a partially filled moving bar',0<flowing['sweepWidth']<flowing['barBounds']['width'] and flowing['progress']<100,flowing)
    probe('operation_probe',1);full=wait(lambda:g if (g:=operation()) and 'output 99' in (g['selectableText'] or '') and g['scrollGap']<=2 and g['scrollExtent']['Height']>g['scrollViewport']['Height'] else None)
    check(locale+' '+theme+' output is selectable as one full text surface',full['lines']==102 and 'output 0' in full['selectableText'],full)
    selected_output=probe('operation_probe',5)['operationPanels'][0];check(locale+' '+theme+' full output selection includes errors and multiple lines',selected_output['selectedText']==selected_output['selectableText'],selected_output)
    check(locale+' '+theme+' failed output is separately marked',full['errors']==5,full)
    check(locale+' '+theme+' long output has a scrollbar',full['scrollExtent']['Height']>full['scrollViewport']['Height'],full)
    check(locale+' '+theme+' added output keeps the top fixed',abs(initial['panel']['y']-full['panel']['y'])<.6,full)
    tool('ui_window',width=1050,height=700);small=wait(lambda:operation() if operation() and operation_inside(operation()) else None)
    check(locale+' '+theme+' resize keeps the entire panel inside its tab',operation_inside(small),small)
    probe('operation_probe',4);hidden=wait(lambda:operation() if operation() and not operation()['ShowFullOutput'] else None)
    check(locale+' '+theme+' hiding output keeps the top fixed',abs(hidden['panel']['y']-small['panel']['y'])<.6,hidden)
    probe('operation_probe',4)
    def git_progress(line):
     tool('ui_layout',action='test_menu',text='operation_probe',value=6,button=line)
     return operation()['reported']
    counting=git_progress('remote: Counting objects: 100% (50/50), done.')
    receiving=git_progress('Receiving objects:  40% (20/50)')
    ignored=git_progress('remote: issue is 100% reproducible')
    resolving=git_progress('Resolving deltas: 100% (8/8), done.')
    check(locale+' '+theme+' reported progress follows Git milestones',0<initial['reported']<counting<receiving==ignored<resolving<100)
    probe('operation_probe',2);done=wait(lambda:g if (g:=operation()) and g['closeEnabled'] and g['progress']==100 else None)
    check(locale+' '+theme+' completed close retains first-open style',done['closeWidth']==80 and done['closeFill'].upper()==('#FF0747A6' if theme=='Blue' else '#FF16836D') and not done['sweeping'],done)
    check(locale+' '+theme+' completion disables cancel and enables close',not done['cancelEnabled'] and done['closeEnabled'] and not done['InProgress'] and done['progress']==100,done)
    (out/(locale+'-'+theme+'-operation.png')).write_bytes(tool('ui_screenshot'))
    tool('ui_layout',action='test_click',button='CloseOperation');wait(lambda:not operation() and not geometry());check(locale+' '+theme+' close dismisses the result and original form',not operation() and not geometry())
    tool('ui_command',repo=str(repo),command='fetch_dialog');original=wait(geometry);probe('operation_probe',0);wait(operation);probe('operation_probe',3);restored=wait(geometry)
    check(locale+' '+theme+' cancel returns to the original form',restored['title']==original['title'] and not operation());cancel()
    if locale=='en_US' and theme=='Blue':
     tool('ui_command',repo=str(repo),command='fetch_dialog');wait(geometry);probe('operation_probe',0);wait(operation)
     partial=git_progress('Receiving objects: 50% (1/2)')
     probe('operation_probe',7);failed=wait(lambda:operation() if operation() and operation()['closeEnabled'] else None)
     check('failed transfer freezes its estimate below full',partial==failed['reported'] and 0<=failed['progress']==operation()['progress']<100 and not failed['sweeping'] and not failed['cancelEnabled'],failed)
     tool('ui_layout',action='test_click',button='CloseOperation');wait(lambda:not operation() and not geometry())
    tool('ui_window',width=1280,height=820)
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
 if pid:
  try:(out/'failure.png').write_bytes(tool('ui_screenshot'));(out/'failure-layout.json').write_text(json.dumps(probe(),ensure_ascii=False,indent=2),encoding='utf-8')
  except Exception:pass
finally:
 if pid:
  if os.name=='nt':
   command=f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{if($p.Path -ne '{str(exe).replace(chr(39),chr(39)*2)}'){{throw 'Unexpected test process'}};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(5000)){{$p.Kill();$p.WaitForExit()}}}}"
   subprocess.run(['powershell','-NoProfile','-EncodedCommand',base64.b64encode(command.encode('utf-16-le')).decode()],check=True,capture_output=True)
  else:os.kill(pid,15);time.sleep(.3)
 if process:
  process.stdin.close()
  try:process.wait(timeout=5)
  except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
 if profile.exists():profile.unlink()
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8');remove_fixture(root,out,'TreeGit page dialog fixture ')
if failure:raise SystemExit(1)
print(json.dumps({'passed':len(checks),'failure':None,'nativeInput':False}))
