"""Cancel actual large binary snapshot reads by changing selection and closing tabs."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
from qa_cleanup import remove_fixture, new_fixture
assert os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_OS')=='Windows', 'Large binary tests require an owned CI runner'

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit large binary cancellation ');repo=root/'repo';repo.mkdir();snapshots=root/'snapshots';snapshots.mkdir()
env=os.environ.copy();env.update(TEMP=str(snapshots),TMP=str(snapshots),GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
def git(*args):
    result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',timeout=60,creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,result.stderr
git('init','-b','main');git('config','user.name','Binary cancellation QA');git('config','user.email','cancel@example.invalid')
size=128*1024*1024;file=repo/'large.bin'
with file.open('wb') as stream:stream.truncate(size)
(repo/'note.txt').write_text('before\n');(repo/'.gitattributes').write_text('*.bin binary\n');git('add','.');git('commit','-m','Initial large file')
with file.open('r+b') as stream:stream.seek(size-17);stream.write(b'changed')
(repo/'note.txt').write_text('after\n')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failure=None
def rpc(method,params):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    result=json.loads(messages.get(timeout=60));assert 'error' not in result,result
    return result['result']
def tool(name,args=None,allow_error=False):
    result=rpc('tools/call',{'name':name,'arguments':args or {}})
    if allow_error and result.get('isError'):return None
    assert not result.get('isError'),result
    return json.loads(result['content'][0]['text'])
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId']
    for turn in range(8):
        tool('ui_open_repository',{'repo':str(repo)});tool('ui_working_copy',{'repo':str(repo)});tool('ui_refresh',{'repo':str(repo)})
        selected=None
        for attempt in range(100):
            selected=tool('ui_diff',{'repo':str(repo),'action':'select','file':'large.bin'},True)
            if selected:break
            time.sleep(.05)
        assert selected,'Large file did not appear'
        deadline=time.monotonic()+10;observed=False;state={}
        while time.monotonic()<deadline:
            state=tool('ui_diff',{'repo':str(repo),'action':'state'})
            observed=bool(list(snapshots.glob('TreeGit-binary-*')))
            if observed and state.get('status')=='loading':break
            time.sleep(.01)
        assert observed and state.get('status')=='loading',('The intended read was not in progress',state)
        started=time.monotonic()
        if turn%2:
            tool('ui_diff',{'repo':str(repo),'action':'select','file':'note.txt'})
        else:
            tabs=tool('ui_tabs',{'action':'list'})['tabs'];tab=next(t for t in tabs if t['repo'] and Path(t['repo'])==repo)
            tool('ui_tabs',{'action':'close','index':tab['index']})
            assert not any(t['repo'] and Path(t['repo'])==repo for t in tool('ui_tabs',{'action':'list'})['tabs'])
        while list(snapshots.glob('TreeGit-binary-*')) and time.monotonic()-started<15:time.sleep(.05)
        assert not list(snapshots.glob('TreeGit-binary-*')),'Cancelled read retained snapshots'
        assert tool('ui_state')['processId']==pid,'Cancellation restarted the program'
        records.append({'turn':turn,'action':'select text' if turn%2 else 'close tab','cancelledWhileLoading':True,'cleanupSeconds':round(time.monotonic()-started,3)})
        print('PASS large binary cancellation',records[-1],flush=True)
    assert not (repo/'.git/index.lock').exists()
except BaseException:
    import traceback
    failure=traceback.format_exc();raise
finally:
    (out/'binary-cancellation-results.json').write_text(json.dumps({'exe':str(exe),'size':size,'root':str(root),'records':records,'failure':failure,'remainingSnapshots':[str(s) for s in snapshots.glob('TreeGit-binary-*')]},ensure_ascii=False,indent=2),'utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait()
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'Owned QA app did not exit normally'}};exit 0"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
    remove_fixture(root,out,'TreeGit large binary cancellation ')
