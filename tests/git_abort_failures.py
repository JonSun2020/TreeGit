"""A failed native abort must retain the operation's recovery files, data and draft."""
import hashlib
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
root=Path(tempfile.mkdtemp(prefix='TreeGit abort failure audit '));fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(repo,*args,success=True):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    if success:assert r.returncode==0,r.stderr
    return r
def states(repo):
    paths=['MERGE_HEAD','MERGE_MSG','CHERRY_PICK_HEAD','REVERT_HEAD','ORIG_HEAD','rebase-merge','rebase-apply','sequencer']
    result={}
    for name in paths:
        path=repo/'.git'/name
        files=path.rglob('*') if path.is_dir() else [path]
        for file in files:
            if file.is_file():result[file.relative_to(repo/'.git').as_posix()]=hashlib.sha256(file.read_bytes()).hexdigest()
    return result
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
def probe(repo,draft,locked):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True);fixture.write_text(json.dumps({'action':'cancel','operation':'native_abort','draft':draft,'lock':locked,'disableAutoRefresh':'--manual-refresh' in sys.argv[3:]}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Native abort timed out'
    result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
    return result['operationDetails']
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    for operation in ['merge','cherry-pick','revert','rebase','rebase-apply','am']:
        repo=root/operation;repo.mkdir();file=repo/'file.txt'
        git(repo,'init','-b','main');git(repo,'config','user.name','Abort QA');git(repo,'config','user.email','abort@example.invalid')
        file.write_text('base\n');git(repo,'add','.');git(repo,'commit','-m','Base')
        file.write_text('one\n');git(repo,'add','.');git(repo,'commit','-m','One');one=git(repo,'rev-parse','HEAD').stdout.strip()
        git(repo,'switch','-c','feature');file.write_text('feature\n');git(repo,'add','.');git(repo,'commit','-m','Feature');feature=git(repo,'rev-parse','HEAD').stdout.strip()
        git(repo,'switch','main');file.write_text('main two\n');git(repo,'add','.');git(repo,'commit','-m','Main two')
        args=['merge','--no-edit','feature'] if operation=='merge' else ['cherry-pick',feature] if operation=='cherry-pick' else ['revert','--no-edit',one] if operation=='revert' else ['rebase','feature']
        if operation=='rebase-apply':args=['rebase','--apply','feature']
        if operation=='am':
            patch=root/'conflict.patch';patch.write_text(git(repo,'format-patch','--stdout',one+'..feature').stdout,encoding='utf-8');args=['am','-3',str(patch)]
        command='am' if operation=='am' else 'rebase' if operation=='rebase-apply' else operation
        original_head=git(repo,'rev-parse','HEAD').stdout
        conflicted=git(repo,*args,success=False);assert conflicted.returncode!=0,conflicted.stdout
        reference=root/('reference-'+operation);shutil.copytree(repo,reference)
        (reference/'.git/index.lock').write_text('owned test lock')
        direct=git(reference,command,'--abort',success=False);assert direct.returncode!=0,direct.stdout+direct.stderr
        reference_state=states(reference)
        before_head=git(repo,'rev-parse','HEAD').stdout;before_index=(repo/'.git/index').read_bytes();before_file=file.read_bytes();draft='Abort failure must preserve this draft'
        tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
        details=probe(repo,draft,True);records.append({'operation':operation,'details':details,'referenceState':reference_state,'actualState':states(repo)})
        check(operation+' abort failure was caused by the owned index lock','index.lock' in details['log'])
        check(operation+' abort failure preserves draft and releases busy state',details['draft']==draft and not details['busy'])
        check(operation+' abort failure preserves the same recovery files as Git itself',states(repo)==reference_state)
        check(operation+' abort failure preserves HEAD, index and working file',git(repo,'rev-parse','HEAD').stdout==before_head and (repo/'.git/index').read_bytes()==before_index and file.read_bytes()==before_file)
        if states(repo)==reference_state:
            retried=probe(repo,draft,False)
            check(operation+' abort succeeds after the lock is removed',not retried['busy'] and retried['context'] is None and git(repo,'rev-parse','HEAD').stdout==original_head and file.read_text()=='main two\n' and not any((repo/'.git'/name).exists() for name in ['MERGE_HEAD','CHERRY_PICK_HEAD','REVERT_HEAD','rebase-merge','rebase-apply','sequencer']))
    assert not failures,failures
finally:
    (out/'git-abort-failure-results.json').write_text(json.dumps({'records':records,'failures':failures,'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
