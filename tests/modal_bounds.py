"""Expand and validate actual nested dialogs at each zoom; inspect buttons and screen bounds."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit nested dialog audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr
git('init','-b','main');git('config','user.name','Modal QA');git('config','user.email','modal@example.invalid')
git('remote','add','origin','https://example.invalid/owned-audit.git')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    for zoom in [1,1.5,2]:
        tool('ui_window',{'width':1280,'height':800,'zoom':zoom,'theme':'Blue'})
        for dialog in ['remote','template','credentials']:
            result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
            fixture.write_text(json.dumps({'action':'cancel','operation':'dialog_'+dialog}),encoding='utf-8')
            tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
            deadline=time.monotonic()+30
            while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
            assert result_file.exists(),'Dialog probe timed out'
            result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
            details=result['operationDetails'];bounds=details['bounds'] if dialog=='credentials' else details
            records.append({'dialog':dialog,'zoom':zoom,'result':details})
            shutil.copy2(str(fixture)+'.dialog.png',out/f'{dialog}-{zoom}.png')
            check(f'{dialog} {zoom} stays on screen',bounds['withinScreen'])
            check(f'{dialog} {zoom} keeps all footer actions visible',len(bounds['buttons'])>=2 and all(b['visible'] for b in bounds['buttons']))
            if dialog=='credentials':
                check(f'credentials {zoom} remains topmost',bounds['topmost'])
                check(f'credentials {zoom} invalid input preserves the authentication mode',details['invalidInputPreservedConfig'])
    tool('ui_window',{'zoom':1})
    for scenario in ['unchanged','empty_password','explicit_clear','oversized','config_locked','commit_failure']:
        result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
        fixture.write_text(json.dumps({'action':'cancel','operation':'credential_save','case':scenario}),encoding='utf-8')
        tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        deadline=time.monotonic()+30
        while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
        assert result_file.exists(),'Credential probe timed out'
        result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
        details=result['operationDetails'];records.append({'scenario':scenario,'result':details})
        if scenario=='unchanged':check('confirming an existing account preserves its unedited password',details['success'] and details['originalCredentialPreserved'] and not details['systemMode'])
        elif scenario=='empty_password':check('a different account can explicitly use an empty password',details['success'] and details['emptyPasswordSaved'] and details['newUsernameSaved'] and not details['systemMode'])
        elif scenario=='explicit_clear':check('typing and clearing a password saves an intentional empty password',details['success'] and details['emptyPasswordSaved'] and not details['systemMode'])
        else:check(scenario+' leaves both original credentials and authentication mode unchanged',not details['success'] and details['configurationUnchanged'] and details['originalCredentialPreserved'] and details['systemMode'])
    assert not failures,failures
finally:
    (out/'modal-bounds-results.json').write_text(json.dumps({'records':records,'failures':failures,'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid)],capture_output=True)
