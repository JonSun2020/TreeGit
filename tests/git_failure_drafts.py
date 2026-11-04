"""Failed native GUI operations must preserve the working tree, index and commit draft."""
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
root=Path(tempfile.mkdtemp(prefix='TreeGit native failure audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args,success=True):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    if success:assert r.returncode==0,r.stderr
    return r.stdout.strip()
git('init','-b','main');git('config','user.name','Failure QA');git('config','user.email','failure@example.invalid')
file=repo/'file.txt';file.write_text('base\n',encoding='utf-8');git('add','.');git('commit','-m','Base')
file.write_text('committed\n',encoding='utf-8');git('add','.');git('commit','-m','Revert target');revert=git('rev-parse','HEAD')
git('switch','-c','feature')
targets=[]
for i in range(2):
    file.write_text('feature '+str(i)+'\n',encoding='utf-8');git('add','.');git('commit','-m','Feature '+str(i));targets.append(git('rev-parse','HEAD'))
git('switch','main');file.write_text('uncommitted work\n',encoding='utf-8')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    result=json.loads(messages.get(timeout=45));assert 'error' not in result,result
    return result['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    for operation,revisions in [('merge',[targets[-1]]),('revert',[revert]),('cherry_pick',targets),('rebase',[targets[-1]])]:
        before_head=git('rev-parse','HEAD');before_index=git('write-tree');before_bytes=file.read_bytes();draft='Do not lose my draft\n未提交的工作说明'
        reference=root/('reference-'+operation);shutil.copytree(repo,reference)
        args=['merge','--no-edit',*revisions] if operation=='merge' else ['revert','--no-edit',*revisions] if operation=='revert' else ['rebase','--no-autostash',*revisions] if operation=='rebase' else ['cherry-pick','--no-commit','-x',*revisions]
        direct=subprocess.run(['git','-C',str(reference),*args],env=env,capture_output=True)
        assert direct.returncode!=0,direct.stdout+direct.stderr
        reference_message=(reference/'.git/MERGE_MSG').exists()
        result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
        fixture.write_text(json.dumps({'action':'cancel','operation':'native_'+operation,'targets':revisions,'draft':draft}),encoding='utf-8')
        tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        deadline=time.monotonic()+30
        while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
        assert result_file.exists(),'Native failure probe timed out'
        result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
        details=result['operationDetails'];records.append({'operation':operation,'details':details})
        check(operation+' failure really reports blocked local changes','local changes' in details['log'].lower() or 'unstaged changes' in details['log'].lower())
        check(operation+' failure preserves commit draft',details['draft']==draft)
        check(operation+' failure preserves working file, index and HEAD',file.read_bytes()==before_bytes and git('write-tree')==before_index and git('rev-parse','HEAD')==before_head)
        check(operation+' failure leaves the same message-file state as Git itself',details['messageFileExists']==reference_message)
        git('cherry-pick','--abort',success=False);git('revert','--abort',success=False);git('merge','--abort',success=False)
        (repo/'.git/MERGE_MSG').unlink(missing_ok=True)
    assert not failures,failures
finally:
    (out/'git-failure-drafts-results.json').write_text(json.dumps({'records':records,'failures':failures,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
