"""Real desktop regression: sidebar navigation never changes HEAD-based filtering or graph lanes."""
import base64
import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
repo = Path(tempfile.mkdtemp(prefix='TreeGit graph 中文 ')).resolve()
git = shutil.which('git')
clock = 0
checks = []
def run(*args):
    env = os.environ.copy()
    env['GIT_AUTHOR_DATE'] = env['GIT_COMMITTER_DATE'] = (datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(minutes=clock)).isoformat()
    r = subprocess.run([git, *args], cwd=repo, env=env, capture_output=True, text=True, encoding='utf-8')
    assert r.returncode == 0, r.stderr
    return r.stdout.strip()
def commit(message, file='README.txt'):
    global clock
    clock += 1
    with (repo/file).open('a', encoding='utf-8') as f: f.write(message+'\n')
    run('add', '.'); run('commit','-m',message)
    return run('rev-parse','HEAD')
def check(name, ok):
    assert ok, name
    checks.append(name); print('PASS', name, flush=True)
run('init','-b','main');run('config','user.name','TreeGit QA');run('config','user.email','qa@example.invalid')
commit('创建项目 / Initial project')
run('checkout','-b','feature/graph');commit('调整路线图列 / Graph column', 'graph.txt');commit('路线图配色 / Graph colors','graph.txt')
run('checkout','main');commit('更新主题 / Update theme')
clock += 1
run('merge','--no-ff','feature/graph','-m','合并路线图 / Merge graph')
commit('主界面布局 / Main interface layout')
run('tag','v1.0')
run('checkout','-b','feature/auth');commit('仓库账号隔离 / Repository credentials','auth.txt');commit('身份验证弹窗 / Authentication dialog','auth.txt')
run('checkout','main')
run('branch','release/1.0');run('checkout','release/1.0');commit('发布说明 / Release notes','release.txt');run('checkout','main')
run('remote','add','origin','.')
run('update-ref','refs/remotes/origin/main',run('rev-parse','main'))
run('update-ref','refs/remotes/origin/feature/auth',run('rev-parse','feature/auth'))
(repo/'.git/sourcegit.uistates').write_text(json.dumps({'GraphBranchRef':'refs/remotes/origin/feature/auth','ShowOnlyGraphBranch':True,'ShowRemoteBranchesInHistory':False}),encoding='utf-8')
(exe.parent/'data').mkdir(exist_ok=True)
(exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False,'UseCompactBranchNamesInGraph':True}),'utf-8')
p = subprocess.Popen([str(exe),'--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
seq=0
gui_pid=None
def rpc(method, params={}):
    global seq
    seq+=1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params}, ensure_ascii=False)+'\n');p.stdin.flush()
    r=json.loads(p.stdout.readline());assert 'error' not in r,r
    return r['result']
def tool(name,args={}):
    r=rpc('tools/call',{'name':name,'arguments':args});assert not r.get('isError'),r
    if name=='ui_screenshot':return base64.b64decode(r['content'][0]['data'])
    return json.loads(r['content'][0]['text'])
def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve()==repo)
def wait(expected):
    for _ in range(100):
        s=state()
        if not s['graphLoading'] and {c['sha'] for c in s['graphCommits']}==expected:return s
        time.sleep(.1)
    raise AssertionError(s)
def wait_refs(present=(),absent=()):
    for _ in range(100):
        current=state()
        names={name for commit in current['graphCommits'] for name in commit['refs']}
        if not current['graphLoading'] and set(present).issubset(names) and not set(absent).intersection(names):return current
        time.sleep(.1)
    raise AssertionError(current)

def wait_selected(sha):
    for _ in range(100):
        current=state()
        if not current['graphLoading'] and current['selectedCommits']==[sha]:return current
        time.sleep(.1)
    raise AssertionError(current)

def topology(current):
    return [(c['sha'],c['x'],c['subjectLeft'],c['refs']) for c in current['graphCommits']]

def wait_branch(name):
    for _ in range(100):
        if state()['branch']==name:return
        time.sleep(.1)
    raise AssertionError(state())

