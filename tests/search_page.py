"""Search-page navigation, all original search methods, and off-graph details."""
import base64
import os
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
repo = Path(tempfile.mkdtemp(prefix='TreeGit search ')).resolve()
checks = []

def git(*args):
    p = subprocess.run([shutil.which('git'), *args], cwd=repo, capture_output=True, text=True, encoding='utf-8')
    assert p.returncode == 0, p.stderr
    return p.stdout.strip()

git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit QA'); git('config', 'user.email', 'qa@example.invalid')
(repo/'sensor.txt').write_text('sensor initial\n'); git('add', '.'); git('commit', '-m', 'Initial sensor')
git('checkout', '-b', 'feature/search')
(repo/'calibration.txt').write_text('unique_calibration_value\n'); git('add', '.'); git('commit', '-m', 'Calibrate sensor')
feature = git('rev-parse', 'HEAD'); git('checkout', 'main')
initial=git('rev-parse','HEAD')
git('remote','add','origin','.');git('update-ref','refs/remotes/origin/feature/search',feature);git('branch','-D','feature/search')
for index in range(6):
    (repo/'sensor.txt').write_text('sensor revision '+str(index)+'\n');git('add','.');git('commit','-m','Main revision '+str(index))
main_head=git('rev-parse','HEAD')
git('checkout','--detach',initial)
(repo/'orphan.txt').write_text('Unreachable search result\n');git('add','.');git('commit','-m','Detached calibration')
unreachable=git('rev-parse','HEAD');git('checkout','main')
(repo/'.git/sourcegit.uistates').write_text(json.dumps({'ShowOnlyGraphBranch':True,'ShowRemoteBranchesInHistory':False,'HistoryShowFlags':6,'IsLocalBranchesExpandedInSideBar':False,'IsRemotesExpandedInSideBar':False}),'utf-8')
(exe.parent/'data').mkdir(exist_ok=True)
(exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','MaxHistoryCommits':2,'EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),'utf-8')
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
seq = 0
gui_pid = None

def rpc(method, params={}):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0', 'id':seq, 'method':method, 'params':params})+'\n'); p.stdin.flush()
    r = json.loads(p.stdout.readline()); assert 'error' not in r, r
    return r['result']

def tool(name, args={}):
    r = rpc('tools/call', {'name':name, 'arguments':args}); assert not r.get('isError'), r
    return base64.b64decode(r['content'][0]['data']) if name == 'ui_screenshot' else json.loads(r['content'][0]['text'])

def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)

def check(name, ok):
    assert ok, name
    checks.append(name); print('PASS', name, flush=True)

def search(query, method='message', current=False):
    tool('ui_search', {'repo':str(repo), 'query':query, 'method':method, 'currentBranchOnly':current})
    for _ in range(150):
        s = state()
        if not s['searchQuerying']: return s
        time.sleep(.1)
    raise AssertionError('Search did not finish')

def wait_refs(expected,absent=()):
    for _ in range(150):
        current=state();refs={b['reference']:b['head'] for b in current['loadedBranches']}
        if all(refs.get(ref)==sha for ref,sha in expected.items()) and all(ref not in refs for ref in absent):return
        time.sleep(.1)
    raise AssertionError(current)

def verify_coverage(label,sha,current):
    expected=[]
    for line in git('for-each-ref','--contains='+sha,'--format=%(refname)%09%(symref)','refs/heads','refs/remotes').splitlines():
        parts=line.split('\t')
        if len(parts)==1 or not parts[1]: expected.append(parts[0])
    row=next(c for c in current['searchResults'] if c['sha']==sha)
    check(label+' exact containing refs match Git',set(row['branches'])==set(expected))
    check(label+' branch count matches refs without aliases',row['branchCount']==len(expected))
    local=sorted(r for r in expected if r.startswith('refs/heads/'))
    remote=sorted(r for r in expected if r.startswith('refs/remotes/'))
    preferred='refs/heads/main' if 'refs/heads/main' in local else (local+remote+[None])[0]
    name=preferred.removeprefix('refs/heads/').removeprefix('refs/remotes/') if preferred else '无分支'
    check(label+' primary name is an actually containing branch, with current first',row['branch']==name)
    return row

