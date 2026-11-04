"""An unborn branch is an empty old side; an unreadable existing HEAD is an error."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit binary error audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))

def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()

git('init','-b','main');git('config','user.name','Binary error QA');git('config','user.email','binary@example.invalid')
payload=bytes(range(64));(repo/'firmware.bin').write_bytes(payload);git('add','.')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]

def tool(name,args=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':'tools/call','params':{'name':name,'arguments':args or {}}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    r=r['result'];assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])

def probe(revision='HEAD',old_path='firmware.bin'):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'binary_snapshot','oldRevision':revision,'oldPath':old_path,'path':'firmware.bin'}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Snapshot error probe timed out'
    r=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in r,r
    state=r['operationDetails'];records.append(state);return state

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)

try:
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':0,'method':'initialize','params':{'protocolVersion':'2025-11-25'}})+'\n');p.stdin.flush();assert 'error' not in json.loads(messages.get(timeout=45))
    tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    state=probe();check('an unborn HEAD compares an empty old side against the complete index',state['success'] and state['oldSize']==0 and state['newSize']==64 and state['differences']==64)
    git('commit','-m','First binary');sha=git('rev-parse','HEAD');tool('ui_refresh',{'repo':str(repo)});time.sleep(.5)
    state=probe();check('a valid HEAD compares the actual committed bytes',state['success'] and state['oldSize']==64 and state['differences']==0)
    state=probe(old_path='absent.bin');check('a missing path in a valid revision remains a valid empty side',state['success'] and state['oldSize']==0 and state['newSize']==64)
    state=probe('refs/heads/missing');check('an invalid named revision reports an error',not state['success'] and 'Cannot resolve' in state['error'])
    obj=repo/'.git/objects'/sha[:2]/sha[2:];backup=obj.with_name(obj.name+'.owned-backup');obj.rename(backup)
    try:state=probe()
    finally:backup.rename(obj)
    check('an unreadable existing HEAD never silently becomes an empty old side',not state['success'] and 'Cannot resolve' in state['error'])
    blob=git('rev-parse','HEAD:firmware.bin');blob_obj=repo/'.git/objects'/blob[:2]/blob[2:];blob_backup=blob_obj.with_name(blob_obj.name+'.owned-backup');blob_obj.rename(blob_backup)
    try:
        state=probe();check('an existing committed path with a missing blob reports an error',not state['success'] and 'Cannot read' in state['error'])
        state=probe(old_path='absent.bin');check('an existing index path with a missing blob reports an error',not state['success'] and 'Cannot read' in state['error'])
    finally:blob_backup.rename(blob_obj)
    state=probe();check('restoring the Git object allows an exact comparison again',state['success'] and state['oldSize']==64 and state['newSize']==64 and state['differences']==0)
    assert not failures,failures
finally:
    (out/'binary-snapshot-error-results.json').write_text(json.dumps({'records':records,'failures':failures,'root':str(root),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
