"""A failed auto-stage must never commit an already staged subset."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit auto-stage failure audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()
git('init','-b','main');git('config','user.name','Commit QA');git('config','user.email','commit@example.invalid')
(repo/'ready.txt').write_text('base\n');(repo/'.gitattributes').write_text('*.blocked filter=qa-failure\n');git('add','.');git('commit','-m','Base');base=git('rev-parse','HEAD')
(repo/'ready.txt').write_text('already staged\n');git('add','--','ready.txt');(repo/'unavailable.blocked').write_text('must also be included\n')
git('config','filter.qa-failure.clean','false');git('config','filter.qa-failure.required','true')
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
def probe(auto_stage,twice=False):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_commit','draft':'Commit all intended changes','autoStage':auto_stage,'twice':twice}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Native commit timed out'
    result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
    records.append(result['operationDetails']);return result['operationDetails']
def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    before_index=git('write-tree');state=probe(True)
    check('auto-stage failure stops before creating a partial commit',git('rev-parse','HEAD')==base)
    check('auto-stage failure retains the originally staged subset',git('write-tree')==before_index)
    check('auto-stage failure preserves the draft and releases both busy states',state['draft']=='Commit all intended changes' and not state['busy'] and not state['staging'])
    check('auto-stage failure is recorded in the command log','clean filter' in '\n'.join(state['logs']).lower())
    # Restore this disposable fixture before testing an independent hook rejection.
    git('reset','--soft',base);git('add','--','ready.txt')
    hook=repo/'.git/hooks/pre-commit';hook.write_text('#!/bin/sh\necho QA-hook-rejected >&2\nexit 1\n')
    state=probe(False)
    check('a rejected pre-commit hook creates no commit and preserves staged data',git('rev-parse','HEAD')==base and git('write-tree')==before_index)
    check('a rejected pre-commit hook preserves the draft and releases busy state',state['draft']=='Commit all intended changes' and not state['busy'] and 'QA-hook-rejected' in '\n'.join(state['logs']))
    hook.unlink();git('config','filter.qa-failure.clean','cat');state=probe(True)
    check('retry after filter and hook recovery includes every intended file',git('diff-tree','--no-commit-id','--name-only','-r','HEAD').splitlines()==['ready.txt','unavailable.blocked'])
    check('successful commit clears the draft and releases busy state',state['draft']=='' and not state['busy'] and not state['staging'])
    before=git('rev-list','--count','HEAD');(repo/'ready.txt').write_text('one more change\n');git('add','--','ready.txt');state=probe(False,True)
    check('two concurrent commit requests produce exactly one commit',int(git('rev-list','--count','HEAD'))==int(before)+1 and not state['busy'])
    assert not failures,failures
finally:
    (out/'commit-failure-results.json').write_text(json.dumps({'records':records,'failures':failures,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