def open_history(label,sha,local=None,remote=None):
    tool('ui_graph',{'repo':str(repo),'mode':'current','showRemotes':False})
    for _ in range(100):
        current=state()
        if not current['graphLoading'] and current['graphMode']==1 and not current['graphShowRemotes']:break
        time.sleep(.1)
    search(sha,'sha');tool('ui_search',{'repo':str(repo),'select':sha})
    current=state()
    verify_coverage(label,sha,current)
    check(label+' single selection remains on the search page',current['searchActive'] and current['searchSelected']==sha)
    tool('ui_search',{'repo':str(repo),'open':True})
    for _ in range(150):
        current=state()
        if (not current['graphLoading'] and not current['searchActive'] and current['view']==0 and
            current['graphMode']==0 and current['graphShowRemotes'] and current['historyVisible'] and
            current['selectedCommits']==[sha] and current['historySelectedCommits']==[sha] and current['detailCommit']==sha and
            (current['localBranchSelection'] or [])==([local] if local else []) and
            (current['remoteBranchSelection'] or [])==([remote] if remote else []) and
            (not local or current['localBranchTreeVisible']) and (not remote or current['remoteBranchTreeVisible'])):break
        time.sleep(.1)
    else:raise AssertionError(current)
    check(label+' opens history with all branches and remotes',current['graphMode']==0 and current['graphShowRemotes'] and not current['searchActive'])
    check(label+' keeps the selected result and matching details after sidebar selection',current['historySelectedCommits']==[sha] and current['detailCommit']==sha)
    check(label+' selects the expected local branch', (current['localBranchSelection'] or [])==([local] if local else []))
    check(label+' selects the expected remote branch', (current['remoteBranchSelection'] or [])==([remote] if remote else []))
    check(label+' expands the corresponding sidebar group', (not local or current['localBranchTreeVisible']) and (not remote or current['remoteBranchTreeVisible']))
    check(label+' is included without unlimited history loading',sha in {c['sha'] for c in current['graphCommits']} and current['graphTotal']<=202)
    check(label+' never checks out or changes HEAD',git('branch','--show-current')=='main' and git('rev-parse','HEAD')==main_head)
    (out/(label+'-history.png')).write_bytes(tool('ui_screenshot'))

