"""Use real merge conflicts, the shipped editor, native input, and native Git verification."""
import base64
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time

assert os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_OS')=='Windows', 'Native input is restricted to an owned CI desktop'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit merge QA ')).resolve();repo=root/'repo';repo.mkdir()
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
checks=[];failure=None;pid=None;opened_text=None
def git(*args,ok=True):
    result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    if ok:assert result.returncode==0,result.stderr
    return result
git('init','-b','main');git('config','user.name','Merge QA');git('config','user.email','qa@example.invalid');git('config','core.autocrlf','false')
(repo/'.gitattributes').write_text('*.c text eol=crlf\n',encoding='utf-8')
file='合并 [1].c';path=repo/file
base='// 合并测试\nint first = 0;\n' + ''.join('int common_'+str(i)+' = 0;\n' for i in range(9)) + 'int last = 0;\n'
def write(text):path.write_bytes(b'\xef\xbb\xbf'+text.replace('\r\n','\n').replace('\n','\r\n').encode('utf-8'))
write(base);git('add','.');git('commit','-m','Initial')
git('checkout','-b','feature');write(base.replace('first = 0','first = 2').replace('last = 0','last = 2'));git('add','.');git('commit','-m','Remote values')
git('checkout','main');write(base.replace('first = 0','first = 1').replace('last = 0','last = 1'));git('add','.');git('commit','-m','Local values')
head=git('rev-parse','HEAD').stdout.strip()
def conflict():
    if (repo/'.git/MERGE_HEAD').exists():git('merge','--abort')
    git('reset','--hard','main')
    result=git('merge','--no-commit','feature',ok=False);assert result.returncode==1
    assert len(git('ls-files','-u','--',file).stdout.splitlines())==3
conflict()
(exe.parent/'data').mkdir(exist_ok=True)
prefs=exe.parent/'data/preference.json'
prefs.write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    response=json.loads(messages.get(timeout=60));assert 'error' not in response,response;return response['result']
def tool(name,args=None):
    response=rpc('tools/call',{'name':name,'arguments':args or {}});assert not response.get('isError'),response
    return base64.b64decode(response['content'][0]['data']) if name=='ui_screenshot' else json.loads(response['content'][0]['text'])
def check(label,condition):
    assert condition,label
    checks.append(label);print('PASS '+label,flush=True)
def wait_for(fn):
    for _ in range(150):
        value=fn()
        if value:return value
        time.sleep(.1)
    raise AssertionError('Timed out')
def dialog():return next((d for d in tool('ui_state')['dialogs'] if d['type']=='MergeConflictEditor'),None)
def action(name,**values):return tool('ui_merge_result',{'repo':str(repo),'file':file,'action':name,**values})
def open_editor(use_button=False):
    global opened_text
    if use_button:
        select_conflicted_file()
        wait_for(lambda:any(b['text']=='用合并工具编辑' and b['enabled'] for b in layout()['buttons']))
        press_text('用合并工具编辑')
    else:
        tool('ui_refresh',{'repo':str(repo)});time.sleep(.3)
        tool('ui_command',{'repo':str(repo),'command':'merge_conflicts','file':file})
    wait_for(lambda: dialog() and dialog()['merge']['editable'])
    opened_text=action('read')['text']
    return opened_text
def clean_close():
    while any(d['type']=='Confirm' for d in tool('ui_state')['dialogs']):
        tool('ui_command',{'command':'close_dialog'})
    if dialog():
        action('replace',text=opened_text)
        tool('ui_command',{'command':'close_dialog'})
        wait_for(lambda: dialog() is None)
def shot(name):(out/(name+'.png')).write_bytes(tool('ui_screenshot',{'window':'active'}))

api=ctypes.WinDLL('user32',use_last_error=True)
api.WindowFromPoint.argtypes=[wintypes.POINT];api.WindowFromPoint.restype=wintypes.HWND
api.GetForegroundWindow.restype=wintypes.HWND
api.GetWindowThreadProcessId.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]
api.SetForegroundWindow.argtypes=[wintypes.HWND]
def owner(hwnd):
    result=wintypes.DWORD();api.GetWindowThreadProcessId(hwnd,ctypes.byref(result));return result.value
