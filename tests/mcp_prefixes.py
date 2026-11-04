"""Global Git options retain the actual repository's credential scope and SSH key."""
import json, os, queue, subprocess, sys, tempfile, threading
from pathlib import Path

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit MCP prefix audit '));trace=root/'query.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',GIT_ALLOW_PROTOCOL='file',TREEGIT_TEST_QUERY_TRACE=str(trace),TREEGIT_TEST_PROTOCOL='always')

def git(where,*args):
    r=subprocess.run(['git','-C',str(where),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr;return r.stdout.strip()

repos={name:root/name for name in ['left','right','bare-client.git']};keys={name:root/(name+' private key') for name in repos}
remote=root/'remote.git';remote.mkdir();git(remote,'init','--bare')
for name,repo in repos.items():
    repo.mkdir();git(repo,'init',*(['--bare'] if name.endswith('.git') else ['-b','main']))
    git(repo,'config','user.name',name);git(repo,'config','user.email','prefix@example.invalid');git(repo,'config','sourcegit.credentialmanager','true' if name=='left' else 'false')
    keys[name].write_text('owned non-secret key fixture; local transport never reads this file')
    git(repo,'remote','add','origin',str(remote));git(repo,'config','remote.origin.sshkey',str(keys[name]))
left=repos['left'];right=repos['right'];(left/'a.txt').write_text('base\n');git(left,'add','.');git(left,'commit','-m','Base');git(left,'push','origin','main');sha=git(left,'rev-parse','HEAD')
(right/'b.txt').write_text('base\n');git(right,'add','.');git(right,'commit','-m','Right base')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]

def tool(name,args=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':'tools/call','params':{'name':name,'arguments':args or {}}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    r=r['result'];assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])

def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)

try:
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':0,'method':'initialize','params':{'protocolVersion':'2025-11-25'}})+'\n');p.stdin.flush();assert 'error' not in json.loads(messages.get(timeout=45))
    for repo in [right,left]:tool('ui_open_repository',{'repo':str(repo)})
    pid=tool('ui_state')['processId']
    cases=[('plain','left',[]),('config','left',['-c','protocol.file.allow=always']),('flags','left',['--no-optional-locks','--no-pager','-c','protocol.file.allow=always']),('directory','right',['-C',str(right),'-c','protocol.file.allow=always']),('relative directories','right',['-C',str(root),'-C','right']),('explicit git dir','right',['--git-dir',str(right/'.git'),'--work-tree',str(right)]),('equals and environment options','right',['--git-dir='+str(right/'.git'),'--work-tree='+str(right),'--config-env=protocol.file.allow=TREEGIT_TEST_PROTOCOL']),('bare directory','bare-client.git',['-C',str(repos['bare-client.git'])]),('explicit bare dir','bare-client.git',['--git-dir',str(repos['bare-client.git'])]),('configuration overrides','left',['-c','sourcegit.credentialmanager=false','-c','remote.origin.sshkey='+str(keys['right'])])]
    for label,target,prefix in cases:
        result=tool('git_run',{'repo':str(left),'arguments':prefix+['ls-remote','origin']});state=json.loads(trace.read_text(encoding='utf-8'));records.append({'case':label,'trace':state,'result':result})
        check(label+' executes the intended Git command',result['isSuccess'] and sha in result['stdout'])
        expected_key='right' if label=='configuration overrides' else target
        expected_helper=target!='left' or label=='configuration overrides'
        check(label+' uses the actual repository credential scope',Path(state['scope']).resolve()==repos[target].resolve() and state['ownCredentialHelper']==expected_helper)
        check(label+' uses the actual remote SSH key',str(keys[expected_key]).replace('\\','/') in state['ssh'] and all(str(key).replace('\\','/') not in state['ssh'] for name,key in keys.items() if name!=expected_key))
    result=tool('git_run',{'repo':str(left),'arguments':['-C',str(right),'config','user.name','Right modified through prefix']})
    check('directory-prefixed writes affect only their target repository',result['isSuccess'] and git(right,'config','user.name')=='Right modified through prefix' and git(left,'config','user.name')=='left')
    assert not failures,failures
finally:
    (out/'mcp-prefix-results.json').write_text(json.dumps({'records':records,'failures':failures,'root':str(root),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
