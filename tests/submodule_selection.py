"""Selected submodule operations must preserve a similarly named unselected module."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit submodule selection audit '));repo=root/'parent';repo.mkdir();source=root/'source';source.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',GIT_ALLOW_PROTOCOL='file',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def run(where,*args):
    r=subprocess.run(['git','-C',str(where),*args],env=env,capture_output=True,text=True,encoding='utf-8')
    assert r.returncode==0,r.stderr
    return r.stdout.strip()
def git(*args):return run(repo,*args)
for r in [repo,source]:
    run(r,'init','-b','main');run(r,'config','user.name','Submodule QA');run(r,'config','user.email','submodule@example.invalid')
(source/'data.txt').write_text('original\n');run(source,'add','.');run(source,'commit','-m','Initial module');old=run(source,'rev-parse','HEAD')
selected='modules/mod[1]';other='modules/mod1'
for n in [selected,other]:git('-c','protocol.file.allow=always','submodule','add',str(source),n)
git('commit','-am','Two modules')
(source/'data.txt').write_text('updated\n');run(source,'commit','-am','Updated module');new=run(source,'rev-parse','HEAD')
for n in [selected,other]:run(repo/n,'fetch','origin');run(repo/n,'checkout','--detach',new)
git('add','.');git('commit','-m','Advance both module pointers')
for n in [selected,other]:run(repo/n,'checkout','--detach',old)
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start()
seq=0;pid=None;checks=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
def native(operation,paths,value=None):
    result=Path(str(fixture)+'.result');result.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':'cancel','operation':operation,'paths':paths,'value':value}),encoding='utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        if result.exists():
            try:r=json.loads(result.read_text(encoding='utf-8'))
            except json.JSONDecodeError:time.sleep(.05);continue
            assert 'exception' not in r,r
            assert r['operationResult'],r
            return r
        time.sleep(.05)
    raise AssertionError('Native operation did not finish')
def check(name,ok):
    checks.append({'name':name,'passed':bool(ok)});print(('PASS ' if ok else 'FAIL ')+name,flush=True)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    native('submodule_branch',[selected],'qa-selected')
    check('branch configuration changes only the selected module',git('config','-f','.gitmodules','--get','submodule.'+selected+'.branch')=='qa-selected' and 'submodule.'+other+'.branch' not in git('config','-f','.gitmodules','--list'))
    native('submodule_branch',[selected],None)
    check('clearing the selected branch restores inherited tracking','submodule.'+selected+'.branch' not in git('config','-f','.gitmodules','--list'))
    native('submodule_url',[selected],str(source)+'/.')
    check('URL configuration changes only the selected module',git('config','-f','.gitmodules','--get','submodule.'+selected+'.url')==source.as_posix()+'/.' and git('config','-f','.gitmodules','--get','submodule.'+other+'.url')==str(source))
    mcp=tool('git_run',{'repo':str(repo),'arguments':(['-c','protocol.file.allow=always'] if '--prefixed-mcp' in sys.argv[3:] else [])+['submodule','set-url','--',selected,str(source)]})
    check('MCP submodule commands preserve literal paths and Windows local URLs',mcp['isSuccess'] and git('config','-f','.gitmodules','--get','submodule.'+selected+'.url')==source.as_posix() and git('config','-f','.gitmodules','--get','submodule.'+other+'.url')==str(source))
    native('submodule_update',[selected])
    check('selected submodule updates to the recorded revision',run(repo/selected,'rev-parse','HEAD')==new)
    check('updating a bracket-named submodule preserves the unselected revision',run(repo/other,'rev-parse','HEAD')==old)
    for n in [selected,other]:run(repo/n,'checkout','--detach',old)
    native('submodule_deinit',[selected])
    check('deinit removes only the selected submodule worktree',not (repo/selected/'data.txt').exists())
    check('deinit preserves the unselected worktree',(repo/other/'data.txt').is_file())
    git('submodule','update','--init')
    git('add','--','.gitmodules')
    # This dirty unselected file must survive deletion of the similarly named module.
    (repo/other/'data.txt').write_text('unsaved unselected data\n')
    native('submodule_delete',[selected])
    check('delete removes the selected submodule from the index',selected not in git('ls-files','--stage'))
    check('delete keeps the unselected submodule in the index',other in git('ls-files','--stage'))
    check('delete preserves unselected dirty file bytes',(repo/other/'data.txt').is_file() and (repo/other/'data.txt').read_text()=='unsaved unselected data\n')
    git('-c','protocol.file.allow=always','submodule','add',str(source),'modules/add1')
    native('submodule_add',['modules/add[1]'],str(source))
    check('adding a bracket-named module preserves the existing sibling',(repo/'modules/add[1]/data.txt').exists() and (repo/other/'data.txt').read_text()=='unsaved unselected data\n')
    spaced='modules/中文 module'
    native('submodule_add',[spaced],str(source))
    native('submodule_branch',[spaced],'main')
    (repo/spaced/'data.txt').write_text('dirty submodule with spaces\n')
    modules=native('submodule_state',[])['operationDetails'];module=next(m for m in modules if m['path']==spaced)
    check('submodule names with Chinese and spaces retain their URL',module['url']==source.as_posix())
    check('submodule names with spaces retain the configured tracking branch',module['branch']=='main')
    check('dirty state is shown for a submodule with spaces in its path',module['status']=='Modified')
    assert all(c['passed'] for c in checks),checks
finally:
    (out/'submodule-selection-results.json').write_text(json.dumps({'checks':checks,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