def key(vk,ctrl=False):
    assert owner(api.GetForegroundWindow())==pid,'Refusing keyboard input outside the owned CI GUI'
    if ctrl:api.keybd_event(0x11,0,0,0)
    api.keybd_event(vk,0,0,0);api.keybd_event(vk,0,2,0)
    if ctrl:api.keybd_event(0x11,0,2,0)
def click_result():
    target=dialog()['merge']['resultTarget'];point=wintypes.POINT(target['x'],target['y'])
    hwnd=api.WindowFromPoint(point);assert owner(hwnd)==pid,'Refusing pointer input outside the owned CI GUI'
    api.SetForegroundWindow(hwnd);assert api.SetCursorPos(point.x,point.y)
    api.mouse_event(2,0,0,0,0);api.mouse_event(4,0,0,0,0);time.sleep(.2)

def pointer(x,y,click=False,right=False):
    point=wintypes.POINT(round(x),round(y));hwnd=api.WindowFromPoint(point)
    assert owner(hwnd)==pid,'Refusing pointer input outside the owned CI GUI'
    if owner(api.GetForegroundWindow())!=pid:api.SetForegroundWindow(hwnd)
    assert api.SetCursorPos(point.x,point.y)
    if click:api.mouse_event(8 if right else 2,0,0,0,0);api.mouse_event(16 if right else 4,0,0,0,0)
    time.sleep(.25)
def layout():return tool('ui_layout',{'window':'active'})
def button(name):return next(b for b in layout()['buttons'] if b['name']==name)
def screen(bounds,xf=.5,yf=.5):
    window=next((d for d in reversed(tool('ui_state')['dialogs']) if d['type'] in ('MergeConflictEditor','Confirm')),None)
    if window is None:
        hwnd=api.GetForegroundWindow();assert owner(hwnd)==pid
        origin=wintypes.POINT(0,0);assert api.ClientToScreen(hwnd,ctypes.byref(origin))
        window={'x':origin.x,'y':origin.y,'scaling':layout()['window']['scaling']}
    return window['x']+(bounds['x']+bounds['width']*xf)*window['scaling'],window['y']+(bounds['y']+bounds['height']*yf)*window['scaling']
def press(name):pointer(*screen(button(name)['bounds']),click=True)
def source_block(index,click=False,side='left',right=False):
    target=dialog()['merge']['regions'][index]['sourceTarget']
    assert target is not None,'Block is outside the current viewport'
    if side in ('base','right'):
        target=dialog()['merge']['regions'][index]['baseTarget' if side=='base' else 'remoteTarget']
        assert target is not None

    pointer(target['x'],target['y'],click or right,right)

def press_text(text):
    item=next(b for b in layout()['buttons'] if b['text']==text)
    pointer(*screen(item['bounds']),click=True)
def menu_items():
    return (dialog()['merge']['contextMenu'] or {}).get('items',[]) if dialog() else []
def menu_labels():
    return [i['text'] for i in wait_for(menu_items)]
def choose(name):
    item=next(i for i in wait_for(menu_items) if i['name']==name)
    assert item['enabled'];pointer(item['x'],item['y'],click=True)
    wait_for(lambda:not menu_items())
api.GetDC.argtypes=[wintypes.HWND];api.GetDC.restype=wintypes.HDC
api.ReleaseDC.argtypes=[wintypes.HWND,wintypes.HDC]
gdi=ctypes.WinDLL('gdi32',use_last_error=True)
gdi.GetPixel.argtypes=[wintypes.HDC,ctypes.c_int,ctypes.c_int];gdi.GetPixel.restype=wintypes.DWORD
def conflict_color(index,column):
    window=dialog();region=window['merge']['regions'][index]
    target=region[['sourceTarget','baseTarget','remoteTarget','resultTarget'][column]]
    assert target is not None
    scroller=[s for s in layout()['scrolls'] if s['visible']][column]
    x=round(window['x']+(scroller['bounds']['x']+4)*window['scaling']);y=target['y']
    assert owner(api.WindowFromPoint(wintypes.POINT(x,y)))==pid,'Read pixels only inside the owned QA window'
    dc=api.GetDC(None)
    try:pixel=gdi.GetPixel(dc,x,y)
    finally:api.ReleaseDC(None,dc)
    assert pixel!=0xffffffff
    return pixel&255,(pixel>>8)&255,(pixel>>16)&255