failure=None
try:
    rpc('initialize', {'protocolVersion':'2025-11-25'})
    tool('ui_open_repository', {'repo':str(repo)})
    gui_pid=tool('ui_state')['processId']
    tool('ui_window', {'width':1440, 'height':900, 'theme':'Blue','state':'maximized'})
    for _ in range(100):
        if not state()['graphLoading'] and state()['branch']=='main' and state()['graphTotal']>0: break
        time.sleep(.1)
    before = state()
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    time.sleep(.3); s = state()
    check('search opens in the right page with the branch sidebar retained', s['searchActive'] and s['sidebarVisible'])
    check('search does not change the sidebar width', s['sidebarWidth'] == before['sidebarWidth'])
    check('new search page has no selected commit', s['searchSelected'] is None)
    (out/'search-empty.png').write_bytes(tool('ui_screenshot'))
    for method, query in [('message','Calibrate'), ('author','TreeGit QA'), ('path','calibration.txt'), ('content','unique_calibration_value'), ('sha',feature)]:
        s = search(query, method)
        check(method+' search finds the unmerged commit', feature in {c['sha'] for c in s['searchResults'] or []})
    rows={c['sha']:c['branch'] for c in search('TreeGit QA','author')['searchResults']}
    check('batched branch labels identify a remote-only result',rows[feature]=='origin/feature/search')
    check('batched branch labels identify older local-only commits',rows[git('rev-parse','main~2')]=='main')
    check('batched branch labels identify the checked-out local branch',rows[main_head]=='main')
    search(feature,'sha')
    tool('ui_search', {'repo':str(repo), 'select':feature})
    time.sleep(.5); s = state()
    check('selecting an off-graph result shows its commit details', s['searchSelected'] == feature and s['detailView'] == 'CommitDetail')
    (out/'search-result.png').write_bytes(tool('ui_screenshot'))
    check('search selection never checks out a different branch', git('branch', '--show-current') == 'main')
    s = search('Calibrate', current=True)
    check('current-branch search excludes unmerged results', not s['searchResults'])
    s = search('')
    check('clearing the search clears results and selection', not s['searchResults'] and s['searchSelected'] is None and not s['searchQuerying'])
    search('Calibrate')
    open_history('remote-result',feature,remote='refs/remotes/origin/feature/search')
    open_history('older-result',initial,remote='refs/remotes/origin/feature/search')
    open_history('unreachable-result',unreachable)
    open_history('local-only-result',git('rev-parse','main~2'),local='refs/heads/main')

    git('branch','local/calibration',initial)
    git('config','branch.local/calibration.remote','origin')
    git('config','branch.local/calibration.merge','refs/heads/feature/search')
    tool('ui_refresh',{'repo':str(repo)})
    wait_refs({'refs/heads/local/calibration':initial})
    open_history('tracked-local-behind',feature,local='refs/heads/local/calibration')
    check('renamed local tracking branch is still behind the searched remote result',git('rev-parse','local/calibration')!=feature)

    ahead=git('commit-tree',git('rev-parse',feature+'^{tree}'),'-p',feature,'-m','Local tracking commit')
    git('update-ref','refs/heads/local/calibration',ahead)
    tool('ui_refresh',{'repo':str(repo)})
    wait_refs({'refs/heads/local/calibration':ahead})
    for _ in range(150):
        current=state()
        if not current['graphLoading'] and current['localBranchSelection']==['refs/heads/local/calibration'] and current['historySelectedCommits']==[feature]:break
        time.sleep(.1)
    else:raise AssertionError(current)
    check('background branch refresh preserves sidebar selection without moving the result to the new branch tip',current['historySelectedCommits']==[feature])
    open_history('tracked-local-ahead',ahead,local='refs/heads/local/calibration')
    open_history('unreachable-after-local',unreachable)

    git('branch','-D','local/calibration');tool('ui_refresh',{'repo':str(repo)})
    wait_refs({},absent=['refs/heads/local/calibration'])
    open_history('remote-after-local-removal',feature,remote='refs/remotes/origin/feature/search')

    search(feature,'sha');tool('ui_search',{'repo':str(repo),'open':True})
    tool('ui_working_copy',{'repo':str(repo)})
    for _ in range(150):
        if not state()['graphLoading']:break
        time.sleep(.1)
    time.sleep(.3)
    check('a later file-status action is not overridden by a pending search jump',state()['workingCopySelected'] and not state()['searchActive'])

    search('Calibrate')
    tool('ui_select_view', {'repo':str(repo), 'view':'history'}); s = state()
    check('history tab exits search and restores the history page', not s['searchActive'] and s['view'] == 0 and s['sidebarVisible'])
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    tool('ui_working_copy', {'repo':str(repo)}); s = state()
    check('file-status tab exits search and shows working-copy overview', not s['searchActive'] and s['workingCopySelected'])
    (out/'file-status-tab.png').write_bytes(tool('ui_screenshot'))
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    tool('ui_select_view', {'repo':str(repo), 'view':'working_copy'})
    check('full commit page also exits search', not state()['searchActive'])
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    tool('ui_window', {'width':1024, 'height':720})
    (out/'search-narrow.png').write_bytes(tool('ui_screenshot'))
    tool('ui_select_view', {'repo':str(repo), 'view':'history'})

    # Derivation, merge propagation, unchanged historical containment after revert,
    # and remote-only membership all use real Git ancestry rather than branch names.
    child=git('commit-tree',git('rev-parse',feature+'^{tree}'),'-p',feature,'-m','Derived calibration')
    merged=git('commit-tree',git('rev-parse',main_head+'^{tree}'),'-p',main_head,'-p',feature,'-m','Merged calibration')
    reverted=git('commit-tree',git('rev-parse',initial+'^{tree}'),'-p',feature,'-m','Revert calibration with unrelated changes')
    fixtures={'derived/child':child,'derived/grandchild':child,'merge/receiver':merged,'revert/receiver':reverted,'before/calibration':initial,'unrelated/main':main_head}
    for name,tip in fixtures.items():git('update-ref','refs/heads/'+name,tip)
    for index in range(70):git('update-ref','refs/remotes/origin/derived/long-name-'+str(index).zfill(2),child)
    git('symbolic-ref','refs/remotes/origin/HEAD','refs/remotes/origin/feature/search')
    tool('ui_refresh',{'repo':str(repo)})
    wait_refs({'refs/heads/derived/child':child,'refs/heads/merge/receiver':merged})
    s=search(feature,'sha');row=verify_coverage('derived coverage',feature,s)
    paged_refs=[]
    for offset in range(0,row['branchCount'],16):
        page_state=tool('ui_state',{'searchOffset':0,'searchLimit':1,'searchBranchOffset':offset,'searchBranchLimit':16})
        page=next(r for r in page_state['repositories'] if Path(r['repo']).resolve()==repo)
        result=page['searchResults'][0]
        check('MCP branch page preserves the full count '+str(offset),page['searchResultCount']==1 and result['sha']==feature and result['branchCount']==row['branchCount'] and len(result['branches'])<=16 and result['branchesTruncated']==(offset+16<row['branchCount']))
        paged_refs.extend(result['branches'])
    check('MCP pagination exposes every containing branch without duplication',set(paged_refs)==set(row['branches']) and len(paged_refs)==len(set(paged_refs)))
    count_state=tool('ui_state',{'searchLimit':0,'searchBranchLimit':0})
    count_page=next(r for r in count_state['repositories'] if Path(r['repo']).resolve()==repo)
    check('MCP count-only inspection preserves search and returns no rows',count_page['searchResultCount']==1 and count_page['searchResults']==[] and count_page['searchResultsTruncated'] and count_page['searchActive'])
    check('derived descendants and merge recipients contain the original',all('refs/heads/'+name in row['branches'] for name in ('derived/child','derived/grandchild','merge/receiver')))
    check('revert never removes original ancestry from coverage','refs/heads/revert/receiver' in row['branches'])
    check('branches forked before the change are excluded','refs/heads/before/calibration' not in row['branches'] and 'refs/heads/unrelated/main' not in row['branches'])
    check('symbolic remote HEAD is excluded','refs/remotes/origin/HEAD' not in row['branches'])
    bulk=search('TreeGit QA','author')
    for commit in bulk['searchResults']:verify_coverage('bulk '+commit['sha'][:8],commit['sha'],bulk)
    tool('ui_search',{'repo':str(repo),'select':feature})
    (out/'containing-branches-search-row.png').write_bytes(tool('ui_screenshot'))
    popup=tool('ui_search',{'repo':str(repo),'showBranches':True})['branchPopup']
    check('popup lists every containing branch in local/remote groups',popup['total']==row['branchCount'] and len(popup['rows'])==row['branchCount'] and len(popup['groups'])==2)
    check('opening coverage stays on the search result',state()['searchActive'] and state()['searchSelected']==feature)
    (out/'containing-branches-popup.png').write_bytes(tool('ui_screenshot',{'window':'active'}))
    for theme in ('Blue','Light','Dark'):
        tool('ui_window',{'theme':theme})
        colors=tool('ui_search',{'repo':str(repo)})['branchPopup']
        def luminance(color):
            value=str(color).lstrip('#')[-6:]
            rgb=[int(value[i:i+2],16)/255 for i in (0,2,4)]
            linear=[v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4 for v in rgb]
            return sum(v*w for v,w in zip(linear,(.2126,.7152,.0722)))
        foreground=luminance(colors['foreground']);background=luminance(colors['background'])
        check('popup text has readable contrast in '+theme,(max(foreground,background)+.05)/(min(foreground,background)+.05)>=4.5)
        foreground=luminance(colors['branchForeground'])
        check('popup branch names have readable contrast in '+theme,(max(foreground,background)+.05)/(min(foreground,background)+.05)>=4.5)
        (out/('containing-branches-'+theme+'.png')).write_bytes(tool('ui_screenshot',{'window':'active'}))
    tool('ui_window',{'theme':'Blue'})
    filtered=tool('ui_search',{'repo':str(repo),'branchFilter':'DERIVED/CHILD'})['branchPopup']
    check('popup filtering is case-insensitive and keeps only matching branches',len(filtered['rows'])==1 and filtered['rows'][0]['reference']=='refs/heads/derived/child')
    (out/'containing-branches-filtered.png').write_bytes(tool('ui_screenshot',{'window':'active'}))
    empty=tool('ui_search',{'repo':str(repo),'branchFilter':'no-such-branch'})['branchPopup']
    check('empty popup filter leaves the total and no results',not empty['rows'] and empty['total']==row['branchCount'])
    tool('ui_search',{'repo':str(repo),'branchFilter':''})
    # Exercise the real popup button with native mouse input only in our CI desktop.
    if os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_OS')=='Windows':
        import ctypes
        from ctypes import wintypes
        user32=ctypes.windll.user32
        user32.WindowFromPoint.argtypes=[wintypes.POINT];user32.WindowFromPoint.restype=wintypes.HWND
        user32.GetWindowThreadProcessId.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.DWORD)]
        def owned_move(x,y):
            point=wintypes.POINT(x,y)
            handle=user32.WindowFromPoint(point);owner=wintypes.DWORD()
            user32.GetWindowThreadProcessId(handle,ctypes.byref(owner))
            assert owner.value==gui_pid,('Refusing pointer input outside the owned CI GUI',owner.value,gui_pid)
            assert user32.SetCursorPos(point.x,point.y)
        for _ in range(50):
            popup=tool('ui_search',{'repo':str(repo)})['branchPopup']
            target=popup and popup['firstTarget']
            if target and target['reference']=='refs/heads/derived/child' and popup['firstGroupRegion']:break
            time.sleep(.1)
        else:raise AssertionError(popup)
        owned_move(popup['bounds']['x']+12,popup['bounds']['y']+12)
        for _ in range(50):
            popup=tool('ui_search',{'repo':str(repo)})['branchPopup']
            if not popup['firstTarget']['hovered']:break
            time.sleep(.1)
        background=str(popup['firstTarget']['background']).lower()
        check('branch names have no normal background: '+background,background=='transparent' or background.startswith('#00'))
        before_hover=out/'containing-branches-before-hover.png'
        after_hover=out/'containing-branches-first-row-hover.png'
        before_hover.write_bytes(tool('ui_screenshot',{'window':'active'}))
        target=popup['firstTarget'];region=popup['firstGroupRegion']
        assert target['enabled'],target
        owned_move(target['x'],target['y'])
        for _ in range(50):
            hovered=tool('ui_search',{'repo':str(repo)})['branchPopup']
            if hovered['firstTarget']['hovered']:break
            time.sleep(.1)
        else:raise AssertionError('Native pointer did not reach first branch')
        after_hover.write_bytes(tool('ui_screenshot',{'window':'active'}))
        comparison=subprocess.run(['powershell','-NoProfile','-File',str(Path(__file__).with_name('compare_hover_region.ps1').resolve()),str(before_hover),str(after_hover),str(region['x']),str(region['y']),str(region['width']),str(region['height'])],capture_output=True,text=True)
        check('group header pixels stay unchanged when its first branch is hovered',comparison.returncode==0)
        point=wintypes.POINT(target['x'],target['y'])
        handle=user32.WindowFromPoint(point);owner=wintypes.DWORD()
        user32.GetWindowThreadProcessId(handle,ctypes.byref(owner))
        assert owner.value==gui_pid,('Refusing native click outside the owned CI GUI',owner.value,gui_pid)
        assert user32.SetCursorPos(point.x,point.y)
        user32.mouse_event(2,0,0,0,0);time.sleep(.06);user32.mouse_event(4,0,0,0,0)
        check('real mouse clicked the enabled containing-branch button',True)
    else:
        tool('ui_search',{'repo':str(repo),'branch':'refs/heads/derived/child','open':True})
    for _ in range(150):
        current=state()
        if not current['graphLoading'] and not current['searchActive'] and current['historySelectedCommits']==[feature] and current['localBranchSelection']==['refs/heads/derived/child']:break
        time.sleep(.1)
    else:raise AssertionError(current)
    check('choosing a containing branch reveals the original commit, not its newer tip',current['detailCommit']==feature and feature!=child)
    check('choosing a containing branch expands its folder and never checks out',current['localBranchTreeVisible'] and git('branch','--show-current')=='main' and git('rev-parse','HEAD')==main_head)
    (out/'containing-branch-navigation.png').write_bytes(tool('ui_screenshot'))
    tool('ui_refresh',{'repo':str(repo)});time.sleep(1)
    check('background refresh keeps the explicitly chosen containing branch',state()['localBranchSelection']==['refs/heads/derived/child'])
    for zoom in (1,2,2.5):
        tool('ui_window',{'zoom':zoom,'width':1024,'height':720,'state':'normal'})
        search(feature,'sha')
        tool('ui_search',{'repo':str(repo),'select':feature,'showBranches':True})
        for _ in range(50):
            popup=tool('ui_search',{'repo':str(repo)})['branchPopup']
            bounds=popup and popup['bounds']
            if bounds and bounds['right']>bounds['x'] and bounds['bottom']>bounds['y']:break
            time.sleep(.1)
        else:raise AssertionError(popup)
        check('coverage popup fits the screen at zoom'+str(zoom),bounds['x']>=bounds['screenX'] and bounds['y']>=bounds['screenY'] and bounds['right']<=bounds['screenRight'] and bounds['bottom']<=bounds['screenBottom'])
        time.sleep(.3)
        (out/('containing-branches-zoom'+str(zoom)+'.png')).write_bytes(tool('ui_screenshot',{'window':'active'}))
        tool('ui_search',{'repo':str(repo),'branch':'refs/remotes/origin/feature/search','open':True})
        for _ in range(150):
            current=state()
            if not current['graphLoading'] and not current['searchActive'] and current['remoteBranchSelection']==['refs/remotes/origin/feature/search'] and current['historySelectedCommits']==[feature]:break
            time.sleep(.1)
        else:raise AssertionError(current)
        check('remote containing branch navigation at zoom'+str(zoom),current['remoteBranchTreeVisible'] and git('branch','--show-current')=='main')
    tool('ui_window',{'zoom':1})
except BaseException as error:
    failure=repr(error)
    raise
finally:
    (out/'search-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe),'failure':failure},ensure_ascii=False,indent=2),'utf-8')
    p.stdin.close();p.wait(timeout=15)
    if gui_pid:
        code="$p=Get-Process -Id "+str(gui_pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'};if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell','-NoProfile','-Command',code],capture_output=True,check=True)
    assert repo.name.startswith('TreeGit search ') and not repo.is_symlink() and not repo.is_junction()
    assert repo.parent==Path(tempfile.gettempdir()).resolve()
    def remove_readonly(function,path,error):
        owned=Path(path)
        assert owned.resolve().is_relative_to(repo) and not owned.is_symlink() and not owned.is_junction()
        owned.chmod(owned.stat().st_mode | stat.S_IWRITE)
        function(path)
    shutil.rmtree(repo,onerror=remove_readonly)
    (exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','MaxHistoryCommits':20000,'EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),'utf-8')
