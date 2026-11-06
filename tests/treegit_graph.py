"""Real desktop graph regression: branch pinning, current-branch filtering and merged ancestry."""
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
run('update-ref','refs/remotes/origin/main',run('rev-parse','main'))
run('update-ref','refs/remotes/origin/feature/auth',run('rev-parse','feature/auth'))
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

failure=None
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    tool('ui_open_repository',{'repo':str(repo)});tool('ui_window',{'width':1440,'height':900,'theme':'Light'})
    gui_pid=tool('ui_state')['processId']
    allsha=set(run('rev-list','--all').splitlines())
    wait(allsha)
    for _ in range(100):
        if state()['branch']=='main': break
        time.sleep(.1)
    else: raise AssertionError('Repository branch refresh did not complete')
    for branch in ['main','feature/auth','release/1.0','origin/feature/auth']:
        tool('ui_graph',{'repo':str(repo),'branch':branch,'mode':'all'})
        s=wait(allsha)
        spine=set(run('rev-list','--first-parent',branch).splitlines())
        check(branch+' first-parent commits occupy leftmost lane',all(c['x']==6 for c in s['graphCommits'] if c['sha'] in spine))
        check(branch+' subjects have a fixed aligned graph area',len({c['subjectLeft'] for c in s['graphCommits']})==1)
        tool('ui_graph',{'repo':str(repo),'mode':'current'})
        ancestry=set(run('rev-list',branch).splitlines())
        s=wait(ancestry)
        check(branch+' current mode hides unrelated commits',s['graphTotal']==len(ancestry))
        check(branch+' current mode keeps the first-parent line at the left',all(c['x']==6 for c in s['graphCommits'] if c['sha'] in spine))
        check(branch+' current mode hides other branch labels',all(all(ref in [branch,'v1.0'] for ref in c['refs']) for c in s['graphCommits']))
        if branch=='main':
            tool('ui_select_commit',{'repo':str(repo),'revision':'main'});time.sleep(.6)
            (out/'only-main.png').write_bytes(tool('ui_screenshot'))
        tool('ui_graph',{'repo':str(repo),'mode':'merged'})
        s=wait(set(run('rev-list',branch).splitlines()))
        check(branch+' legacy mode uses the same current-branch history',s['graphTotal']==len(run('rev-list',branch).splitlines()))
        check(branch+' filtering never checks out the branch',run('branch','--show-current')=='main')
    tool('ui_graph',{'repo':str(repo),'branch':'feature/auth','mode':'current'})
    wait(set(run('rev-list','feature/auth').splitlines()))
    run('branch','-D','feature/auth')
    tool('ui_refresh',{'repo':str(repo)})
    s=wait(set(run('rev-list','main').splitlines()))
    check('deleting the selected branch falls back to HEAD history',s['graphBranch']=='main')
    check('fallback keeps the current branch pinned to lane zero',all(c['x']==6 for c in s['graphCommits'] if c['sha'] in set(run('rev-list','--first-parent','main').splitlines())))
    tool('ui_graph',{'repo':str(repo),'branch':'HEAD','mode':'all'});wait(allsha)
    tool('ui_select_commit',{'repo':str(repo),'revision':'main'});time.sleep(.8)
    (out/'main-interface.png').write_bytes(tool('ui_screenshot'))
    check('all mode restores all branch histories',state()['graphTotal']==len(allsha))
    tool('ui_window',{'width':1024,'height':720});time.sleep(.4)
    (out/'main-1024.png').write_bytes(tool('ui_screenshot'))
    tool('ui_window',{'width':1440,'height':900,'theme':'Dark'});time.sleep(.4)
    (out/'main-dark.png').write_bytes(tool('ui_screenshot'))
    run('remote','add','origin','.')
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
