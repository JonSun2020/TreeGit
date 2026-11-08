"""Use the native rebase view model and real Git to verify recovery and drafts."""
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
exe = Path(sys.argv[1]).resolve(); out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
profile = IsolatedProfile(exe, out); profile.reset()
(exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','EnableAutoFetch':False,'Check4UpdatesOnStartup':False}), encoding='utf-8')
root = new_fixture(out, 'TreeGit native rebase safety '); repo = root/'repo'; repo.mkdir(); fixture = root/'operation.json'
env = {**os.environ, 'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'), 'GIT_CONFIG_NOSYSTEM':'1', 'TREEGIT_TEST_SETTINGS_FIXTURE':str(fixture)}
checks = []; failure = None; pid = None

def git(*args, ok=True):
    r = subprocess.run(['git','-C',str(repo),*args], env=env, capture_output=True, text=True, encoding='utf-8')
    if ok: assert r.returncode == 0, (args,r.stdout,r.stderr)
    return r.stdout.strip()

def write(name, text): (repo/name).write_text(text, encoding='utf-8')
def commit(message):
    git('add','-A'); git('commit','-m',message)
    return git('rev-parse','HEAD')

git('init','-b','main'); git('config','user.name','Rebase safety QA'); git('config','user.email','qa@example.invalid'); git('config','core.autocrlf','false')
write('values.txt','base\n'); write('draft.txt','draft base\n'); write('.gitignore','ignored.data\n'); base = commit('Base')
git('checkout','-b','safe'); write('remote.txt','incoming addition\n'); safe = commit('Safe addition')
git('checkout','-b','incoming',base); write('values.txt','incoming\n'); incoming = commit('Incoming conflict')
git('checkout','-b','ignored',base); write('ignored.data','incoming tracked file\n'); git('add','-f','ignored.data'); ignored = commit('Incoming ignored collision')
git('checkout','main'); write('values.txt','local\n'); main = commit('Local commit')
p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue(); threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start(); seq = 0

def tool(name,args=None):
    global seq
    seq += 1
    method = 'initialize' if name == 'initialize' else 'tools/call'
    params = {'protocolVersion':'2025-11-25'} if method == 'initialize' else {'name':name,'arguments':args or {}}
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n'); p.stdin.flush()
    result = json.loads(messages.get(timeout=60)); assert 'error' not in result, result
    if method == 'initialize': return result['result']
    result = result['result']; assert not result.get('isError'), result
    return json.loads(result['content'][0]['text'])

def wait(fn):
    for _ in range(250):
        v = fn()
        if v: return v
        time.sleep(.1)
    raise AssertionError('Timed out waiting for '+repr(fn))

def state(): return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve()==repo)
def refresh():
    before = state()['workingCopyRefreshVersion']; tool('ui_refresh',{'repo':str(repo)})
    wait(lambda:state()['workingCopyRefreshVersion']>before and state()['workingCopyStatusKnown'] and not state()['graphLoading'])
    actual = git('rev-parse','HEAD')
    wait(lambda:any(b['reference']=='refs/heads/main' and b['head']==actual for b in state()['loadedBranches']))

def prepare():
    if (repo/'.git/rebase-merge').exists() or (repo/'.git/rebase-apply').exists(): git('rebase','--abort')
    if (repo/'.git/MERGE_HEAD').exists(): git('merge','--abort')
    git('checkout','main'); git('reset','--hard',main); git('clean','-fdx'); git('stash','clear')
    git('config','--unset-all','rebase.autoStash',ok=False)
    tool('ui_working_copy',{'repo':str(repo),'message':''}); refresh()

def probe(target, draft, auto=False, **extra):
    result = Path(str(fixture)+'.result'); result.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_rebase','targets':[target],'draft':draft,'autoStash':auto,**extra}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    wait(result.exists)
    data = json.loads(result.read_text('utf-8')); assert 'exception' not in data, data
    wait(lambda:not tool('ui_state')['dialogs'])
    return data['operationDetails']

