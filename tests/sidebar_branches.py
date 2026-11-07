"""Real sidebar gestures and checkout safety, restricted to an owned Windows CI desktop."""
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

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows', 'Native input is CI-only'
exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit sidebar QA ')).resolve()
repo = root/'repo'; repo.mkdir()
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1')
checks = []
pid = None
failure = None

def git(*args):
    result = subprocess.run(['git', '-C', str(repo), *args], env=env, capture_output=True, text=True, encoding='utf-8')
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()

git('init', '-b', 'main'); git('config', 'user.name', 'Sidebar QA'); git('config', 'user.email', 'qa@example.invalid')
(repo/'file.txt').write_text('base\n', encoding='utf-8'); git('add', '.'); git('commit', '-m', 'Initial')
base = git('rev-parse', 'HEAD'); tree = git('rev-parse', 'HEAD^{tree}')
def child(parent, message): return git('commit-tree', tree, '-p', parent, '-m', message)
local = child(base, 'Other local'); remote = child(base, 'Remote change')
git('branch', 'other', local)
git('remote', 'add', 'origin', '.')
for name in ('new', 'renamed', 'ahead', 'diverged', 'behind', 'associate', 'collision', 'dirty', 'unrelated', '中文"branch'):
    git('update-ref', 'refs/remotes/origin/'+name, remote)
git('branch', 'ahead', child(remote, 'Unpushed local'))
git('branch', '--set-upstream-to=origin/ahead', 'ahead')
git('branch', 'diverged', child(base, 'Diverged local'))
git('branch', '--set-upstream-to=origin/diverged', 'diverged')
git('branch', 'behind', base); git('branch', '--set-upstream-to=origin/behind', 'behind')
git('branch', 'associate', local)
git('branch', 'collision', base); git('branch', '--set-upstream-to=origin/unrelated', 'collision')
ahead = git('rev-parse', 'ahead'); diverged = git('rev-parse', 'diverged')
(repo/'file.txt').write_text('stashed\n', encoding='utf-8'); git('stash', 'push', '-m', 'Sidebar fixture')
(repo/'.git/sourcegit.uistates').write_text(json.dumps({'ShowOnlyGraphBranch':True,'ShowRemoteBranchesInHistory':False,'IsLocalBranchesExpandedInSideBar':True,'IsRemotesExpandedInSideBar':True,'ExpandedBranchNodesInSideBar':['refs/remotes/origin']}), encoding='utf-8')
preferences = exe.parent/'data/preference.json'; preferences.parent.mkdir(exist_ok=True)
preferences.write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','MaxHistoryCommits':200,'EnableAutoFetch':False,'Check4UpdatesOnStartup':False,'UseStashAndReapplyByDefault':True}), encoding='utf-8')
p = subprocess.Popen([str(exe), '--mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue()
threading.Thread(target=lambda: [messages.put(line) for line in p.stdout], daemon=True).start()
seq = 0
def rpc(method, params=None):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n'); p.stdin.flush()
    response = json.loads(messages.get(timeout=60)); assert 'error' not in response, response
    return response['result']
def tool(name, args=None):
    response = rpc('tools/call', {'name':name,'arguments':args or {}}); assert not response.get('isError'), response
    return base64.b64decode(response['content'][0]['data']) if name == 'ui_screenshot' else json.loads(response['content'][0]['text'])
def state(): return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)
def wait_for(predicate):
    for _ in range(150):
        value = predicate()
        if value: return value
        time.sleep(.1)
    raise AssertionError('Timed out: '+str(tool('ui_state')['activePage']))
def check(label, condition):
    assert condition, label
    checks.append(label); print('PASS '+label, flush=True)
def page(): return tool('ui_state')['activePage']
def shot(name): (out/(name+'.png')).write_bytes(tool('ui_screenshot'))

api = ctypes.WinDLL('user32', use_last_error=True)
api.WindowFromPoint.argtypes = [wintypes.POINT]; api.WindowFromPoint.restype = wintypes.HWND
api.GetForegroundWindow.restype = wintypes.HWND
api.SetForegroundWindow.argtypes = [wintypes.HWND]
api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
api.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]; api.GetAncestor.restype = wintypes.HWND
api.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
api.VkKeyScanW.argtypes = [wintypes.WCHAR]; api.VkKeyScanW.restype = ctypes.c_short
def owner(hwnd):
    value = wintypes.DWORD(); api.GetWindowThreadProcessId(hwnd, ctypes.byref(value)); return value.value