def select_conflicted_file():
    tool('ui_refresh',{'repo':str(repo)});time.sleep(.3)
    tool('ui_diff',{'repo':str(repo),'action':'select','file':file,'source':'unstaged'})
    wait_for(lambda:any(b['name']=='UseLocalConflict' and b['enabled'] for b in layout()['buttons']))
    wait_for(lambda:any(b['text']=='用合并工具编辑' and b['enabled'] for b in layout()['buttons']))

try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)})
    pid=tool('ui_state')['processId'];tool('ui_window',{'state':'maximized','theme':'Blue'})
    wait_for(lambda: any(Path(r['repo']).resolve()==repo and not r['graphLoading'] for r in tool('ui_state')['repositories']))
    for name,value in [('UseLocalConflict',1),('UseRemoteConflict',2)]:
        select_conflicted_file()
        local=button('UseLocalConflict');remote=button('UseRemoteConflict')
        check('conflict overview places Local before Remote '+name,local['text']=='使用 Local' and remote['text']=='使用 Remote' and local['bounds']['x']<remote['bounds']['x'])
        check('overview uses the requested merge-tool label '+name,any(b['text']=='用合并工具编辑' for b in layout()['buttons']))
        if name=='UseLocalConflict':shot('conflict-overview')
        press(name);wait_for(lambda:not git('ls-files','-u','--',file).stdout)
        check('overview choice selects the correct complete version '+name,path.read_bytes()==b'\xef\xbb\xbf'+base.replace('first = 0','first = '+str(value)).replace('last = 0','last = '+str(value)).replace('\n','\r\n').encode('utf-8'))
        time.sleep(.3);conflict()
    select_conflicted_file()
    original=path.read_bytes();text=open_editor(use_button=True)
    check('overview merge-tool button opens the editable conflict window',dialog() is not None)
    press('CancelMergeEditor');wait_for(lambda:dialog() is None)
    check('the actual Cancel button closes an unchanged editor directly',path.read_bytes()==original)
    text=open_editor(use_button=True)
    check('actual merge markers load without BOM characters or synthetic padding',text==original.decode('utf-8-sig') and action('read')['unsolved']==2)
    check('the displayed result is enabled and editable',dialog()['merge']['editable'])
    check('Base shows the actual common ancestor instead of either side','int first = 0;' in dialog()['merge']['baseSample'] and 'int first = 1;' not in dialog()['merge']['baseSample'] and not dialog()['merge']['baseHint'])
    check('there are three comparison columns and one editable result',len([s for s in layout()['scrolls'] if s['visible']])==4)
    original_window=dialog()
    for zoom in (.75,1.5,2,2.5):
        tool('ui_window',{'zoom':zoom})
        tool('ui_layout',{'window':'active','action':'resize','width':800,'height':520});time.sleep(.3)
        geometry=layout();window=geometry['window']
        footers=[b for b in geometry['buttons'] if b['name'] in ('SaveMergeResult','CancelMergeEditor')]
        check('merge footer stays visible while resizing at zoom '+str(zoom),len(footers)==2 and all(b['bounds']['x']>=-1.5 and b['bounds']['y']>=-1.5 and b['bounds']['x']+b['bounds']['width']<=window['width']+1.5 and b['bounds']['y']+b['bounds']['height']<=window['height']+1.5 for b in footers))
        panes=[s for s in geometry['scrolls'] if s['visible']]
        check('all four comparison panes remain reachable at zoom '+str(zoom),len(panes)==4 and all(s['bounds']['width']>=20 and s['bounds']['height']>=20 and s['bounds']['x']>=-1.5 and s['bounds']['y']>=-1.5 and s['bounds']['x']+s['bounds']['width']<=window['width']+1.5 and s['bounds']['y']+s['bounds']['height']<=window['height']+1.5 for s in panes))
        if zoom==2.5:shot('merge-editor-narrow-zoom-2.5')
    tool('ui_window',{'zoom':1})
    tool('ui_layout',{'window':'active','action':'resize','width':original_window['width'],'height':original_window['height']});time.sleep(.3)
    divider=next(s for s in layout()['splitters'] if s['visible'])
    for delta in (-10000,10000):
        tool('ui_layout',{'window':'active','action':'drag','splitter':divider['index'],'dy':delta});time.sleep(.15)
        check('both editing panes stay usable at splitter extreme '+str(delta),all(s['bounds']['height']>=60 for s in layout()['scrolls'] if s['visible']))
    tool('ui_layout',{'window':'active','action':'restore','splitter':divider['index'],'rows':['*','4','*']});time.sleep(.15)
    check('initial conflict position is one of a fixed total',dialog()['merge']['position']=='1/2')
    controls=layout();nav=button('UndoMergeResult')['bounds']
    scrolls=[s for s in controls['scrolls'] if s['visible']]
    check('navigation starts with Undo and aligns with the Base column',abs(nav['x']-scrolls[1]['bounds']['x'])<=2)
    check('there are no bottom undo or redo buttons',not any(b['text'] in ('撤销','重做') for b in controls['buttons']))
    source_block(0)
    check('hover does not display actions over code',dialog()['merge']['contextMenu'] is None and not any(b['name'] in ('AcceptLocalConflict','AcceptRemoteConflict','RestoreLocalConflict','RestoreRemoteConflict') for b in layout()['buttons']))
    source_block(0,right=True)
    check('Local has all four consistent choices',menu_labels()[:4]==['<-仅左边','仅右边->','先左后右','先右后左'])
    shot('left-context-menu')
    choose('RightThenLeftConflict')
    check('right-then-left menu action edits just the selected block','int first = 2;\r\nint first = 1;' in action('read')['text'] and action('read')['unsolved']==1)
    for column in range(4):
        red,green,blue=conflict_color(0,column)
        check('the selected resolved block immediately turns green in column '+str(column),green>red+8)
    press('UndoMergeResult')
    for column in range(4):
        red,green,blue=conflict_color(0,column)
        check('undo immediately restores red in column '+str(column),red>green+8)
    source_block(0,side='base',right=True)
    check('Base has the same four choices',menu_labels()[:4]==['<-仅左边','仅右边->','先左后右','先右后左'])
    choose('LeftThenRightConflict')
    check('the Base menu still uses Local and Remote in the requested order','int first = 1;\r\nint first = 2;' in action('read')['text'])
    press('UndoMergeResult')
    source_block(0,side='right',right=True)
    check('Remote has all four consistent choices',menu_labels()[:4]==['<-仅左边','仅右边->','先左后右','先右后左'])
    shot('right-context-menu');choose('UseRightConflict')
    check('right menu uses the remote contents','int first = 2;' in action('read')['text'] and 'int first = 1;' not in action('read')['text'])
    press('UndoMergeResult')
    source_block(0,right=True);choose('UseLeftConflict')
    check('left menu uses local contents','int first = 1;' in action('read')['text'] and 'int first = 2;' not in action('read')['text'])
    check('resolving a block changes remaining count but not the total',dialog()['merge']['unsolved']==1 and dialog()['merge']['position']=='1/2')
    source_block(0)
    check('resolved blocks also show no hover overlay',dialog()['merge']['contextMenu'] is None)
    source_block(0,right=True)
    check('a resolved block retains the four choices and Restore',menu_labels()[:4]==['<-仅左边','仅右边->','先左后右','先右后左'] and '复原' in menu_labels())
    choose('RestoreConflict')
    check('Restore brings back exactly the original block',action('read')['text']==text and dialog()['merge']['unsolved']==2)
    press('UndoMergeResult')
    check('toolbar undo reverses Restore',dialog()['merge']['unsolved']==1)
    press('NextConflict');check('Next selects the second original block',dialog()['merge']['position']=='2/2')
    press('PreviousConflict');check('Previous includes the already resolved block',dialog()['merge']['position']=='1/2')
    source_scroll=next(s for s in layout()['scrolls'] if s['visible'])
    tool('ui_layout',{'window':'active','action':'scroll','scroller':source_scroll['index'],'dy':10000});time.sleep(.2)
    source_block(1,click=True)
    check('clicking reference data selects that block',dialog()['merge']['position']=='2/2')
    current=action('read')['text'];action('replace',offset=0,length=0,text='// manual common edit\r\n')
    action('resolve',region=1,choice='theirs')
    resolved=action('read')['text']
    expected='// manual common edit\r\n'+base.replace('first = 0','first = 1').replace('last = 0','last = 2').replace('\n','\r\n')
    check('per-block choices preserve intervening manual edits',resolved==expected and action('read')['unsolved']==0)
    click_result();key(0x23,ctrl=True);key(0x58);time.sleep(.25)
    check('real keyboard input edits the result',action('read')['text']==expected+'x')
    key(0x5A,ctrl=True);time.sleep(.2);check('real keyboard Undo restores the result',action('read')['text']==expected)
    key(0x59,ctrl=True);time.sleep(.2);check('real keyboard Redo restores the edit',action('read')['text']==expected+'x')
    region=dialog()['merge']['regions'][1]['resultTarget']
    if region:
        pointer(region['x'],region['y'],click=True,right=True)
        check('the editable result also uses the same four choices',menu_labels()[:4]==['<-仅左边','仅右边->','先左后右','先右后左'])
        key(0x1b);wait_for(lambda:not menu_items())
    shot('manual-mixed-result')
    check('all-resolved navigation remains enabled and preserves the total',button('NextConflict')['enabled'] and dialog()['merge']['totalConflicts']==2)
    tool('ui_command',{'command':'close_dialog'})
    wait_for(lambda:any(d['type']=='Confirm' for d in tool('ui_state')['dialogs']))
    check('unsaved close presents OK and Cancel',''.join(button('BtnYes')['text'].split())=='确定' and ''.join(button('BtnNo')['text'].split())=='取消')
    shot('unsaved-close')
    press('BtnNo');wait_for(lambda:not any(d['type']=='Confirm' for d in tool('ui_state')['dialogs']))
    check('Cancel keeps unsaved text and the editor open',dialog() is not None and action('read')['text']==expected+'x')
    press('SaveMergeResult');wait_for(lambda: dialog() is None)
    check('the actual Save and Stage button closes the editor after success',True)
    wanted=b'\xef\xbb\xbf'+(expected+'x').encode('utf-8')
    check('save preserves UTF-8 BOM, CRLF and the missing final newline',path.read_bytes()==wanted)
    check('save stages the actual merged bytes and clears unmerged entries',not git('ls-files','-u','--',file).stdout and git('show',':'+file).stdout==wanted.decode('utf-8').replace('\r\n','\n'))
    check('saving never commits or ends the pending merge',git('rev-parse','HEAD').stdout.strip()==head and (repo/'.git/MERGE_HEAD').exists())

    conflict();original=path.read_bytes();open_editor();action('replace',text='cancelled edit\n')
    press('CancelMergeEditor');wait_for(lambda:any(d['type']=='Confirm' for d in tool('ui_state')['dialogs']))
    press('BtnYes');wait_for(lambda:dialog() is None)
    check('OK discards only unsaved changes without writing or staging',path.read_bytes()==original and len(git('ls-files','-u','--',file).stdout.splitlines())==3)
    check('closing an unchanged restored buffer does not write or stage',path.read_bytes()==original and len(git('ls-files','-u','--',file).stdout.splitlines())==3)
    open_editor();action('replace',text='my edited result\n');external=b'external modification\n';path.write_bytes(external)
    press('SaveMergeResult');wait_for(lambda:dialog() and dialog()['merge']['error'])
    check('a failed save keeps the editor open and rejects an external file change',dialog() is not None)
    check('external text and the edited in-memory result both survive',path.read_bytes()==external and action('read')['text']=='my edited result\n' and dialog()['merge']['error'])
    shot('external-edit-protected');clean_close()
    check('no temporary merge file is left behind',not list(repo.glob('.*.treegit-merge-*')))
except BaseException as error:
    failure=repr(error)
    try:(out/'failure-state.json').write_text(json.dumps({'state':tool('ui_state'),'layout':layout()},ensure_ascii=False,indent=2),encoding='utf-8')
    except BaseException:pass
    try:shot('failure')
    except BaseException:pass
    raise
finally:
    try:clean_close()
    except BaseException:pass
    (out/'merge-editor-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close();p.wait(timeout=15)
    if pid:
        code="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell','-NoProfile','-Command',code],capture_output=True,check=True)
    assert root.name.startswith('TreeGit merge QA ') and root.parent==Path(tempfile.gettempdir()).resolve()
    for item in (root,*root.rglob('*')):assert not item.is_symlink() and not item.is_junction()
    def readonly(function,path,error):
        owned=Path(path);assert owned.resolve().is_relative_to(root)
        owned.chmod(owned.stat().st_mode|stat.S_IWRITE);function(path)
    shutil.rmtree(root,onerror=readonly)
    prefs.write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
