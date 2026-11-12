"""Real mouse modifiers/context menus and editable merge recovery on an owned CI desktop."""
import base64, json, os, queue, shutil, subprocess, sys, tempfile, threading, time, traceback
from pathlib import Path
from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui

assert os.name=='posix' and os.environ.get('CI')=='true'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
parent=Path(tempfile.gettempdir()).resolve();root=new_fixture(parent,'TreeGit i ');repo=root/'repo';repo.mkdir()
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
checks=[];pid=None;p=None;failure=None
def check(name,value):
    assert value,name
    checks.append(name);print('PASS',name,flush=True)
def git(*args,ok=True):
    r=subprocess.run([shutil.which('git'),'-C',str(repo),*args],env=env,capture_output=True,timeout=30)
    if ok:assert r.returncode==0,r.stderr
    return r.stdout.decode().strip() if ok else r
def until(fn):
    end=time.monotonic()+15
    while time.monotonic()<end:
        v=fn()
        if v:return v
        time.sleep(.05)
    raise AssertionError('The requested UI state did not settle')
responses=queue.Queue();seq=0
def rpc(method,args):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':args})+'\n');p.stdin.flush()
    result=json.loads(responses.get(timeout=45));assert 'error' not in result,result;return result['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return base64.b64decode(r['content'][0]['data']) if name=='ui_screenshot' else json.loads(r['content'][0]['text'])
def state():return next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
def mouse(sha,button=1,modifier=None,refresh_held=False):
    assert Path(f'/proc/{pid}/exe').resolve()==exe
    row=until(lambda:next((r for r in state()['historyRows'] if r['sha']==sha),None))
    time.sleep(.15)
    row=until(lambda:next((r for r in state()['historyRows'] if r['sha']==sha),None))
    subprocess.run(['xdotool','mousemove','--sync',str(row['x']),str(row['y']),'sleep','.2'],check=True)
    if modifier:subprocess.run(['xdotool','keydown',modifier],check=True)
    subprocess.run(['xdotool','mousedown',str(button)],check=True)
    try:
        if refresh_held:
            until(lambda:selected([sha]))
            tool('ui_refresh',{'repo':str(repo)})
        time.sleep(.12)
    finally:
        subprocess.run(['xdotool','mouseup',str(button)],check=True)
        if modifier:subprocess.run(['xdotool','keyup',modifier],check=True)
        time.sleep(.55)  # These are separate clicks, not a double-click gesture.
def escape():subprocess.run(['xdotool','key','Escape'],check=True)
def selected(wanted):return set(state()['historySelectedCommits'])==set(wanted)
def merge_action(action,**args):return tool('ui_merge_result',{'repo':str(repo),'file':'config.txt','action':action,**args})
def open_editor():
    tool('ui_refresh',{'repo':str(repo)})
    until(lambda:state()['workingCopyStatusKnown'] and state()['changes']>0)
    tool('ui_command',{'repo':str(repo),'command':'merge_conflicts','file':'config.txt'})
    until(lambda:any(d['type']=='MergeConflictEditor' and d['merge']['editable'] for d in tool('ui_state')['dialogs']))
try:
    git('init','-b','main');git('config','user.name','Interaction QA');git('config','user.email','interaction@example.invalid')
    (repo/'config.txt').write_text('rate=10\n');git('add','.');git('commit','-m','Base');base=git('rev-parse','HEAD')
    for i in range(4):
        (repo/'note.txt').write_text(str(i)+'\n');git('add','.');git('commit','-m','History '+str(i))
    head=git('rev-parse','HEAD');commits=git('rev-list','HEAD').splitlines();middle=commits[2]
    p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=(out/'stderr.log').open('w'),text=True,encoding='utf-8',env=env)
    threading.Thread(target=lambda:[responses.put(s) for s in p.stdout],daemon=True).start()
    rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId']
    tool('ui_open_repository',{'repo':str(repo)});tool('ui_select_view',{'repo':str(repo),'view':'history'})
    tool('ui_graph',{'repo':str(repo),'mode':'all','showRemotes':True});tool('ui_window',{'width':1280,'height':800,'zoom':1})
    until(lambda:not state()['graphLoading'] and len(state()['historyRows'])==5)
    owned=[line.split()[0] for line in subprocess.check_output(['wmctrl','-lp'],text=True).splitlines() if int(line.split()[2])==pid]
    assert len(owned)==1
    subprocess.run(['wmctrl','-i','-a',owned[0]],check=True)
    until(lambda:int(subprocess.check_output(['xdotool','getactivewindow','getwindowpid'],text=True).strip())==pid)
    before=head,git('ls-files','--stage'),git('status','--porcelain')
    for turn in range(5):
        mouse(base,refresh_held=True);until(lambda:selected([base]) and not state()['graphLoading'])
        check('held mouse selection survives row refresh '+str(turn),state()['detailCommit']==base)
        mouse(head);until(lambda:selected([head]));mouse(base,modifier='Control_L');until(lambda:selected([base,head]))
        check('Ctrl-click preserves both commits '+str(turn),selected([base,head]))
        mouse(base);until(lambda:selected([base]));mouse(head,modifier='Shift_L');until(lambda:selected(commits))
        check('Shift-click selects the complete contiguous range '+str(turn),selected(commits))
        mouse(middle,button=3);until(lambda:state()['commitMenu'])
        check('right-click keeps an existing multiple selection '+str(turn),selected(commits));escape();until(lambda:not state()['commitMenu'])
        mouse(head);until(lambda:selected([head]));mouse(middle,button=3);until(lambda:state()['commitMenu'] and selected([middle]))
        check('right-click on another row targets that commit '+str(turn),state()['detailCommit']==middle);escape();until(lambda:not state()['commitMenu'])
    check('native selection and context menus never write Git',before==(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')))
    git('switch','-c','incoming',base);(repo/'config.txt').write_text('rate=30\n');git('commit','-am','Incoming');incoming=git('rev-parse','HEAD')
    git('switch','main');(repo/'config.txt').write_text('rate=20\n');git('commit','-am','Local');local=git('rev-parse','HEAD')
    assert git('merge','incoming',ok=False).returncode==1
    original=(repo/'config.txt').read_bytes();index=git('ls-files','-u');open_editor()
    def merge_parents():
        row=state()['pendingRow'];values=row.get('parents') if row else None
        return values if values and len(values)==2 else None
    parents=until(merge_parents)
    check('pending merge graph contains both actual parents',{p['sha'] for p in parents}=={local,incoming})
    initial=merge_action('read');check('editable conflict starts unresolved',initial['unsolved']==1)
    merge_action('resolve',region=0,choice='left');left=merge_action('read')
    check('Local resolution uses local text',left['text']=='rate=20\n' and left['unsolved']==0)
    merge_action('undo');check('undo restores the exact conflict',merge_action('read')['text']==initial['text'])
    merge_action('redo');check('redo restores the resolution',merge_action('read')['text']==left['text'])
    merge_action('resolve',region=0,choice='restore');check('restore reopens the original conflict',merge_action('read')['unsolved']==1)
    manual='rate=25\nreviewed=local+remote\n';merge_action('replace',text=manual)
    (repo/'config.txt').write_text('external editor change\n')
    refused=merge_action('save');check('save preserves a concurrent external edit',not refused['saved'] and (repo/'config.txt').read_text()=='external editor change\n' and git('ls-files','-u')==index)
    check('failed save keeps the edited result available',merge_action('read')['text']==manual)
    (repo/'config.txt').write_bytes(original)
    owned_lock=repo/'.git/index.lock'
    owned_lock.write_text('TreeGit owned staging failure fixture\n')
    try:
        failed_stage=merge_action('save')
        failed_state=merge_action('read')
        check('an index lock keeps the merge editor open with a clear save error',not failed_stage['saved'] and bool(failed_state['error']) and any(d['type']=='MergeConflictEditor' for d in tool('ui_state')['dialogs']))
        check('failed staging preserves the saved result and the unresolved index',(repo/'config.txt').read_text()==manual and git('ls-files','-u')==index and merge_action('read')['text']==manual)
    finally:
        assert owned_lock.read_text()=='TreeGit owned staging failure fixture\n'
        owned_lock.unlink()
    assert merge_action('save')['saved']
    until(lambda:not any(d['type']=='MergeConflictEditor' for d in tool('ui_state')['dialogs']))
    check('save stages the exact result and closes its window',git('show',':config.txt')==manual.strip() and not git('ls-files','-u'))
    check('saving a conflict does not silently commit',git('rev-parse','HEAD')==local and (repo/'.git/MERGE_HEAD').exists())
    assert tool('git_action',{'repo':str(repo),'action':'merge_continue'})['isSuccess']
    check('explicit continuation makes a clean two-parent merge',len(git('rev-list','--parents','-n','1','HEAD').split())==3 and not git('status','--porcelain'))
    (out/'final.png').write_bytes(tool('ui_screenshot'))
except BaseException:
    failure=traceback.format_exc()
    try:(out/'failure.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2));(out/'failure.png').write_bytes(tool('ui_screenshot'))
    except Exception:pass
    raise
finally:
    subprocess.run(['xdotool','keyup','Control_L','Shift_L','Alt_L'],capture_output=True)
    if p:
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait(timeout=10)
    if pid:close_owned_gui(exe,pid)
    remove_fixture(root,parent,'TreeGit i ')
    (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'fixtureRemoved':not root.exists()},ensure_ascii=False,indent=2),encoding='utf-8')