def click(x, y, double=False):
    point = wintypes.POINT(round(x), round(y))
    hwnd = api.WindowFromPoint(point)
    assert owner(hwnd) == pid, 'Refusing input outside the owned CI window'
    api.SetForegroundWindow(hwnd)
    assert api.SetCursorPos(point.x, point.y)
    for index in range(2 if double else 1):
        assert owner(api.WindowFromPoint(point)) == pid
        api.mouse_event(2,0,0,0,0); time.sleep(.04); api.mouse_event(4,0,0,0,0)
        if double and index == 0: time.sleep(.07)
    time.sleep(.45)
def key(code, ctrl=False):
    assert owner(api.GetForegroundWindow()) == pid, 'Refusing keyboard input outside the owned CI window'
    if ctrl: api.keybd_event(0x11,0,0,0)
    api.keybd_event(code,0,0,0); api.keybd_event(code,0,2,0)
    if ctrl: api.keybd_event(0x11,0,2,0)
def type_name(value):
    key(0x41, ctrl=True)
    for char in value:
        code = api.VkKeyScanW(char); assert code >= 0
        if code & 0x100: key_shift = True; api.keybd_event(0x10,0,0,0)
        else: key_shift = False
        key(code & 255)
        if key_shift: api.keybd_event(0x10,0,2,0)
    time.sleep(.25)
def branch_target(reference):
    tool('ui_graph', {'repo':str(repo),'branch':reference})
    return wait_for(lambda: next((b for b in (state()['localBranchTargets'] or [])+(state()['remoteBranchTargets'] or []) if b['reference']==reference), None))
def branch_click(reference, source='history', double=False):
    branch_target(reference)
    tool('ui_select_view', {'repo':str(repo),'view':source})
    time.sleep(.25)
    target = wait_for(lambda: next((b for b in (state()['localBranchTargets'] or [])+(state()['remoteBranchTargets'] or []) if b['reference']==reference), None))
    click(target['x'], target['y'], double)
    wait_for(lambda: state()['view']==0 and not state()['searchActive'] and not state()['workingCopySelected'])
def button(name):
    b = wait_for(lambda: next((b for b in tool('ui_layout')['buttons'] if b['name']==name and b['enabled']), None))
    hwnd = api.GetAncestor(api.GetForegroundWindow(), 2)
    assert owner(hwnd)==pid
    origin = wintypes.POINT(); assert api.ClientToScreen(hwnd, ctypes.byref(origin))
    bounds = b['bounds']; click(origin.x+(bounds['x']+bounds['width']/2), origin.y+(bounds['y']+bounds['height']/2))
def cancel():
    button('CancelPopup'); wait_for(lambda: page()['pendingDialog'] is None)
def confirm(branch):
    button('ConfirmPopup'); wait_for(lambda: page()['pendingDialog'] is None)
    wait_for(lambda: not state()['graphLoading'] and state()['branch']==branch)
def refresh():
    tool('ui_refresh', {'repo':str(repo)})
    wait_for(lambda: not state()['graphLoading']); time.sleep(.3)

