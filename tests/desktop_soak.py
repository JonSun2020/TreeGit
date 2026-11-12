"""Sustained multi-repository GUI/Git/diff cycles; only disposable repositories are written."""
import base64, json, os, queue, random, shutil, subprocess, sys, tempfile, threading, time, traceback
from pathlib import Path
from qa_cleanup import remove_fixture, new_fixture
from qa_process import process_sample, close_owned_gui
assert os.environ.get('CI')=='true' and (os.name=='posix' or os.environ.get('RUNNER_OS')=='Windows'), 'Run only on an owned CI desktop'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
seconds=float(sys.argv[3]) if len(sys.argv)>3 else 600
build=json.loads((exe.parent/'build-info.json').read_text('utf-8-sig')) if (exe.parent/'build-info.json').exists() else {}
root_parent=Path(tempfile.gettempdir()).resolve();root=new_fixture(root_parent,'TreeGit s ');env=os.environ.copy()
snapshot_root=root/'s';snapshot_root.mkdir()
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TMPDIR=str(snapshot_root),TEMP=str(snapshot_root),TMP=str(snapshot_root))
verifier_git=shutil.which('git');assert verifier_git,'A separate Git verifier is required'
verifier_retries=[]

def verify_git_process(arguments,**options):
    for attempt in range(3):
        try:
            return subprocess.run([verifier_git,*arguments],env=env,stdin=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW,**options)
        except PermissionError as error:
            if getattr(error,'winerror',None)!=5 or attempt==2:raise
            verifier_retries.append({'seconds':round(time.monotonic()-start,2),'attempt':attempt+1,'error':str(error)})
            time.sleep(.2*(attempt+1))

def git(repo,*args):
    r=verify_git_process(['-C',str(repo),*args],capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()
def make_repo(index):
    repo=root/('仓库 '+str(index));repo.mkdir();git(repo,'init','-b','main');git(repo,'config','user.name','Soak QA');git(repo,'config','user.email','soak@example.invalid')
    (repo/'note.txt').write_text('initial\n');(repo/'firmware.bin').write_bytes(bytes([(i+index)%256 for i in range(65537)]));git(repo,'add','.');git(repo,'commit','-m','Initial');git(repo,'branch','feature/base');git(repo,'remote','add','preview',str(repo));git(repo,'update-ref','refs/remotes/preview/main','HEAD');git(repo,'branch','--set-upstream-to=preview/main','main')
    if index==0:
        count=int(os.environ.get('TREEGIT_SOAK_SEED_COMMITS','0'));assert 0<=count<=2000
        tree=git(repo,'rev-parse','HEAD^{tree}');head=git(repo,'rev-parse','HEAD')
        for i in range(count):head=git(repo,'commit-tree',tree,'-p',head,'-m','Seed history '+str(i))
        if count:git(repo,'update-ref','refs/heads/main',head)
    return repo
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
replies=queue.Queue();threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start()
seq=0;pid=None;latencies=[];samples=[];snapshots=set();cycle=0;merge_cycles=0;failure=None;start=time.monotonic();rng=random.Random(704)
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}},ensure_ascii=False)+'\n');p.stdin.flush()
    result=json.loads(replies.get(timeout=60));assert 'error' not in result,result;return result['result']
def tool(name,args=None,allow_error=False):
    before=time.monotonic();result=rpc('tools/call',{'name':name,'arguments':args or {}});latencies.append(time.monotonic()-before)
    if result.get('isError'):
        if allow_error:return None
        raise AssertionError(result)
    return base64.b64decode(result['content'][0]['data']) if name=='ui_screenshot' else json.loads(result['content'][0]['text'])
def close_repositories():
    for tab in reversed(tool('ui_tabs',{'action':'list'})['tabs']):
        if tab['repo']:tool('ui_tabs',{'action':'close','index':tab['index']})
