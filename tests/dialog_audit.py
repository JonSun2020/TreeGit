"""Inspect real dialog geometry and screenshots, including small-window and high-zoom cases."""
import base64
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

sys.stdout.reconfigure(encoding='utf-8')

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit dialog audit ')).resolve()
repo = root/'repo'
remote = root/'remote.git'
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1')
def git(*args):
    p = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, text=True, encoding='utf-8',env=env)
    assert p.returncode == 0, p.stderr
    return p.stdout.strip()
repo.mkdir()
git('init','-b','main')
git('config','user.name','Dialog QA')
git('config','user.email','dialog@example.invalid')
(repo/'file.txt').write_text('initial\n',encoding='utf-8')
git('add','.')
git('commit','-m','Initial tagged commit')
git('tag','v1.0')
git('init','--bare',str(remote))
git('remote','add','origin',str(remote))
git('push','-u','origin','main')
(repo/'file.txt').write_text('changed\n',encoding='utf-8')
git('stash','push','-m','Audit stash')
(repo/'lost.txt').write_text('Recoverable commit\n',encoding='utf-8')
git('add','--','lost.txt');git('commit','-m','Lost commit for recovery audit')
lost=git('rev-parse','HEAD');git('reset','--hard','HEAD~1')
(repo/'file.txt').write_text('unstaged\n',encoding='utf-8')
p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
    text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue()
threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start()
seq=0
pid=None
records=[]
failures=[]
def rpc(method, params=None):
    global seq
    seq+=1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n')
    p.stdin.flush()
    result=json.loads(messages.get(timeout=45))
    assert 'error' not in result,result
    return result['result']
def tool(name,args=None):
    result=rpc('tools/call',{'name':name,'arguments':args or {}})
    assert not result.get('isError'),result
    if name=='ui_screenshot':return base64.b64decode(result['content'][0]['data'])
    return json.loads(result['content'][0]['text'])
def capture(name,active=False):
    time.sleep(.25)
    (out/(name+'.png')).write_bytes(tool('ui_screenshot',{'window':'active' if active else 'main'}))
    state=tool('ui_state')
    records.append({'name':name,'state':state})
    return state
def check(name,ok):
    print(('PASS ' if ok else 'FAIL ')+name,flush=True)
    if not ok:failures.append(name)
