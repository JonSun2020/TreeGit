"""Native archive, patch, blame and bisect workflows, including rejected operations."""
import base64, json, os, queue, re, stat, subprocess, sys, tempfile, threading, time, zipfile
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit advanced Git audit '));repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()
git('init','-b','main',*(['--ref-format=reftable'] if '--reftable' in sys.argv[3:] else []));git('config','user.name','Advanced QA 中文');git('config','user.email','advanced@example.invalid')
(repo/'a[1].txt').write_text('first\nsecond\n');(repo/'a1.txt').write_text('other\n');steps=[]
for i in range(8):
    (repo/'version.txt').write_text(str(i)+'\n')
    if i==3:(repo/'a[1].txt').write_text('first\nselected second\n')
    if i==4:(repo/'a1.txt').write_text('unselected history\n')
    git('add','.');git('commit','-m','Revision '+str(i));steps.append(git('rev-parse','HEAD'))
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
def probe(operation,**fields):
    result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True);fixture.write_text(json.dumps({'action':'cancel','operation':'native_'+operation,**fields}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert result_file.exists(),'Native advanced operation timed out'
    result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result;records.append(result);return result

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    target=root/"existing O'Brien 中文.zip";backup=b'Original valuable archive';target.write_bytes(backup)
    failed=probe('archive',revision='0'*40,destination=str(target))
    check('archive failure is reported without replacing the existing output',not failed['operationResult'] and target.read_bytes()==backup)
    target.write_bytes(backup);saved=probe('archive',revision=steps[-1],destination=str(target))
    check('archive can be retried successfully',saved['operationResult'])
    with zipfile.ZipFile(target) as z:
        check('archive stores the exact selected revision and literal filenames',all(z.read(name)==(repo/name).read_bytes() for name in ['version.txt','a[1].txt','a1.txt']))
    original=target.read_bytes();target.chmod(stat.S_IREAD)
    try:failed=probe('archive',revision=steps[-1],destination=str(target))
    finally:target.chmod(stat.S_IREAD|stat.S_IWRITE)
    check('unwritable archive destination preserves existing bytes and reports failure',not failed['operationResult'] and target.read_bytes()==original)
    relative=probe('archive',revision=steps[-1],destination='relative archive.zip')
    check('relative archive paths resolve inside the selected repository',relative['operationResult'] and (repo/'relative archive.zip').is_file())
    (repo/'relative archive.zip').unlink(missing_ok=True)
    check('archive retries leave no owned temporary output files',not list(root.glob('.treegit-archive-*')))
    blame=probe('blame',paths=['a[1].txt'])['operationDetails']
    check('native blame follows only the literal selected filename',not blame['binary'] and blame['content']=='first\nselected second' and len(blame['lines'])==2 and steps[0].startswith(blame['lines'][0]) and steps[3].startswith(blame['lines'][1]))
    (repo/'a[1].txt').write_text('patched first\nselected second\n');patch=root/"patch O'Brien 中文.patch"
    patch.write_text(git('--literal-pathspecs','diff','--','a[1].txt')+'\n',encoding='utf-8');git('--literal-pathspecs','restore','--worktree','--','a[1].txt')
    applied=probe('patch',file=str(patch))
    check('native patch application modifies the selected file only',applied['operationResult'] and (repo/'a[1].txt').read_text()=='patched first\nselected second\n' and (repo/'a1.txt').read_text()=='unselected history\n')
    before=git('diff');failed=probe('patch',file=str(patch))
    check('rejected patch application reports failure and preserves the working tree',not failed['operationResult'] and git('diff')==before)
    git('--literal-pathspecs','restore','--worktree','--','a[1].txt');result=probe('bisect',arguments='start '+steps[-1]+' '+steps[0])
    check('native bisect enters an actual detached search state',(repo/'.git/BISECT_START').exists() and not result['operationDetails']['busy'] and result['operationDetails']['state']=='WaitingForMark')
    for _ in range(10):
        version=int((repo/'version.txt').read_text());result=probe('bisect',arguments='bad' if version>=5 else 'good')
        if re.search(r"is the first '?bad'? commit",result['operationDetails']['log']):break
    check('native bisect locates the independently defined first defective revision',re.search(steps[5]+r" is the first '?bad'? commit",result['operationDetails']['log']) is not None)
    check('completed bisect displays a finished state and selects its actual result',result['operationDetails']['state']=='Completed' and result['operationDetails']['firstBad']==steps[5] and result['operationDetails']['selected']==steps[5])
    tool('ui_command',{'command':'clear_notifications'})
    image=rpc('tools/call',{'name':'ui_screenshot','arguments':{}});(out/'bisect-completed.png').write_bytes(base64.b64decode(image['content'][0]['data']))
    result=probe('bisect',arguments='reset')
    check('native bisect reset restores the original branch and clean files',git('symbolic-ref','--short','HEAD')=='main' and git('rev-parse','HEAD')==steps[-1] and git('status','--porcelain')=='' and not result['operationDetails']['busy'])
    lock=repo/'.git/index.lock'
    with lock.open('xb'):
        result=probe('bisect',arguments='start '+steps[-1]+' '+steps[0])
    lock.unlink()
    check('blocked bisect checkout preserves HEAD and releases busy state','index.lock' in result['operationDetails']['log'] and git('rev-parse','HEAD')==steps[-1] and not result['operationDetails']['busy'])
    probe('bisect',arguments='reset')
    check('bisect recovery remains available after a failed checkout',git('symbolic-ref','--short','HEAD')=='main' and not (repo/'.git/BISECT_START').exists())
    assert not failures,failures
finally:
    (out/'advanced-git-results.json').write_text(json.dumps({'records':records,'failures':failures,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
