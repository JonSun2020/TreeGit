"""Stash sidebar navigation must retain the main sidebar and all original operations."""
import base64
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
repo = Path(tempfile.mkdtemp(prefix='TreeGit stash sidebar '))
checks = []

def git(*args):
    r = subprocess.run([shutil.which('git'), *args], cwd=repo, capture_output=True, text=True, encoding='utf-8')
    assert not r.returncode, r.stderr
    return r.stdout.strip()

git('init','-b','main'); git('config','user.name','TreeGit QA'); git('config','user.email','qa@example.invalid')
(repo/'firmware.bin').write_bytes(bytes(range(256))*8); (repo/'sensor.txt').write_text('initial\n')
git('add','.'); git('commit','-m','Initial')
for i in range(3):
    (repo/'sensor.txt').write_text(f'stash {i}\n')
    data=bytearray((repo/'firmware.bin').read_bytes()); data[13+i] ^= 0x40; (repo/'firmware.bin').write_bytes(data)
    (repo/f'untracked-{i}.txt').write_text('untracked\n')
    git('stash','push','-u','-m',f'calibration {i}')
before=git('stash','list'); status=git('status','--porcelain')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
seq=0
def tool(name,args={}):
    global seq
    seq+=1; method='initialize' if name=='initialize' else 'tools/call'
    params={} if method=='initialize' else {'name':name,'arguments':args}
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    r=json.loads(p.stdout.readline()); assert 'error' not in r,r
    r=r['result']; assert not r.get('isError'),r
    if name=='initialize': return r
    return base64.b64decode(r['content'][0]['data']) if name=='ui_screenshot' else json.loads(r['content'][0]['text'])
def state(): return next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
def stash(action='list',**kwargs): return tool('ui_stashes',{'repo':str(repo),'action':action,**kwargs})
def check(label,ok):
    assert ok,label
    checks.append(label);print('PASS',label,flush=True)
def wait(predicate):
    for _ in range(120):
        s=state()
        if predicate(s): return s
        time.sleep(.1)
    raise AssertionError(s)

tool('initialize');tool('ui_open_repository',{'repo':str(repo)})
tool('ui_window',{'width':1440,'height':900,'theme':'SourceTree'})
wait(lambda s:s['stashes']['count']==3 and not s['graphLoading'])
width=state()['sidebarWidth']
stash('collapse');check('collapsing stash header does not leave history',state()['view']==0 and not state()['stashes']['expanded'])
stash('expand');check('expanding stash header keeps history and sidebar width',state()['view']==0 and state()['sidebarWidth']==width)
for name,index in [('stash@{0}',2),('stash@{1}',1),('stash@{2}',0)]:
    selected=stash('select',stash=name)['selected']
    s=wait(lambda s:s['stashes']['selected']==selected and f'untracked-{index}.txt' in (s['stashes']['changes'] or []))
    check('select '+name+' keeps the branch/remote sidebar and shows correct files',s['view']==2 and s['sidebarVisible'] and s['sidebarWidth']==width and 'firmware.bin' in s['stashes']['changes'])
    tool('ui_diff',{'repo':str(repo),'action':'select','source':'stash','file':'firmware.bin'})
    for _ in range(100):
        d=tool('ui_diff',{'repo':str(repo)})
        if d['status']=='ready' and d['selectedOffset']==13+index: break
        time.sleep(.1)
    check(name+' binary changes compare against its actual base',d['status']=='ready' and d['differentBytes']==1 and d['selectedOffset']==13+index)
    tool('ui_select_view',{'repo':str(repo),'view':'history'})
    check('history exits '+name+' without shrinking the sidebar',state()['view']==0 and state()['sidebarWidth']==width and state()['stashes']['selected'] is None)
stash('select',stash='stash@{0}');wait(lambda s:bool(s['stashes']['changes']))
(out/'stashes-sourceTree.png').write_bytes(tool('ui_screenshot'))
for command,kind in [('stash_apply_dialog','ApplyStash'),('stash_drop_dialog','DropStash'),('stash_branch_dialog','CheckoutBranchFromStash')]:
    tool('ui_command',{'repo':str(repo),'command':command})
    check(command+' opens its original confirmation',state()['pendingDialog']==kind)
    tool('ui_command',{'repo':str(repo),'command':'cancel_popup'})
filtered=stash(filter='calibration 1');check('stash filtering affects sidebar entries',len(filtered['stashes'])==1)
stash(filter='');check('clearing stash filter restores all entries',len(stash()['stashes'])==3)
tool('ui_window',{'width':1024,'height':720,'zoom':1.5});time.sleep(.5)
(out/'stashes-narrow-150.png').write_bytes(tool('ui_screenshot'))
check('scaled narrow stash view retains its sidebar',state()['sidebarVisible'])
tool('ui_window',{'width':1440,'height':900,'zoom':1,'theme':'Dark'});time.sleep(.3)
(out/'stashes-dark.png').write_bytes(tool('ui_screenshot'))
check('viewing, filtering and cancelling never apply or delete stashes',git('stash','list')==before and git('status','--porcelain')==status)
tool('ui_select_view',{'repo':str(repo),'view':'history'})
tool('ui_window',{'theme':'SourceTree'})
tool('ui_command',{'command':'close_tab'})
p.stdin.close();p.wait(timeout=10)
(out/'stashes-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo)},ensure_ascii=False,indent=2),encoding='utf-8')
