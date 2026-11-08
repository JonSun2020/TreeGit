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
git('checkout','-b','restore',base);write('restored.data','tracked original data\n');git('add','-f','restored.data');commit('Add restored path');git('rm','restored.data');restore=commit('Remove restored path')
git('checkout','main');write('values.txt','local\n');main=commit('Local change');git('branch','alias')
p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start();seq=0

def tool(name,args=None):
    global seq
    seq+=1;method='initialize' if name=='initialize' else 'tools/call'
    params={'protocolVersion':'2025-11-25'} if method=='initialize' else {'name':name,'arguments':args or {}}
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    result=json.loads(messages.get(timeout=60));assert 'error' not in result,result
    if method=='initialize':return result['result']
    result=result['result'];assert not result.get('isError'),result
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
        result=probe(operation,[ignored if operation=='cherry_pick' else restore],draft)
        check(operation+' protects an ignored file which its patch would recreate',not result['succeeded'] and (repo/name).read_text()=='preserve ignored local bytes\n' and git('rev-parse','HEAD')==main and result['draft']==draft)
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