try:
    rpc('initialize', {'protocolVersion':'2025-11-25'})
    tool('ui_open_repository', {'repo':str(repo)})
    pid = tool('ui_state')['processId']
    tool('ui_window', {'width':1440,'height':900,'state':'maximized','theme':'Blue'})
    wait_for(lambda: not state()['graphLoading'] and state()['branch']=='main')
    for mode in ('current', 'all'):
        tool('ui_graph', {'repo':str(repo),'mode':mode,'showRemotes':True})
        for source in ('search','working_copy','stashes','history'):
            for ref in ('refs/heads/main','refs/heads/other','refs/remotes/origin/new'):
                branch_click(ref, source)
                s = state()
                check(mode+' '+source+' single '+ref+' routes to history without checkout',git('branch','--show-current')=='main' and s['graphMode']==(1 if mode=='current' else 0) and s['graphShowRemotes'])
                check('single-click never opens checkout: '+source+' '+ref,page()['pendingDialog'] is None)
        branch_click('refs/heads/main','search',True)
        check(mode+' double current only returns to history',page()['pendingDialog'] is None and git('rev-parse','HEAD')==base)
    branch_click('refs/heads/other','working_copy',True)
    check('clean other-local double click asks before checkout',page()['pendingDialog']=='Checkout' and git('branch','--show-current')=='main')
    shot('local-confirmation'); cancel()
    check('cancel local checkout leaves HEAD unchanged',git('rev-parse','HEAD')==base)
    branch_click('refs/heads/other','stashes',True); confirm('other')
    check('confirmed local checkout selects the requested branch',git('rev-parse','HEAD')==local)
    branch_click('refs/heads/other','search',True)
    check('current branch after checkout also has no confirmation',page()['pendingDialog'] is None)

    for zoom,width,height in ((1,1440,900),(1.5,1024,720)):
        tool('ui_window', {'zoom':zoom,'width':width,'height':height,'state':'normal'})
        branch_click('refs/remotes/origin/new','search',True)
        check('remote checkout has dedicated dialog at zoom '+str(zoom),page()['pendingDialog']=='CheckoutRemoteBranch')
        check('remote dialog does not create branch before confirmation',not git('branch','--list','new'))
        buttons=tool('ui_layout')['buttons']; b=next(b for b in buttons if b['name']=='ConfirmPopup')
        check('remote confirmation remains on screen at zoom '+str(zoom),b['bounds']['height']>0 and not b['outsideParentBorder'])
        shot('remote-checkout-'+str(zoom)); cancel()
    tool('ui_window', {'zoom':1,'state':'maximized'})
    branch_click('refs/remotes/origin/new','search',True); confirm('new')
    check('first remote checkout attaches local tracking branch',git('rev-parse','HEAD')==remote and git('rev-parse','--symbolic-full-name','@{upstream}')=='refs/remotes/origin/new')
    branch_click('refs/remotes/origin/renamed','working_copy',True)
    type_name('custom-local')
    wait_for(lambda: page()['remoteCheckout']['name']=='custom-local')
    confirm('custom-local')
    check('renamed local checkout explicitly tracks the selected remote',git('rev-parse','--symbolic-full-name','@{upstream}')=='refs/remotes/origin/renamed')
    branch_click('refs/remotes/origin/中文"branch','search',True); confirm('中文"branch')
    check('Unicode and quoted branch names checkout and track correctly',git('rev-parse','--symbolic-full-name','@{upstream}')=='refs/remotes/origin/中文"branch')
    for name,expected in (('ahead',ahead),('diverged',diverged)):
        branch_click('refs/remotes/origin/'+name,'search',True)
        check(name+' reuses the existing branch and offers no unsafe update',page()['remoteCheckout']['name']==name and not page()['remoteCheckout']['canUpdateRemote'])
        confirm(name)
        check(name+' checkout preserves all local commits',git('rev-parse','HEAD')==expected)
    branch_click('refs/remotes/origin/behind','search',True)
    check('behind branch offers a fast-forward',page()['remoteCheckout']['canUpdateRemote'] and page()['remoteCheckout']['updateRemote'])
    confirm('behind')
    check('fast-forward reaches the remote tip',git('rev-parse','HEAD')==remote)
    branch_click('refs/remotes/origin/associate','working_copy',True); confirm('associate')
    check('existing untracked branch is associated without replacing its commits',git('rev-parse','HEAD')==local and git('rev-parse','--symbolic-full-name','@{upstream}')=='refs/remotes/origin/associate')
    branch_click('refs/remotes/origin/collision','search',True)
    check('a conflicting upstream requires another local name',page()['remoteCheckout']['hasErrors'])
    cancel()
    check('cancelled conflicting remote checkout keeps the original branch',git('branch','--show-current')=='associate' and git('rev-parse','collision')==base)
    (repo/'file.txt').write_text('pending user work\n',encoding='utf-8'); refresh()
    wait_for(lambda: state()['changes']>0)
    stash_before=git('rev-parse','refs/stash')
    branch_click('refs/remotes/origin/dirty','working_copy',True)
    check('dirty remote checkout shows change handling options',page()['remoteCheckout']['hasLocalChanges'])
    shot('remote-checkout-with-changes'); cancel()
    check('cancelling keeps worktree and stash untouched',(repo/'file.txt').read_text(encoding='utf-8')=='pending user work\n' and git('rev-parse','refs/stash')==stash_before)
    branch_click('refs/remotes/origin/dirty','search',True); confirm('dirty')
    check('stash-and-reapply preserves working changes and the prior stash',(repo/'file.txt').read_text(encoding='utf-8')=='pending user work\n' and git('rev-parse','refs/stash')==stash_before)
    check('stash-and-reapply leaves no staging changes',git('diff','--cached','--name-only')=='')
except BaseException as error:
    failure=repr(error)
    try:
        shot('failure')
        (out/'failure-state.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2),encoding='utf-8')
    except BaseException: pass
    raise
finally:
    (out/'sidebar-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close(); p.wait(timeout=15)
    if pid:
        code="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell','-NoProfile','-Command',code],capture_output=True,check=True)
    assert root.parent==Path(tempfile.gettempdir()).resolve() and root.name.startswith('TreeGit sidebar QA ')
    for item in (root,*root.rglob('*')): assert not item.is_symlink() and not item.is_junction()
    def remove_readonly(function,path,error):
        owned=Path(path); assert owned.resolve().is_relative_to(root)
        owned.chmod(owned.stat().st_mode | stat.S_IWRITE); function(path)
    shutil.rmtree(root,onerror=remove_readonly)
    preferences.write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','MaxHistoryCommits':20000,'EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
