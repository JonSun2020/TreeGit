"""Native continue must commit the edited message and follow each new rebase conflict."""
import json
import os
from pathlib import Path
import queue
import stat
import subprocess
import sys
import tempfile
import threading
import time

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
backend=next((a for a in sys.argv[3:] if a in ['merge','apply','am']),'merge')
state_dir='rebase-apply' if backend in ['apply','am'] else 'rebase-merge'
root=Path(tempfile.mkdtemp(prefix='TreeGit rebase message audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args,success=True):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    if success:assert r.returncode==0,r.stderr
    return r
git('init','-b','main');git('config','user.name','Rebase QA');git('config','user.email','rebase@example.invalid')
for n in ['a.txt','b.txt']:(repo/n).write_text('base\n')
git('add','.');git('commit','-m','Base');base=git('rev-parse','HEAD').stdout.strip();git('switch','-c','feature')
steps=[]
commit_messages=[subject+('\n\n# Authored heading\n\n# Conflicts:\n#\t'+name if '--authored-headings' in sys.argv[3:] else '') for name,subject in [('a.txt','First rebase step'),('b.txt','Second rebase step')]]
for n,message in zip(['a.txt','b.txt'],commit_messages):
    (repo/n).write_text('feature\n');git('add','.');git('commit','-m',message);steps.append(git('rev-parse','HEAD').stdout.strip())
patch=root/'commits.patch';patch.write_text(git('format-patch','--stdout',base+'..feature').stdout,encoding='utf-8')
git('switch','main')
for n in ['a.txt','b.txt']:(repo/n).write_text('main\n')
git('add','.');git('commit','-m','Onto commit');onto=git('rev-parse','HEAD').stdout.strip()
if backend=='am':
    assert git('am','-3',str(patch),success=False).returncode!=0
    steps=['1','2']
else:
    git('switch','feature')
    assert git('rebase','--'+backend,'main',success=False).returncode!=0
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
def probe(operation='read_draft',draft=None):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    data={'action':'cancel','operation':operation,'disableAutoRefresh':'--manual-refresh' in sys.argv[3:]}
    if draft is not None:data['draft']=draft
    fixture.write_text(json.dumps(data),encoding='utf-8');tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Rebase probe timed out'
    result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
    records.append({'operation':operation,'details':result['operationDetails']});return result['operationDetails']
def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    state=probe();check('first conflict displays the first commit and its message',state['step']==steps[0] and 'First rebase step' in state['draft'])
    if '--close' in sys.argv[3:]:
        saved_draft='Saved draft before closing\n\nUser-authored recovery notes' if '--edited-close' in sys.argv[3:] else state['draft']
        if '--edited-close' in sys.argv[3:]:probe('set_draft',saved_draft)
        merge_file=repo/'.git/MERGE_MSG';existed=merge_file.exists()
        tab=next(t for t in tool('ui_tabs',{'action':'list'})['tabs'] if t['repo'] and Path(t['repo'])==repo)
        if '--readonly-close' in sys.argv[3:]:
            assert existed,'This test requires a real MERGE_MSG'
            before_message=merge_file.read_bytes();merge_file.chmod(stat.S_IREAD);closed_without_error=True
            try:
                try:tool('ui_tabs',{'action':'close','index':tab['index']})
                except AssertionError as error:
                    closed_without_error=False;records.append({'operation':'readonly_close','error':str(error)})
            finally:merge_file.chmod(stat.S_IWRITE|stat.S_IREAD)
            check('a read-only Git message does not prevent closing or alter its bytes',closed_without_error and merge_file.read_bytes()==before_message)
            if not closed_without_error:tool('ui_tabs',{'action':'close','index':tab['index']})
        else:tool('ui_tabs',{'action':'close','index':tab['index']})
        check('closing an unfinished operation does not invent unrelated Git message files',merge_file.exists()==existed)
        if '--advance-closed' in sys.argv[3:]:
            (repo/'a.txt').write_text('resolved externally\n');git('add','--','a.txt')
            continued=git('-c','core.editor=true','am' if backend=='am' else 'rebase','--continue',success=False)
            check('external continuation reaches the second real conflict',continued.returncode!=0 and (repo/'.git'/state_dir).exists())
        if '--abort-closed' in sys.argv[3:]:git('am' if backend=='am' else 'rebase','--abort')
        tool('ui_open_repository',{'repo':str(repo)});state=probe()
        if '--advance-closed' in sys.argv[3:]:
            check('reopening after external continuation shows the new step and message',state['step']==steps[1] and 'Second rebase step' in state['draft'] and saved_draft not in state['draft'])
            (repo/'b.txt').write_text('resolved second\n');git('add','--','b.txt')
            if '--auto-lock-test' in sys.argv[3:]:
                state_message=repo/'.git'/state_dir/('message' if backend=='merge' else 'final-commit')
                original_bytes=state_message.read_bytes();original_head=git('rev-parse','HEAD').stdout;index_lock=repo/'.git/index.lock'
                with index_lock.open('xb'):failed=probe('native_continue')
                index_lock.unlink()
                check('failed default continuation preserves the authored message and releases busy state',failed['draft']==commit_messages[1] and not failed['busy'] and failed['step']==steps[1])
                check('failed default continuation restores the original Git message bytes',state_message.read_bytes()==original_bytes and git('rev-parse','HEAD').stdout==original_head)
            state=probe('native_continue')
            check('continuing after reopening commits the exact second step message',git('log','-1','--format=%B').stdout.strip()==commit_messages[1] and state['context'] is None)
            assert not failures,failures
            sys.exit(0)
        if '--abort-closed' in sys.argv[3:]:
            check('reopening after external abort keeps only a user-authored draft',state['context'] is None and state['draft']==(saved_draft if '--edited-close' in sys.argv[3:] else ''))
            assert not failures,failures
            sys.exit(0)
        check('reopening the unfinished operation restores its draft and current step',state['step']==steps[0] and state['draft']==saved_draft)
    (repo/'a.txt').write_text('resolved first\n');git('add','--','a.txt')
    head_before=git('rev-parse','HEAD').stdout
    message_file=repo/'.git'/state_dir/('message' if backend=='merge' else 'final-commit')
    default_message=message_file.read_bytes()
    lock=repo/'.git/index.lock'
    with lock.open('xb') as owned_lock:
        try:failed=probe('native_continue','Edited first message\n\nCustom first body')
        finally:pass
    lock.unlink()
    check('failed continue preserves edited draft and releases busy state',failed['draft']=='Edited first message\n\nCustom first body' and not failed['busy'])
    check('failed continue retains the original operation step and Git message',failed['step']==steps[0] and git('rev-parse','HEAD').stdout==head_before and message_file.read_bytes()==default_message)
    state=probe('native_continue','Edited first message\n\nCustom first body')
    first=git('log','-1','--format=%B').stdout
    check('native continue commits the edited first message','Edited first message' in first and 'Custom first body' in first)
    check('next conflict displays the second commit and its message',state['step']==steps[1] and 'Second rebase step' in state['draft'] and 'Edited first message' not in state['draft'])
    state=probe('set_draft','Edited second message\n\nCustom second body')
    check('editing the second message survives metadata refresh',state['draft']=='Edited second message\n\nCustom second body')
    (repo/'b.txt').write_text('resolved second\n');git('add','--','b.txt')
    state=probe('native_continue','Edited second message\n\nCustom second body')
    final=git('log','-2','--format=%B').stdout
    check('native continue completes rebase with both edited messages','Edited first message' in final and 'Edited second message' in final and not (repo/'.git'/state_dir).exists())
    check('finished rebase releases busy state and clears the operation context',not state['busy'] and state['context'] is None)
    check('rebased history still contains the expected onto commit',git('merge-base','--is-ancestor',onto,'HEAD',success=False).returncode==0)
    assert not failures,failures
finally:
    (out/'rebase-messages-results.json').write_text(json.dumps({'records':records,'failures':failures,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
