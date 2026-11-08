"""Real cherry-pick and revert outcomes must preserve drafts and reveal conflicts."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from qa_cleanup import new_fixture, remove_fixture
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_OS') == 'Windows'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
profile=IsolatedProfile(exe,out);profile.reset()
(exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
root=new_fixture(out,'TreeGit commit action safety ');repo=root/'repo';repo.mkdir();fixture=root/'operation.json'
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1','TREEGIT_TEST_SETTINGS_FIXTURE':str(fixture)}
checks=[];failures=[];records=[];failure=None;pid=None

def git(*args,ok=True):
    result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    if ok:assert result.returncode==0,(args,result.stdout,result.stderr)
    return result.stdout.strip()

def write(name,text): (repo/name).write_text(text,encoding='utf-8')
def commit(message):
    git('add','-A');git('commit','-m',message);return git('rev-parse','HEAD')

git('init','-b','main');git('config','user.name','Commit action QA');git('config','user.email','qa@example.invalid');git('config','core.autocrlf','false')
write('values.txt','base\n');write('draft.txt','base draft\n');write('.gitignore','ignored.data\nrestored.data\n');base=commit('Base')
git('checkout','-b','safe');write('incoming.txt','safe addition\n');safe=commit('First safe addition');write('second.txt','second addition\n');second=commit('Second safe addition')
git('checkout','-b','conflict',base);write('values.txt','incoming\n');conflict=commit('Incoming conflicting change')
git('checkout','-b','ignored',base);write('ignored.data','incoming tracked data\n');git('add','-f','ignored.data');ignored=commit('Incoming ignored path')
git('checkout','-b','benign-source');write('benign.txt','unrelated patch addition\n');benign_source=commit('Patch leaves ignored path unchanged')
git('checkout','-b','restore',base);write('restored.data','tracked original data\n');git('add','-f','restored.data');commit('Add restored path');git('rm','restored.data');restore=commit('Remove restored path')
git('checkout','main');write('values.txt','local\n');main=commit('Local change');git('branch','alias')
p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start();seq=0

def tool(name,args=None,expect_error=False):
    global seq
    seq+=1;method='initialize' if name=='initialize' else 'tools/call'
    params={'protocolVersion':'2025-11-25'} if method=='initialize' else {'name':name,'arguments':args or {}}
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    result=json.loads(messages.get(timeout=60))
    if 'error' in result:
        assert expect_error,result
        return result['error']['message']
    if method=='initialize':return result['result']
    result=result['result'];assert bool(result.get('isError'))==expect_error,result
    if expect_error:return result['content'][0]['text']
    return json.loads(result['content'][0]['text'])

def wait(fn):
    for _ in range(250):
        value=fn()
        if value:return value
        time.sleep(.1)
    raise AssertionError('Commit action observation timed out')

def state():return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve()==repo)
def refresh():
    version=state()['workingCopyRefreshVersion'];tool('ui_refresh',{'repo':str(repo)})
    wait(lambda:state()['workingCopyRefreshVersion']>version and state()['workingCopyStatusKnown'] and not state()['graphLoading'])
    actual=git('rev-parse','HEAD')
    wait(lambda:any(b['reference']=='refs/heads/main' and b['head']==actual for b in state()['loadedBranches']))

def prepare():
    for command in ['cherry-pick','revert','merge']:git(command,'--abort',ok=False)
    git('checkout','-f','main');git('reset','--hard',main);git('clean','-fdx')
    tool('ui_working_copy',{'repo':str(repo),'message':''});refresh()

def probe(operation,targets,draft,**extra):
    result=Path(str(fixture)+'.result');result.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_'+operation,'targets':targets,'draft':draft,'autoCommit':False,**extra}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'});wait(result.exists)
    data=json.loads(result.read_text('utf-8'));assert 'exception' not in data,data
    wait(lambda:not tool('ui_state')['dialogs']);records.append({'operation':operation,'targets':targets,'extra':extra,'details':data['operationDetails']});return data['operationDetails']

def check(name,condition):
    (checks if condition else failures).append(name);print(('PASS ' if condition else 'FAIL ')+name,flush=True)

try:
    tool('initialize');tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId'];refresh()
    for operation in ['cherry_pick','revert']:
        prepare();draft='Keep my unrelated draft\n保留原有说明'
        targets=[safe,second] if operation=='cherry_pick' else [main]
        result=probe(operation,targets,draft)
        check(operation+' without auto commit preserves the draft and stages actual changes',result['succeeded'] and result['draft']==draft and git('rev-parse','HEAD')==main and bool(git('diff','--cached','--name-only')) and not result['activeLogs'])
        prepare();result=probe(operation,targets,draft,autoCommit=True)
        check(operation+' with auto commit preserves the unrelated draft',result['succeeded'] and result['draft']==draft and git('rev-parse','HEAD')!=main and not git('status','--porcelain') and not result['activeLogs'])
        prepare();write('values.txt','uncommitted local edit\n');refresh();before=git('write-tree')
        result=probe(operation,[conflict if operation=='cherry_pick' else main],draft)
        check(operation+' failure preserves HEAD, index, local bytes and draft',not result['succeeded'] and result['error'] and git('rev-parse','HEAD')==main and git('write-tree')==before and (repo/'values.txt').read_text()=='uncommitted local edit\n' and result['draft']==draft and not result['activeLogs'])
        prepare();result=probe(operation,[conflict],draft)
        check(operation+' conflict reveals working copy without losing the draft',result['succeeded'] and result['selectedWorkingCopy'] and bool(git('ls-files','-u')) and result['draft']==draft and not result['activeLogs'])
        prepare();result=probe(operation,targets,draft,switchAfterOpen='alias')
        check(operation+' rejects another branch even when its HEAD is identical',not result['succeeded'] and result['error'] and git('symbolic-ref','--short','HEAD')=='alias' and git('rev-parse','HEAD')==main and result['draft']==draft)
        prepare();git('merge','--no-commit','conflict',ok=False);refresh();before=git('ls-files','-u');result=probe(operation,targets,draft)
        check(operation+' reveals an existing operation without replacing it',result['succeeded'] and result['selectedWorkingCopy'] and (repo/'.git/MERGE_HEAD').exists() and git('ls-files','-u')==before and result['draft']==draft)
        prepare();name='ignored.data' if operation=='cherry_pick' else 'restored.data';write(name,'preserve ignored local bytes\n');refresh()
        reference=new_fixture(root,'TreeGit native reference ')
        try:
            cloned=subprocess.run(['git','clone','--shared',str(repo),str(reference)],env=env,capture_output=True,text=True,encoding='utf-8');assert cloned.returncode==0,cloned.stderr
            (reference/name).write_text('preserve ignored local bytes\n',encoding='utf-8')
            native_args=['cherry-pick','-n','-x',ignored] if operation=='cherry_pick' else ['revert','-n','-m','1','--no-edit',restore]
            native=subprocess.run(['git','-C',str(reference),*native_args],env=env,capture_output=True,text=True,encoding='utf-8')
            records.append({'nativeReference':operation,'exitCode':native.returncode,'ignoredBytesPreserved':(reference/name).read_text()=='preserve ignored local bytes\n','stderr':native.stderr[-2048:]})
        finally:remove_fixture(reference,root,'TreeGit native reference ')
        result=probe(operation,[ignored if operation=='cherry_pick' else restore],draft)
        retained=(repo/name).read_text()=='preserve ignored local bytes\n'
        records[-1]['ignoredBytesPreserved']=retained
        check(operation+' protects an ignored file which its patch would recreate',not result['succeeded'] and name in result['error'] and retained and git('rev-parse','HEAD')==main and result['draft']==draft)
        prepare();write('ignored.data','preserve unrelated ignored bytes\n');refresh()
        result=probe(operation,[benign_source if operation=='cherry_pick' else main],draft)
        check(operation+' allows an unrelated ignored path even when present in the source tree',result['succeeded'] and (repo/'ignored.data').read_text()=='preserve unrelated ignored bytes\n' and result['draft']==draft and bool(git('diff','--cached','--name-only')))
    for operation,revision,name in [('cherry_pick',ignored,'ignored.data'),('cherry_pick',base+'..'+benign_source,'ignored.data'),
        ('revert',restore,'restored.data'),('rebase',ignored,'ignored.data'),('checkout','ignored','ignored.data')]:
        prepare();write(name,'preserve MCP local ignored bytes\n');refresh();before=git('write-tree')
        error=tool('git_action',{'repo':str(repo),'action':operation,'revision':revision},expect_error=True)
        retained=(repo/name).read_text()=='preserve MCP local ignored bytes\n'
        records.append({'mcp':operation,'revision':revision,'error':error,'ignoredBytesPreserved':retained})
        check('MCP '+operation+' protects ignored paths '+revision[:12],name in error and retained and git('rev-parse','HEAD')==main and git('write-tree')==before)
    for operation,revision in [('cherry_pick',benign_source),('cherry_pick',base+'..'+second),('revert',main),('rebase',safe),('checkout','safe')]:
        prepare();write('ignored.data','preserve benign MCP data\n');refresh()
        result=tool('git_action',{'repo':str(repo),'action':operation,'revision':revision})
        records.append({'mcp':operation,'revision':revision,'result':result})
        check('MCP '+operation+' allows benign local ignored data '+revision[:12],result['isSuccess'] and (repo/'ignored.data').read_text()=='preserve benign MCP data\n')
    prepare();write('transient.data','temporary history file\n');commit('Add temporary file');git('rm','transient.data');commit('Remove temporary file')
    (repo/'.git/info/exclude').write_text('*.data\n',encoding='utf-8');write('transient.data','preserve intermediate MCP data\n');refresh();before=git('rev-parse','HEAD')
    error=tool('git_action',{'repo':str(repo),'action':'rebase','revision':safe},expect_error=True)
    check('MCP rebase protects a path created and deleted by intermediate commits','transient.data' in error and (repo/'transient.data').read_text()=='preserve intermediate MCP data\n' and git('rev-parse','HEAD')==before)
    prepare();git('cherry-pick','-n',safe,conflict,ok=False);refresh()
    sequence=(repo/'.git/sequencer').exists();before=git('ls-files','-u')
    assert sequence and before,'Expected a real paused no-commit sequence'
    error=tool('git_action',{'repo':str(repo),'action':'merge','revision':safe},expect_error=True)
    records.append({'sequencerOnly':not (repo/'.git/CHERRY_PICK_HEAD').exists(),'error':error})
    check('MCP detects a paused no-commit sequence before another operation','Finish the current TreeGit' in error and git('ls-files','-u')==before and git('rev-parse','HEAD')==main)
    assert not failures,failures
except BaseException as error:
    failure=repr(error)
    try:(out/'failure-state.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2),encoding='utf-8')
    except BaseException:pass
    raise
finally:
    (out/'commit-action-safety-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failures':failures,'records':records,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=15)
    except subprocess.TimeoutExpired:p.kill();p.wait(timeout=10)
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned GUI'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Owned GUI did not close'}}"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
    remove_fixture(root,out,'TreeGit commit action safety ');profile.close()