def check(name, condition):
    assert condition, name
    checks.append(name); print('PASS '+name,flush=True)

try:
    tool('initialize'); tool('ui_open_repository',{'repo':str(repo)}); pid = tool('ui_state')['processId']; prepare()
    result = probe(safe,'Keep my unrelated draft')
    check('successful rebase preserves the unrelated commit draft',result['succeeded'] and result['draft']=='Keep my unrelated draft' and not result['activeLogs'])
    check('successful rebase keeps local and incoming changes',git('merge-base',safe,'HEAD')==safe and (repo/'values.txt').read_text()=='local\n' and (repo/'remote.txt').read_text()=='incoming addition\n')
    prepare(); result = probe(incoming,'Keep my conflict draft')
    check('conflicted rebase reveals the pending files and preserves the draft',result['succeeded'] and result['selectedWorkingCopy'] and result['draft']=='Keep my conflict draft' and bool(git('ls-files','-u')) and not result['activeLogs'])
    prepare(); write('values.txt','uncommitted local bytes\n'); git('config','rebase.autoStash','true'); refresh()
    before_index = git('ls-files','--stage'); result = probe(safe,'Keep the blocked draft',auto=False)
    check('unchecked auto stash overrides repository config without modifying work',not result['succeeded'] and result['error'] and result['draft']=='Keep the blocked draft' and git('rev-parse','HEAD')==main and git('ls-files','--stage')==before_index and (repo/'values.txt').read_text()=='uncommitted local bytes\n' and not git('stash','list') and not (repo/'.git/rebase-merge').exists())
    prepare(); write('draft.txt','protected local draft\n'); git('add','draft.txt'); refresh()
    result = probe(safe,'Keep the automatic stash draft',auto=True)
    check('checked auto stash restores local file bytes and keeps the draft',result['succeeded'] and result['draft']=='Keep the automatic stash draft' and (repo/'draft.txt').read_text()=='protected local draft\n' and not git('stash','list'))
    prepare(); write('ignored.data','preserve ignored local bytes\n'); refresh(); result = probe(ignored,'Keep collision draft')
    check('rebase refuses an incoming ignored-file collision before changing HEAD',not result['succeeded'] and result['error'] and result['draft']=='Keep collision draft' and git('rev-parse','HEAD')==main and (repo/'ignored.data').read_text()=='preserve ignored local bytes\n' and not git('ls-files','-u') and not result['activeLogs'])
    prepare(); result = probe(incoming,'Keep destination-change draft',switchAfterOpen='safe')
    check('a changed destination cannot rebase a different branch',not result['succeeded'] and result['error'] and git('symbolic-ref','--short','HEAD')=='safe' and git('rev-parse','HEAD')==safe and result['draft']=='Keep destination-change draft')
    prepare(); git('merge','--no-commit','incoming',ok=False); refresh(); result = probe(safe,'Keep existing-operation draft')
    check('an existing operation is revealed without starting a second one',result['succeeded'] and result['selectedWorkingCopy'] and (repo/'.git/MERGE_HEAD').exists() and not (repo/'.git/rebase-merge').exists() and result['draft']=='Keep existing-operation draft' and not result['activeLogs'])
except BaseException as error:
    failure = repr(error)
    try: (out/'failure-state.json').write_text(json.dumps(tool('ui_state'),ensure_ascii=False,indent=2),encoding='utf-8')
    except BaseException: pass
    raise
finally:
    (out/'rebase-safety-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try: p.wait(timeout=15)
    except subprocess.TimeoutExpired: p.kill(); p.wait(timeout=10)
    if pid:
        literal = str(exe).replace("'","''")
        script = "$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned GUI'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Owned GUI did not close'}}"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
    remove_fixture(root,out,'TreeGit native rebase safety ')
    profile.close()
