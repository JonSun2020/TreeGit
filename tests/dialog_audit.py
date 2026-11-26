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
from qa_process import close_owned_gui

sys.stdout.reconfigure(encoding='utf-8')

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit dialog audit ')).resolve()
repo = root/'repo'
remote = root/'remote.git'
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1', TREEGIT_TEST_MENU_PROBE='1')
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

def audit_operation(locale, view, command, **args):
    if command in ('stash_apply_dialog','stash_drop_dialog','stash_branch_dialog'):
        tool('ui_stashes', {'repo':str(repo),'action':'select','stash':'stash@{0}'})
    if command.startswith('fixture_'):
        tool('ui_layout', {'action':'test_dropdowns','text':command})
    else:
        tool('ui_command', {'repo':str(repo),'command':command,**args})
    try:
        deadline=time.monotonic()+10
        frames=[]
        while time.monotonic()<deadline:
            frames=[f for f in tool('ui_layout')['operationForms'] if f['popup']==view and f['values']]
            if frames and any(f['captions'] for f in frames):break
            time.sleep(.04)
        assert frames and any(f['captions'] for f in frames),(locale,view,command)
        labels=[label for f in frames for label in f['captions']]
        left=min(label['bounds']['x'] for label in labels)-frames[0]['body']['x']
        right=frames[0]['body']['x']+frames[0]['body']['width']-max(v['x']+v['width'] for f in frames for v in f['values'])
        aligned=abs(left-right)<=1.5 and left>=-1 and right>=-1
        labels_fit=all(label['textWidth']<=label['bounds']['localWidth']+1.1 for label in labels)
        advanced=frames[0]['advancedOptions']
        sample={'operation':command,'locale':locale,'left':left,'right':right,'labels':labels,
                'bodyWidth':frames[0]['body']['localWidth'],'advancedOptions':advanced}
        records.append(sample)
        check(f'{locale} {command} balances the outer margins and fits each caption',aligned and labels_fit)
        if command in {'push_dialog','create_branch'}:
            check(f'{locale} {command} aligns the advanced caption with the first checkbox baseline',
                  advanced is not None and abs(advanced['baselineDifference'])<=.5)
        if command=='fetch_dialog':
            probe={'action':'test_menu','text':'checkbox_initial_appearance','button':'FetchForceCheckBox','value':0}
            initial=tool('ui_layout',probe)
            check(f'{locale} Fetch opens with the regular unchecked border before any click',initial['ready'] and initial['focused'] and
                  not initial['checked'] and not initial['focusVisible'] and initial['thickness']==1 and initial['stroke']==initial['plainStroke'])
            if locale=='zh_CN':(out/'fetch-opening.png').write_bytes(tool('ui_screenshot'))
            tool('ui_layout', {'action':'test_set','button':'FetchForceCheckBox','value':1})
            tool('ui_command', {'repo':str(repo),'command':'cancel_popup'})
            tool('ui_command', {'repo':str(repo),'command':'fetch_dialog'})
            deadline=time.monotonic()+10
            selected={'ready':False}
            while time.monotonic()<deadline:
                selected=tool('ui_layout',probe)
                if selected['ready'] and selected['focused']: break
                time.sleep(.04)
            check(f'{locale} Fetch reopens with the regular checked border',selected['ready'] and selected['checked'] and not selected['focusVisible'] and
                  selected['thickness']==1 and selected['stroke']==selected['selectedStroke'] and selected['fill']==selected['selectedFill'])
            tool('ui_layout', {'action':'test_set','button':'FetchForceCheckBox','value':0})
        if command in {'push_dialog','create_branch','stash_dialog'}:
            (out/(locale+'-'+command+'-spacing.png')).write_bytes(tool('ui_screenshot'))
        if locale=='zh_CN' and command in {'push_dialog','pull_dialog','create_branch','merge_dialog','stash_dialog','fixture_discard_all','create_tag'}:
            (out/(command+'-margins.png')).write_bytes(tool('ui_screenshot'))
        return sample
    finally:
        tool('ui_command', {'repo':str(repo),'command':'cancel_popup'})

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
    before=(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'))
    for locale,theme,scale in ('zh_CN','Light',1),('zh_TW','Dark',1.25),('en_US','Blue',1.5):
        tool('ui_window',{'locale':locale,'theme':theme,'zoom':scale})
        samples={}
        for view,command,args in [
            ('Fetch','fetch_dialog',{}),('Pull','pull_dialog',{}),('Push','push_dialog',{}),
            ('CreateBranch','create_branch',{}),('RenameBranch','fixture_rename_branch',{}),('DeleteBranch','fixture_delete_branch',{}),
            ('Merge','merge_dialog',{'revision':lost}),('Rebase','fixture_rebase',{}),('Reset','reset_dialog',{}),
            ('StashChanges','stash_dialog',{}),('ApplyStash','stash_apply_dialog',{}),('DropStash','stash_drop_dialog',{}),
            ('CheckoutBranchFromStash','stash_branch_dialog',{}),('Discard','fixture_discard_all',{}),
            ('Discard','fixture_discard_one',{}),('Discard','fixture_discard_many',{}),('Apply','apply_patch',{}),
            ('CreateTag','create_tag',{}),('DeleteTag','fixture_delete_tag',{}),('PushTag','fixture_push_tag',{}),
        ]:
            samples[command]=audit_operation(locale,view,command,**args)
        push_gap=samples['push_dialog']['advancedOptions']['gap']
        branch_gap=samples['create_branch']['advancedOptions']['gap']
        check(f'{locale} Push and Create Branch share the 8-DIP gap above advanced options',
              abs(push_gap-branch_gap)<=.5 and abs(push_gap-8*scale)<=1)
        check(f'{locale} Stash uses the same width as Push, Pull, and Create Branch',
              max(samples[command]['bodyWidth'] for command in ('push_dialog','pull_dialog','create_branch','stash_dialog')) -
              min(samples[command]['bodyWidth'] for command in ('push_dialog','pull_dialog','create_branch','stash_dialog')) <=1)
    check('opening and cancelling operation forms preserves real Git state',
          before==(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')))
    tool('ui_window',{'locale':'zh_CN','zoom':1})
    tool('ui_select_commit',{'repo':str(repo),'revision':'v1.0'})
    deadline=time.monotonic()+10
    while time.monotonic()<deadline and not tool('ui_state')['layoutProbe']['taggedRefs']:
        time.sleep(.04)
    for theme in ['Blue','Light','Dark','Default','Blue']:
        tool('ui_window',{'theme':theme})
        state=capture('tag-theme-'+theme.lower())
        colors=state['layoutProbe']['taggedRefs']
        check('live tag follows graph lane for '+theme,bool(colors) and all(c['color'] is not None and c['color']==c['graphColor'] for c in colors))
    for zoom in ([float(os.environ['TREEGIT_AUDIT_ZOOM'])] if 'TREEGIT_AUDIT_ZOOM' in os.environ else [1,1.5,2,2.5]):
        print('Opening small window at zoom '+str(zoom),flush=True)
        tool('ui_window',{'width':1024,'height':720,'zoom':zoom})
        for page, tab_index in [('general',0),('appearance',1),('git',0),('signing',2),('integration',0),('terminal',1),('diff-merge',2)]:
            tool('ui_command',{'command':'preferences','page':page})
            state=capture(f'preferences-{page}-{zoom}',True)
            dialogs=state['dialogs']
            check(f'preferences {page} {zoom} remains within screen',len(dialogs)==1 and within(dialogs[0]))
            probe=state['layoutProbe']['dialogs'][0]
            own_settings = len(probe['tabs']) == 1 and len(probe['tabs'][0]['visibleHeaders']) == 3 and probe['tabs'][0]['selected'] == tab_index
            check(f'preferences {page} {zoom} shows only its own settings',own_settings and bool(probe['content']))
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
                check(f'SSH generator {zoom} retains a scrollable body',any(s['name']=='KeyViewport' and s['visible'] and s['adaptive'] for s in g['scrolls']))
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
        close_owned_gui(exe,pid)
    assert root.name.startswith('TreeGit dialog audit ') and repo.parent==root and not root.is_symlink() and not root.is_junction()
    def readonly(fn,path,_):
        owned=Path(path)
        assert owned.resolve().is_relative_to(root) and not owned.is_symlink() and not owned.is_junction()
        owned.chmod(owned.stat().st_mode | stat.S_IWRITE)
        fn(path)
    shutil.rmtree(root,onerror=readonly)
