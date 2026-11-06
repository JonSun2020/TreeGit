"""Search-page navigation, all original search methods, and off-graph details."""
import base64
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

def open_history(label,sha,local=None,remote=None):
    tool('ui_graph',{'repo':str(repo),'mode':'current','showRemotes':False})
    for _ in range(100):
        current=state()
        if not current['graphLoading'] and current['graphMode']==1 and not current['graphShowRemotes']:break
        time.sleep(.1)
    search(sha,'sha');tool('ui_search',{'repo':str(repo),'select':sha})
    current=state()
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
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    tool('ui_select_view', {'repo':str(repo), 'view':'working_copy'})
    check('full commit page also exits search', not state()['searchActive'])
    tool('ui_select_view', {'repo':str(repo), 'view':'search'})
    tool('ui_window', {'width':1024, 'height':720})
    (out/'search-narrow.png').write_bytes(tool('ui_screenshot'))
    tool('ui_select_view', {'repo':str(repo), 'view':'history'})
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