def within(d):
    a=d['workArea']
    return d['x']>=a['x']-1 and d['y']>=a['y']-1 and d['x']+d['width']*d['scaling']<=a['x']+a['width']+1 and d['y']+d['height']*d['scaling']<=a['y']+a['height']+1
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    tool('ui_open_repository',{'repo':str(repo)})
    pid=tool('ui_state')['processId']
    for _ in range(100):
        if any(r['graphCommits'] for r in tool('ui_state')['repositories']):break
        time.sleep(.1)
    tool('ui_window',{'width':1280,'height':800,'state':'normal','theme':'Blue','zoom':1})
    def has_lost():
        return any(c['sha']==lost for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo for c in r['graphCommits'])
    check('normal history excludes a commit lost after reset',not has_lost())
    for attempt in range(2):
        tool('ui_command',{'repo':str(repo),'command':'show_reflog'})
        deadline=time.monotonic()+15
        while time.monotonic()<deadline and not has_lost():time.sleep(.1)
        check('reflog recovery entry reveals the lost commit, call '+str(attempt+1),has_lost())
        capture('reflog-recovery-'+str(attempt+1))
    tool('git_run',{'repo':str(repo),'arguments':['branch','recovered',lost]})
    check('a recovered branch keeps the lost commit reachable',git('rev-parse','recovered')==lost)
    for theme in ['Blue','Light','Dark','Default','Blue']:
        tool('ui_window',{'theme':theme})
        state=capture('tag-theme-'+theme.lower())
        colors=state['layoutProbe']['taggedRefs']
        check('live tag follows graph lane for '+theme,bool(colors) and all(c['color'] is not None and c['color']==c['graphColor'] for c in colors))
    for zoom in ([float(os.environ['TREEGIT_AUDIT_ZOOM'])] if 'TREEGIT_AUDIT_ZOOM' in os.environ else [1,1.5,2]):
        print('Opening small window at zoom '+str(zoom),flush=True)
        tool('ui_window',{'width':1024,'height':720,'zoom':zoom})
        for page in ['general','appearance','git','signing','integration']:
            tool('ui_command',{'command':'preferences','page':page})
            state=capture(f'preferences-{page}-{zoom}',True)
            dialogs=state['dialogs']
            check(f'preferences {page} {zoom} remains within screen',len(dialogs)==1 and within(dialogs[0]))
            probe=state['layoutProbe']['dialogs'][0]
            check(f'preferences {page} {zoom} has five useful tabs',len(probe['tabs'])==1 and len(probe['tabs'][0]['headers'])==5)
        tool('ui_command',{'command':'close_dialog'})
        tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        for page in ['remotes','advanced']:
            tool('ui_command',{'command':'repository_settings_'+page})
            state=capture(f'repository-{page}-{zoom}',True)
            dialog=state['dialogs'][0]
            check(f'repository {page} {zoom} has visible confirmation within screen',within(dialog) and dialog['confirmVisible'])
        tool('ui_command',{'command':'close_dialog'})
        for command in ['credentials','about','mcp_connection','ssh_keys']:
            tool('ui_command',{'repo':str(repo),'command':command})
            state=capture(f'dialog-{command}-{zoom}',True)
            check(f'{command} {zoom} fits the screen',len(state['dialogs'])==1 and within(state['dialogs'][0]))
            layout=tool('ui_layout',{'window':'active'})
            w=layout['window']
            fixed=[b for b in layout['buttons'] if not b['inScrollArea']]
            check(f'{command} {zoom} fixed actions remain visible',all(b['bounds']['x']>=-.5 and b['bounds']['y']>=-.5 and b['bounds']['x']+b['bounds']['width']<=w['width']+.5 and b['bounds']['y']+b['bounds']['height']<=w['height']+.5 for b in fixed))
            if command=='ssh_keys':
                tool('ui_layout',{'window':'active','action':'test_click','button':'OpenKeyGenerator'})
                generated=capture(f'dialog-ssh-generator-{zoom}',True)
                check(f'SSH generator {zoom} window stays on screen',within(generated['dialogs'][0]))
                g=tool('ui_layout',{'window':'active'})
                check(f'SSH generator {zoom} retains a scrollable body',any(s['name']=='KeyViewport' and s['visible'] and s['horizontal']=='Auto' and s['vertical']=='Auto' for s in g['scrolls']))
            tool('ui_command',{'command':'close_dialog'})
        for command in ['create_branch','create_tag','reset_dialog','fetch_dialog','pull_dialog','push_dialog','stash_dialog','apply_patch']:
            tool('ui_command',{'repo':str(repo),'command':command})
            state=capture(f'popup-{command}-{zoom}')
            boxes=state['layoutProbe']['popups']
            check(f'{command} {zoom} is fully inside the window',bool(boxes) and all(b['withinWindow'] for b in boxes))
            tool('ui_command',{'repo':str(repo),'command':'cancel_popup'})
    (out/'dialog-audit-results.json').write_text(json.dumps({'failures':failures,'records':records,'exe':str(exe),'repo':str(repo)},ensure_ascii=False,indent=2),encoding='utf-8')
    assert not failures,failures
finally:
    (out/'dialog-audit-results.json').write_text(json.dumps({'failures':failures,'records':records,'exe':str(exe),'repo':str(repo)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:
        code="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell','-NoProfile','-Command',code],capture_output=True,check=True)
    assert root.name.startswith('TreeGit dialog audit ') and repo.parent==root and not root.is_symlink() and not root.is_junction()
    def readonly(fn,path,_):
        owned=Path(path)
        assert owned.resolve().is_relative_to(root) and not owned.is_symlink() and not owned.is_junction()
        owned.chmod(owned.stat().st_mode | stat.S_IWRITE)
        fn(path)
    shutil.rmtree(root,onerror=readonly)