def save_result():
    ordered=sorted(latencies)
    pending=out/'desktop-soak-results.tmp'
    pending.write_text(json.dumps({'exe':str(exe),'build':build,'root':str(root),'seconds':round(time.monotonic()-start,1),'requestedDurationSeconds':seconds,'stoppedByRequest':(out/'stop-requested').exists(),'verifierGit':verifier_git,'verifierLaunchRetries':verifier_retries,'cycles':cycle,'mergeEditorCycles':merge_cycles,'requests':len(latencies),'maxRequestSeconds':max(latencies,default=0),'p95RequestSeconds':ordered[int(len(ordered)*.95)] if ordered else 0,'samples':samples,'ownedSnapshotCount':len(snapshots),'remainingSnapshots':[str(s) for s in snapshots if s.exists()],'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    pending.replace(out/'desktop-soak-results.json')
def sample(label):
    state=tool('ui_state');assert state['processId']==pid and not state['dialogs'],state['dialogs']
    process=process_sample(exe,pid)
    temporary_bytes=0
    for directory,dirs,files in os.walk(root,followlinks=False):
        dirs[:]=[d for d in dirs if not (Path(directory)/d).is_symlink() and not (Path(directory)/d).is_junction()]
        temporary_bytes+=sum((Path(directory)/f).stat().st_size for f in files if not (Path(directory)/f).is_symlink())
    samples.append({'label':label,'cycles':cycle,'seconds':round(time.monotonic()-start,1),'repositories':len(state['repositories']),'process':process,'temporaryMiB':round(temporary_bytes/1048576,2)})
    assert shutil.disk_usage(root).free>10*1024**3, 'Cloud disk reserve fell below 10 GiB'
    if os.name=='posix':
        available=next(int(l.split()[1])*1024 for l in Path('/proc/meminfo').read_text().splitlines() if l.startswith('MemAvailable:'))
        assert available>2560*1024**2, 'Cloud memory reserve is insufficient for further cycles'
    assert process['PrivateMemorySize64']<1536*1024*1024,process
    assert process['HandleCount'] < (512 if os.name=='posix' else 5000), 'Application handles grew beyond the test limit'
    assert temporary_bytes<2*1024*1024*1024, 'Owned test fixtures exceeded 2 GiB'
    save_result()
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});state=tool('ui_state');pid=state['processId'];close_repositories();sample('baseline')
    repos=[make_repo(i) for i in range(8)]
    for repo in repos:tool('ui_open_repository',{'repo':str(repo)})
    # Verify overflow with many repositories, then dispose the extra watchers/tabs.
    extras=[make_repo(i) for i in range(8,24)]
    for repo in extras:tool('ui_open_repository',{'repo':str(repo)})
    tabs=tool('ui_tabs',{'action':'dropdown'})['tabs'];assert len([t for t in tabs if t['repo']])==24
    assert tool('ui_state')['tabDropdownOpen']
    assert all(t['title']==Path(t['repo']).name for t in tabs if t['repo']), 'A deferred repository tab lost its cached title'
    tool('ui_tabs',{'action':'select','index':next(t['index'] for t in tabs if t['repo'] and Path(t['repo'])==repos[0])})
    assert not tool('ui_state')['tabDropdownOpen']
    (out/'overflow-selected.png').write_bytes(tool('ui_screenshot'))
    for tab in reversed(tool('ui_tabs',{'action':'list'})['tabs']):
        if tab['repo'] and Path(tab['repo']) in extras:tool('ui_tabs',{'action':'close','index':tab['index']})
    merge_repo=make_repo(24)
    git(merge_repo,'checkout','-b','incoming');(merge_repo/'note.txt').write_text('incoming\n');git(merge_repo,'commit','-am','Incoming')
    git(merge_repo,'checkout','main');(merge_repo/'note.txt').write_text('local\n');git(merge_repo,'commit','-am','Local')
    conflicted=verify_git_process(['-C',str(merge_repo),'merge','incoming'],capture_output=True)
    assert conflicted.returncode==1 and git(merge_repo,'ls-files','-u')
    original_conflict=(merge_repo/'note.txt').read_bytes()
    sample('eight-active-after-overflow');start=time.monotonic();deadline=start+seconds
    while time.monotonic()<deadline and not (out/'stop-requested').exists():
        repo=repos[cycle%len(repos)];tool('ui_open_repository',{'repo':str(repo)})
        if cycle%12==0:
            tool('ui_window',{'width':1024 if cycle%24==0 else 1600,'height':720 if cycle%24==0 else 1000,'zoom':[1,1.5,2][(cycle//12)%3],'theme':['Blue','Dark','Light'][(cycle//12)%3]})
        old=(repo/'firmware.bin').read_bytes();new=bytearray(old);offset=(cycle*7919+17)%len(new);new[offset]^=0x80
        (repo/'firmware.bin').write_bytes(new);note='Repository '+repo.name+' cycle '+str(cycle)+'\n';(repo/'note.txt').write_text(note,encoding='utf-8')
        tool('ui_working_copy',{'repo':str(repo)});tool('ui_refresh',{'repo':str(repo)})
        for attempt in range(60):
            selected=tool('ui_diff',{'repo':str(repo),'action':'select','file':'firmware.bin','source':'unstaged'},True)
            state=tool('ui_diff',{'repo':str(repo),'action':'state'}) if selected is not None else {}
            if state.get('status')=='ready' and state.get('differentBytes')==1 and state.get('selectedOffset')==offset:break
            time.sleep(.1)
        else:raise AssertionError(('Diff did not load',state))
        assert state['selectedOffset']==offset and state['oldSize']==len(old) and state['newSize']==len(new),state
        top=state['topOffset'];assert bytes.fromhex(state['oldHex'])==old[top:top+256] and bytes.fromhex(state['newHex'])==new[top:top+256],state
        if state.get('snapshotDirectory'):snapshots.add(Path(state['snapshotDirectory']))
        snapshots.update(snapshot_root.glob('TreeGit-binary-*'))
        for action,paths in [('stage',['note.txt','firmware.bin']),('unstage',['firmware.bin']),('stage',['firmware.bin'])]:
            assert tool('git_action',{'repo':str(repo),'action':action,'paths':paths})['isSuccess']
        assert tool('git_action',{'repo':str(repo),'action':'commit','message':'Soak cycle '+str(cycle)})['isSuccess']
        assert git(repo,'status','--porcelain')=='' and not (repo/'.git/index.lock').exists()
        assert git(repo,'show','HEAD:note.txt')==note.strip()
        actual=verify_git_process(['-C',str(repo),'show','HEAD:firmware.bin'],capture_output=True);assert actual.returncode==0 and actual.stdout==new
        if cycle%3==0:
            target=git(repo,'rev-list','--max-parents=0','HEAD') if cycle%9==0 else git(repo,'rev-parse','HEAD')
            expected_count=int(git(repo,'rev-list','--count','--all'))
            tool('ui_select_view',{'repo':str(repo),'view':'history'});tool('ui_refresh',{'repo':str(repo)})
            for attempt in range(100):
                rs=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                if not rs['graphLoading'] and rs['graphTotal']==expected_count:break
                time.sleep(.05)
            else:raise AssertionError('Refreshed commit did not enter history')
            tool('ui_select_commit',{'repo':str(repo),'revision':target})
            for attempt in range(100):
                rs=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                scope=rs.get('commitBranchHighlights')
                if not rs['detailBranchesLoading'] and scope and scope['sha']==target:break
                time.sleep(.05)
            else:raise AssertionError('Commit branch details did not settle')
            if cycle==0 and int(os.environ.get('TREEGIT_SOAK_SEED_COMMITS','0')):
                def history_bar():
                    bars=[b for b in tool('ui_layout')['scrollBars'] if b['visible'] and b['owner']=='Histories' and b['orientation']=='Vertical']
                    assert len(bars)==1 and bars[0]['maximum']>0,bars
                    return bars[0]
                def selected_row_visible():
                    layout=tool('ui_layout');bar=history_bar();scale=layout['window']['scaling'];button=layout['buttons'][0]
                    origin=button['screen']['y']-(button['bounds']['y']+button['bounds']['height']/2)*scale
                    current=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                    row=next((r for r in current['historyRows'] if r['sha']==target),None)
                    return bool(row and row['y']-9.5*scale>=origin+bar['bounds']['y']*scale-1 and row['y']+9.5*scale<=origin+(bar['bounds']['y']+bar['bounds']['height'])*scale+1)
                for attempt in range(100):
                    bar=history_bar()
                    if selected_row_visible():break
                    time.sleep(.05)
                else:raise AssertionError(('Old commit was selected outside the visible history viewport',bar))
                tool('ui_layout',{'action':'scroll','scrollbar':bar['index'],'value':0})
                tool('ui_refresh',{'repo':str(repo)})
                for attempt in range(100):
                    refreshed=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                    if not refreshed['graphLoading'] and not refreshed['detailBranchesLoading']:break
                    time.sleep(.05)
                assert history_bar()['value']<=2,'A background refresh moved the user viewport back to the selected row'
                tool('ui_select_commit',{'repo':str(repo),'revision':target})
                for attempt in range(100):
                    bar=history_bar()
                    if selected_row_visible():break
                    time.sleep(.05)
                else:raise AssertionError(('Explicitly navigating to the same old commit did not reveal its row',bar,next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)['historyRows']))
                print('PASS old commit reveal, refresh preserves viewport, repeated navigation reveals row',flush=True)
            expected=set(git(repo,'for-each-ref','--contains='+target,'--format=%(refname)','refs/heads/','refs/remotes/').splitlines())
            assert {b['reference'] for b in (scope['local'] or [])+(scope['remote'] or [])}==expected
            if rs['detailBranches']['extraCount']:
                tool('ui_command',{'repo':str(repo),'command':'commit_branches'})
                for attempt in range(100):
                    popup=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)['detailBranchPopup']
                    if popup:break
                    time.sleep(.05)
                else:raise AssertionError('Containing-branches popup did not become visible')
                assert popup['total']==len(expected)
            tool('ui_select_view',{'repo':str(repo),'view':'search'})
            head=git(repo,'rev-parse','HEAD');tool('ui_search',{'repo':str(repo),'method':'sha','query':head})
            for attempt in range(100):
                rs=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                if not rs['searchQuerying'] and rs['searchResultCount']==1:break
                time.sleep(.05)
            else:raise AssertionError('Exact commit search did not settle')
            tool('ui_search',{'repo':str(repo),'select':head,'open':True})
            for attempt in range(100):
                rs=next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
                if not rs['searchActive'] and not rs['graphLoading'] and rs['historySelectedCommits']==[head]:break
                time.sleep(.05)
            else:raise AssertionError('Search result did not return to the same history commit')
            assert rs['graphMode']==0 and rs['graphShowRemotes']
        if cycle%8==0:
            tab=next(t for t in tool('ui_tabs',{'action':'list'})['tabs'] if t['repo'] and Path(t['repo'])==repo)
            tool('ui_tabs',{'action':'close','index':tab['index']});tool('ui_open_repository',{'repo':str(repo)})
        cycle+=1
        if cycle%10==0:
            tool('ui_open_repository',{'repo':str(merge_repo)})
            for attempt in range(100):
                current=next((r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve()==merge_repo),None)
                if current and current['workingCopyStatusKnown'] and current['changes']>0:break
                time.sleep(.05)
            else:raise AssertionError('Persistent conflict repository did not load')
            tool('ui_command',{'repo':str(merge_repo),'command':'merge_conflicts','file':'note.txt'})
            for attempt in range(100):
                dialogs=tool('ui_state')['dialogs']
                if any(d['type']=='MergeConflictEditor' and d['merge']['editable'] for d in dialogs):break
                time.sleep(.05)
            else:raise AssertionError('Repeated merge editor did not open')
            args={'repo':str(merge_repo),'file':'note.txt'}
            original=tool('ui_merge_result',{**args,'action':'read'})['text']
            tool('ui_merge_result',{**args,'action':'resolve','region':0,'choice':'left'})
            tool('ui_merge_result',{**args,'action':'undo'})
            assert tool('ui_merge_result',{**args,'action':'read'})['text']==original
            tool('ui_command',{'command':'close_dialog'})
            for attempt in range(100):
                if not tool('ui_state')['dialogs']:break
                time.sleep(.05)
            else:raise AssertionError('Unchanged merge editor did not close')
            assert (merge_repo/'note.txt').read_bytes()==original_conflict and git(merge_repo,'ls-files','-u')
            merge_cycles+=1
            sample('running');print('PASS sustained cycles',cycle,'elapsed',round(time.monotonic()-start),'seconds',flush=True)
        time.sleep(rng.choice([.05,.2,.7,1.1]))
    tool('ui_window',{'zoom':1,'theme':'Blue','width':1440,'height':900});close_repositories()
    for attempt in range(100):
        if not any(s.exists() for s in snapshots):break
        time.sleep(.1)
    assert not any(s.exists() for s in snapshots),'Closed tabs retained binary snapshot files'
    sample('all-tabs-closed');print('PASS completed',cycle,'cycles',flush=True)
except BaseException:
    failure=traceback.format_exc()
    try:
        (out/'failure-ui-state.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2),encoding='utf-8')
        (out/'failure-ui.png').write_bytes(tool('ui_screenshot'))
    except Exception:pass
    raise
finally:
    save_result();p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait(timeout=10)
    if pid:close_owned_gui(exe,pid)
    remove_fixture(root,root_parent,'TreeGit s ')
    report=out/'desktop-soak-results.json';result=json.loads(report.read_text('utf-8'))
    result['fixtureRemoved']=not root.exists();result['ownedApplicationExited']=True
    report.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
