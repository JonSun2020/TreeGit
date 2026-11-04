"""Explicit conflict recovery must update the UI with automatic refresh disabled."""
import json, os, queue, shutil, stat, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit explicit recovery audit '));fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture),GIT_EDITOR='true')

def git(repo,*args,success=True):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    if success:assert r.returncode==0,r.stderr
    return r.stdout.strip() if success else r

p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]

def tool(name,args=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':'tools/call','params':{'name':name,'arguments':args or {}}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    r=r['result'];assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])

def probe(repo,operation,allow_error=False):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
    payload={'action':'cancel','operation':operation,'disableAutoRefresh':True}
    if '--default-message' not in sys.argv[3:]:payload['draft']='Resolved recovery message'
    fixture.write_text(json.dumps(payload),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Recovery action timed out'
    r=json.loads(result_file.read_text(encoding='utf-8'))
    if allow_error and 'exception' in r:return r
    assert 'exception' not in r,r
    return r['operationDetails']

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)

try:
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':0,'method':'initialize','params':{'protocolVersion':'2025-11-25'}})+'\n');p.stdin.flush()
    assert 'error' not in json.loads(messages.get(timeout=45))
    cases=[(kind,'continue') for kind in ['merge','cherry-pick','revert']]+[(kind,'skip') for kind in ['cherry-pick','revert','rebase','rebase-apply','am']]
    if '--readonly-message' in sys.argv[3:]:cases=[('merge','continue')]
    if '--default-message' in sys.argv[3:]:cases=[(kind,'continue') for kind in ['merge','cherry-pick','revert']]
    if '--clean-merge' in sys.argv[3:]:cases=[('merge','continue')]
    if '--resolve-strategy' in sys.argv[3:]:cases=[('merge','continue')]
    for kind,action in cases:
        label=kind+'-'+action;repo=root/label;repo.mkdir();file=repo/'file.txt'
        git(repo,'init','-b','main');git(repo,'config','user.name','Recovery QA');git(repo,'config','user.email','recovery@example.invalid')
        file.write_text('base\n');git(repo,'add','.');git(repo,'commit','-m','Base')
        file.write_text('one\n');git(repo,'commit','-am','One');one=git(repo,'rev-parse','HEAD')
        feature_message='Feature\n\n# Authored heading\n\n# Conflicts:\n#\tfile.txt' if '--default-message' in sys.argv[3:] and (kind=='cherry-pick' or '--clean-merge' in sys.argv[3:]) else 'Feature'
        git(repo,'switch','-c','feature');(repo/'feature.txt' if '--clean-merge' in sys.argv[3:] else file).write_text('feature\n');git(repo,'add','.');git(repo,'commit','-m',feature_message);feature=git(repo,'rev-parse','HEAD')
        git(repo,'switch','main');file.write_text('main\n');git(repo,'commit','-am','Main')
        args=['merge','--no-edit','feature'] if kind=='merge' else ['cherry-pick',feature] if kind=='cherry-pick' else ['revert','--no-edit',one] if kind=='revert' else ['rebase','feature']
        if kind=='rebase-apply':args=['rebase','--apply','feature']
        if kind=='am':
            patch=root/'feature.patch';patch.write_text(git(repo,'format-patch','--stdout',one+'..feature'),encoding='utf-8');args=['am','-3',str(patch)]
        if '--clean-merge' in sys.argv[3:]:args=['merge','--no-commit','--no-ff','-m',feature_message,'feature']
        if '--resolve-strategy' in sys.argv[3:]:args=args[:1]+['-s','resolve']+args[1:]
        r=git(repo,*args,success=False);assert (r.returncode==0 if '--clean-merge' in sys.argv[3:] else r.returncode!=0),r.stdout
        reference=root/('reference-'+label);shutil.copytree(repo,reference)
        command='rebase' if kind=='rebase-apply' else kind
        if action=='continue':
            for target in [repo,reference]:
                (target/'file.txt').write_text('resolved\n');git(target,'add','--','file.txt')
            if '--default-message' not in sys.argv[3:]:(reference/'.git/MERGE_MSG').write_text('Resolved recovery message',encoding='utf-8')
            elif kind=='cherry-pick' or '--clean-merge' in sys.argv[3:]:(reference/'.git/MERGE_MSG').write_text(feature_message,encoding='utf-8')
        git(reference,*(['-c','core.commentString=±'] if '--default-message' in sys.argv[3:] and (kind=='cherry-pick' or '--clean-merge' in sys.argv[3:]) else []),command,'--'+action)
        tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
        if '--readonly-message' in sys.argv[3:]:
            message=repo/'.git/MERGE_MSG';before=message.read_bytes();head=git(repo,'rev-parse','HEAD');index=git(repo,'write-tree')
            message.chmod(stat.S_IREAD)
            try:failed=probe(repo,'native_continue',allow_error=True)
            finally:message.chmod(stat.S_IWRITE|stat.S_IREAD)
            records.append({'case':'readonly-message','state':failed})
            check('an unwritable recovery message reports failure without throwing into the UI','exception' not in failed and not failed['busy'] and 'MERGE_MSG' in failed['log'])
            check('an unwritable recovery message preserves the draft and recovery context','exception' not in failed and failed['draft']=='Resolved recovery message' and failed['context']=='MergeInProgress')
            check('an unwritable recovery message preserves HEAD, index and message bytes',git(repo,'rev-parse','HEAD')==head and git(repo,'write-tree')==index and message.read_bytes()==before)
        state=probe(repo,'native_'+action);records.append({'case':label,'state':state})
        check(label+' matches the file tree and revision count produced by Git',git(repo,'rev-parse','HEAD^{tree}')==git(reference,'rev-parse','HEAD^{tree}') and git(repo,'rev-list','--count','HEAD')==git(reference,'rev-list','--count','HEAD'))
        check(label+' clears the recovery panel without automatic refresh',state['context'] is None and not state['busy'])
        check(label+' leaves a clean index and the intended commit message',git(repo,'status','--porcelain')=='' and git(repo,'log','-1','--format=%B')==git(reference,'log','-1','--format=%B'))
    assert not failures,failures
finally:
    (out/'workflow-resume-results.json').write_text(json.dumps({'records':records,'failures':failures,'root':str(root),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