failure=None
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    tool('ui_open_repository',{'repo':str(repo)});tool('ui_window',{'width':1440,'height':900,'theme':'Light'})
    gui_pid=tool('ui_state')['processId']
    allsha=set(run('rev-list','--all').splitlines())
    ancestry=set(run('rev-list','HEAD').splitlines())
    main_head=run('rev-parse','main')
    wait_branch('main');initial=wait(ancestry)
    check('legacy saved focus cannot change current-branch history',initial['graphBranch']=='main' and initial['graphMode']==1)
    check('current mode retains merged ancestry',run('rev-parse','feature/graph') in ancestry)
    tool('ui_select_commit',{'repo':str(repo),'revision':'main'});baseline=wait_selected(main_head)
    spine=set(run('rev-list','--first-parent','HEAD').splitlines())
    check('checked-out branch first-parent line starts at the left',all(c['x']==6 for c in baseline['graphCommits'] if c['sha'] in spine))
    for branch in ['feature/auth','feature/graph','release/1.0','origin/main','origin/feature/auth']:
        tool('ui_graph',{'repo':str(repo),'branch':branch});time.sleep(.15)
        current=wait(ancestry)
        check('current mode ignores '+branch,topology(current)==topology(baseline) and current['selectedCommits']==baseline['selectedCommits'] and current['view']==baseline['view'] and current['graphMode']==1 and current['graphBranch']=='main')
        check(branch+' click never checks out a branch',run('branch','--show-current')=='main')
    (out/'current-after-other-branch-clicks.png').write_bytes(tool('ui_screenshot'))
    (out/'only-main.png').write_bytes(tool('ui_screenshot'))
    tool('ui_graph',{'repo':str(repo),'mode':'all','showRemotes':True});all_view=wait(allsha)
    for branch in ['feature/auth','feature/graph','release/1.0','origin/feature/auth','origin/main','main']:
        tool('ui_graph',{'repo':str(repo),'branch':branch});current=wait_selected(run('rev-parse',branch))
        selection='remoteBranchSelection' if branch.startswith('origin/') else 'localBranchSelection'
        reference='refs/remotes/'+branch if branch.startswith('origin/') else 'refs/heads/'+branch
        check('real sidebar selection reaches '+branch,reference in (current[selection] or []))
        check('all mode navigates to '+branch+' without relaying out the graph',topology(current)==topology(all_view) and current['graphBranch']=='main')
        check(branch+' navigation leaves checked-out spine on the left',all(c['x']==6 for c in current['graphCommits'] if c['sha'] in spine) and run('branch','--show-current')=='main')
    tool('ui_graph',{'repo':str(repo),'branch':'origin/feature/auth'})
    tool('ui_select_commit',{'repo':str(repo),'revision':'main'});wait_selected(main_head)
    tool('ui_graph',{'repo':str(repo),'branch':'origin/feature/auth'});wait_selected(run('rev-parse','origin/feature/auth'))
    check('clicking an already selected branch still navigates to its tip',topology(state())==topology(all_view))
    (out/'all-after-remote-branch-click.png').write_bytes(tool('ui_screenshot'))
    tool('ui_graph',{'repo':str(repo),'mode':'merged'});current=wait(ancestry)
    check('legacy merged mode still means current checked-out branch',current['graphBranch']=='main' and current['graphMode']==1)
    tool('git_action',{'repo':str(repo),'action':'checkout','revision':'feature/auth'});wait_branch('feature/auth')
    feature_ancestry=set(run('rev-list','HEAD').splitlines());current=wait(feature_ancestry)
    feature_spine=set(run('rev-list','--first-parent','HEAD').splitlines())
    check('actual checkout changes the current history and its leftmost spine',current['graphBranch']=='feature/auth' and all(c['x']==6 for c in current['graphCommits'] if c['sha'] in feature_spine))
    tool('ui_select_commit',{'repo':str(repo),'revision':'HEAD'});baseline=wait_selected(run('rev-parse','HEAD'))
    tool('ui_graph',{'repo':str(repo),'branch':'main'});time.sleep(.15)
    check('after checkout, clicking the former current branch cannot change the view',topology(state())==topology(baseline) and state()['selectedCommits']==baseline['selectedCommits'])
    tool('ui_graph',{'repo':str(repo),'mode':'all'});all_feature=wait(allsha)
    tool('ui_graph',{'repo':str(repo),'branch':'main'});current=wait_selected(main_head)
    check('all mode remains pinned to the newly checked-out branch after navigation',topology(current)==topology(all_feature) and all(c['x']==6 for c in current['graphCommits'] if c['sha'] in feature_spine))
    tool('git_action',{'repo':str(repo),'action':'checkout','revision':'main'});wait_branch('main')
    tool('ui_graph',{'repo':str(repo),'mode':'current'});baseline=wait(ancestry)
    run('branch','-D','feature/auth');tool('ui_refresh',{'repo':str(repo)});current=wait(ancestry)
    check('deleting another branch leaves current history and spine unchanged',topology(current)==topology(baseline) and current['graphBranch']=='main')
    run('config','branch.main.remote','origin');run('config','branch.main.merge','refs/heads/main')
    run('update-ref','refs/remotes/origin/main',run('rev-parse','release/1.0'))
    tool('ui_refresh',{'repo':str(repo)})
    upstream_history=set(run('rev-list','HEAD','origin/main').splitlines());baseline=wait(upstream_history)
    check('current mode still includes incoming upstream history',any('origin/main' in c['refs'] for c in baseline['graphCommits']))
    tool('ui_select_commit',{'repo':str(repo),'revision':'main'});baseline=wait_selected(main_head)
    tool('ui_graph',{'repo':str(repo),'branch':'origin/feature/auth'});time.sleep(.15)
    check('clicking a different remote cannot replace the current upstream history',topology(state())==topology(baseline) and state()['selectedCommits']==baseline['selectedCommits'])
    tool('ui_graph',{'repo':str(repo),'showRemotes':False});current=wait(ancestry)
    check('hiding remote branches retains only the checked-out merged history',all(not any(r.startswith('origin/') for r in c['refs']) for c in current['graphCommits']))
    run('update-ref','refs/remotes/origin/main',main_head)
    tool('ui_graph',{'repo':str(repo),'mode':'all','showRemotes':True});tool('ui_refresh',{'repo':str(repo)});wait(allsha)
    tool('ui_select_commit',{'repo':str(repo),'revision':'main'});time.sleep(.8)
    (out/'main-interface.png').write_bytes(tool('ui_screenshot'))
    check('all mode restores all branch histories',state()['graphTotal']==len(allsha))
    tool('ui_window',{'width':1024,'height':720});time.sleep(.4)
    (out/'main-1024.png').write_bytes(tool('ui_screenshot'))
    tool('ui_window',{'width':1440,'height':900,'theme':'Dark'});time.sleep(.4)
    (out/'main-dark.png').write_bytes(tool('ui_screenshot'))
    run('config','branch.main.remote','origin');run('config','branch.main.merge','refs/heads/main')
    run('tag','graph-tag','feature/graph');run('tag','remote-tag','refs/remotes/origin/feature/auth')
    tool('ui_refresh',{'repo':str(repo)});wait(allsha);wait_refs(present=['graph-tag','remote-tag','v1.0','origin/main'])
    tool('ui_window',{'state':'maximized','zoom':1})
    for theme in ['Light','Dark','Blue']:
        tool('ui_window',{'theme':theme});time.sleep(.4)
        (out/('references-'+theme.lower()+'.png')).write_bytes(tool('ui_screenshot'))
    check('graph preview includes tags on distinct branch lines',{'graph-tag','remote-tag','v1.0'}.issubset({name for c in state()['graphCommits'] for name in c['refs']}))
    run('remote','add','upstream','.');run('update-ref','refs/remotes/upstream/main',run('rev-parse','main'))
    tool('ui_refresh',{'repo':str(repo)});wait(allsha);time.sleep(.4)
    wait_refs(present=['origin/main','upstream/main'])
    (out/'references-multiple-remotes.png').write_bytes(tool('ui_screenshot'))
    check('current head keeps both compact remote names',{'origin/main','upstream/main'}.issubset(set(next(c for c in state()['graphCommits'] if c['sha']==run('rev-parse','main'))['refs'])))
    run('update-ref','-d','refs/remotes/origin/main');run('update-ref','-d','refs/remotes/upstream/main')
    tool('ui_refresh',{'repo':str(repo)});wait(allsha);time.sleep(.4)
    wait_refs(absent=['origin/main','upstream/main'])
    (out/'references-local-only.png').write_bytes(tool('ui_screenshot'))
    check('local head remains available without matching remote decorators','main' in next(c for c in state()['graphCommits'] if c['sha']==run('rev-parse','main'))['refs'])
    tool('ui_window',{'theme':'Light'})
    tool('ui_select_view',{'repo':str(repo),'view':'working_copy'})
    (repo/'README.txt').write_text('Working copy change\n',encoding='utf-8')
    tool('ui_refresh',{'repo':str(repo)});time.sleep(1)
    (out/'working-copy.png').write_bytes(tool('ui_screenshot'))
    tool('ui_select_view',{'repo':str(repo),'view':'history'})
except BaseException as error:
    failure=repr(error)
    raise
finally:
    (out/'graph-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'failure':failure},indent=2,ensure_ascii=False),encoding='utf-8')
    p.stdin.close();p.wait(timeout=15)
    if gui_pid:
        code="$p=Get-Process -Id "+str(gui_pid)+" -ErrorAction SilentlyContinue; if($p){if($p.Path -ne '"+str(exe).replace("'","''")+"'){throw 'Wrong QA process'}; if(-not $p.CloseMainWindow()){throw 'Owned QA window did not close'};if(-not $p.WaitForExit(15000)){throw 'Owned QA window did not exit'}}"
        subprocess.run(['powershell','-NoProfile','-Command',code],capture_output=True,check=True)
