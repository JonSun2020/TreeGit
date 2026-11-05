"""A real refused CreateProcess must leave a useful binary view and a working retry."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
assert (exe.parent/'data').is_dir(),'Use an isolated portable application'
root=Path(tempfile.mkdtemp(prefix='TreeGit binary launch recovery ')).resolve()
repo=root/'repo';repo.mkdir();snapshots=root/'snapshots';snapshots.mkdir();fixture=root/'fixture.json'
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1',
     'TREEGIT_TEST_SETTINGS_FIXTURE':str(fixture),'TEMP':str(snapshots),'TMP':str(snapshots)}
def git(*args):
    result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,result.stderr
    return result.stdout.strip()
git('init','-b','main');git('config','user.name','Binary recovery QA');git('config','user.email','binary-recovery@example.invalid')
old=bytes(range(256))*4;new=bytearray(old);new[17]^=0x33
(repo/'firmware.bin').write_bytes(old);git('add','.');git('commit','-m','Original binary')
head=git('rev-parse','HEAD');index=git('write-tree');(repo/'firmware.bin').write_bytes(new)
fixture.write_text(json.dumps({'action':'cancel','operation':'native_binary_launch_recovery','paths':['firmware.bin']}),'utf-8')
p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                   text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start()
seq=0;pid=None;record={}
def rpc(method,params):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    result=json.loads(messages.get(timeout=45));assert 'error' not in result,result
    return result['result']
def tool(name,args=None):
    result=rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result
    return json.loads(result['content'][0]['text'])
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId']
    tool('ui_open_repository',{'repo':str(repo)});tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    result_file=Path(str(fixture)+'.result');deadline=time.monotonic()+35
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.03)
    assert result_file.exists(),'Binary launch recovery did not finish'
    record=json.loads(result_file.read_text('utf-8'));assert 'exception' not in record,record
    data=record['operationDetails'];before=data['before'];after=data['after']
    assert before['status']=='error' and before['oldHex'] is None and before['newHex'] is None,before
    assert '重试' in before['summary'] and 'An error occurred' not in before['summary'] and str(root) not in before['summary'],before
    assert data['failures']>=3 and data['retryHiddenAfterRecovery'],data
    assert after['status']=='ready' and after['differentBytes']==1 and after['selectedOffset']==17,after
    offset=after['topOffset'];assert after['oldHex']==old[offset:offset+256].hex().upper() and after['newHex']==bytes(new[offset:offset+256]).hex().upper(),after
    assert git('rev-parse','HEAD')==head and git('write-tree')==index and (repo/'firmware.bin').read_bytes()==bytes(new)
    record['passed']=True
    print('PASS real refused Git start, clear binary error, same-view button retry, exact bytes and untouched repository',flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait()
    if pid:
        literal=str(exe).replace("'","''")
        script=("$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){"
                "if($p.Path -ne '"+literal+"'){throw 'Unexpected owned app'};$null=$p.CloseMainWindow();"
                "if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'Owned GUI did not exit'}};exit 0")
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
    record['remainingSnapshots']=[str(d) for d in snapshots.glob('TreeGit-binary-*')]
    (out/'binary-launch-results.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),'utf-8')
    assert not record['remainingSnapshots'],record['remainingSnapshots']
