"""Native interactive rebase must retain planned reword jobs after conflict continuation."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit interactive messages audit '));fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(repo,*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()
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
def probe(repo,operation,**fields):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':operation,**fields}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Interactive rebase probe timed out'
    r=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in r,r;records.append(r);return r

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    for scenario in ['normal','conflict-edited','conflict-default']:
        repo=root/scenario;repo.mkdir();git(repo,'init','-b','main');git(repo,'config','user.name','Interactive QA');git(repo,'config','user.email','interactive@example.invalid')
        for n in ['a.txt','b.txt']:(repo/n).write_text('base\n')
        git(repo,'add','.');git(repo,'commit','-m','Base');base=git(repo,'rev-parse','HEAD');git(repo,'switch','-c','feature');steps=[]
        for n,message in [('a.txt','Original first'),('b.txt','Original second')]:
            (repo/n).write_text('feature\n');git(repo,'add','.');git(repo,'commit','-m',message);steps.append(git(repo,'rev-parse','HEAD'))
        tree=git(repo,'rev-parse','HEAD^{tree}');onto=base
        if scenario!='normal':
            git(repo,'switch','main');(repo/'a.txt').write_text('main\n');git(repo,'commit','-am','Main conflict');onto=git(repo,'rev-parse','HEAD');git(repo,'switch','feature')
        tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
        result=probe(repo,'native_interactive',onto=onto,jobs=[{'sha':sha,'action':'Reword','message':message} for sha,message in zip(steps,['Planned first message','Planned second message'])])
        if scenario=='normal':
            check('native interactive reword completes successfully',result['operationResult'] and not (repo/'.git/rebase-merge').exists())
            check('native interactive reword preserves the original file tree',git(repo,'rev-parse','HEAD^{tree}')==tree)
        else:
            check(scenario+' stops at the expected real conflict',not result['operationResult'] and (repo/'.git/rebase-merge/stopped-sha').read_text().strip()==steps[0])
            (repo/'a.txt').write_text('feature\n');git(repo,'add','--','a.txt')
            fields={'draft':'GUI conflict message'} if scenario=='conflict-edited' else {}
            result=probe(repo,'native_continue',**fields)
            check(scenario+' continues through later planned jobs',not (repo/'.git/rebase-merge').exists() and result['operationDetails']['context'] is None)
        committed_messages=git(repo,'log','-2','--format=%B');first='GUI conflict message' if scenario=='conflict-edited' else 'Planned first message'
        check(scenario+' commits the intended first message',first in committed_messages)
        check(scenario+' preserves the subsequent planned reword message','Planned second message' in committed_messages)
        check(scenario+' keeps the expected onto commit reachable',subprocess.run(['git','-C',str(repo),'merge-base','--is-ancestor',onto,'HEAD'],env=env).returncode==0)
    assert not failures,failures
finally:
    (out/'interactive-message-results.json').write_text(json.dumps({'records':records,'failures':failures,'root':str(root),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
