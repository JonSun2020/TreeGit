"""Use the native Amend view and its unstage method; inspect the real index and worktree."""
import json,os,queue,shutil,subprocess,sys,tempfile,threading,time
from pathlib import Path
from qa_profile import IsolatedProfile
from qa_cleanup import remove_fixture, new_fixture
source=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True);records=[]
profile=IsolatedProfile(source,out)
for format_name,scenario in [(f,s) for f in ['sha1','sha256'] for s in ['full','root','subset']]:
    case=format_name+'-'+scenario
    app=profile.reset();(app/'data/preference.json').write_text('{"Locale":"zh_CN","Check4UpdatesOnStartup":false}','utf-8')
    root=new_fixture(out,prefix='TreeGit native amend '+format_name+' ').resolve();repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
    env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
    def git(*args):
        result=subprocess.run(['git','-C',str(repo),*args],stdin=subprocess.DEVNULL,env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW);assert result.returncode==0,result.stderr;return result.stdout.strip()
    git('init','-b','main','--object-format='+format_name);git('config','user.name','Amend QA');git('config','user.email','amend@example.invalid');git('config','core.filemode','false')
    # An index-only Unix filename exercises NUL parsing without creating an invalid Windows file.
    git('config','core.protectNTFS','false')
    for name,text in [('script.sh','#!/bin/sh\necho old\n'),('delete.txt','delete me\n'),('rename-old.txt','unchanged rename content\n')]: (repo/name).write_text(text,'utf-8')
    git('add','.');git('update-index','--chmod=+x','script.sh');git('commit','-m','Parent');parent_tree=git('rev-parse','HEAD^{tree}')
    odd_path='index-only\tline\nname.txt'
    if scenario=='root':
        parent_tree=git('hash-object','-t','tree','--stdin')
    else:
        (repo/'script.sh').write_text('#!/bin/sh\necho new\n','utf-8');(repo/'delete.txt').unlink();git('mv','rename-old.txt','新名称 [1].txt');(repo/'added.txt').write_text('new file\n','utf-8');git('add','-A')
        blob=git('rev-parse','HEAD:script.sh')
        synthetic=subprocess.run(['git','-C',str(repo),'update-index','-z','--index-info'],input=('100644 '+blob+'\t'+odd_path+'\0').encode('utf-8'),env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW);assert synthetic.returncode==0 and not synthetic.stderr,synthetic.stderr
        git('commit','-m','Amend target')
        assert odd_path in git('ls-tree','-rz','--name-only','HEAD').split('\0'),'Git did not include the synthetic filename'
    expected_tree=parent_tree
    if scenario=='subset':
        expected_index=root/'expected-index';shutil.copy2(repo/'.git/index',expected_index)
        expected_env={**env,'GIT_INDEX_FILE':str(expected_index)}
        reset=subprocess.run(['git','-C',str(repo),'reset','HEAD^','--','script.sh'],env=expected_env,capture_output=True);assert reset.returncode==0,reset.stderr
        result=subprocess.run(['git','-C',str(repo),'write-tree'],env=expected_env,capture_output=True);assert result.returncode==0,result.stderr
        expected_tree=result.stdout.decode('ascii').strip();expected_index.unlink()
    head=git('rev-parse','HEAD')
    before={p.name:p.read_bytes().hex() for p in repo.iterdir() if p.is_file()};draft='Keep the amend draft '+format_name
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_amend_unstage','allChanges':scenario!='subset','paths':['script.sh'],'draft':draft}), 'utf-8')
    exe=app/source.name;p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW);messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start();seq=0;pid=None;record={'format':format_name,'scenario':scenario}
    def rpc(method,params):
        global seq
        seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush();result=json.loads(messages.get(timeout=45));assert 'error' not in result,result;return result['result']
    def tool(name,args=None):
        result=rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result;return json.loads(result['content'][0]['text'])
    try:
        rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];tool('ui_open_repository',{'repo':str(repo)});tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        result_file=Path(str(fixture)+'.result');deadline=time.monotonic()+25
        while not result_file.exists() and time.monotonic()<deadline:time.sleep(.03)
        assert result_file.exists(),'Native amend operation did not finish';record['result']=json.loads(result_file.read_text('utf-8'));assert 'exception' not in record['result'],record
        details=record['result']['operationDetails'];assert details['draft']==draft and not details['busy'],details
        if scenario=='full':assert odd_path in [c['path'] for c in details['selected']],details
        if scenario=='subset':assert [c['path'] for c in details['selected']]==['script.sh'],details
        assert git('write-tree')==expected_tree,'The unstage operation did not restore the expected index'
        assert git('rev-parse','HEAD')==head and {f.name:f.read_bytes().hex() for f in repo.iterdir() if f.is_file()}==before,'Amend unstage changed a commit or worktree content'
        record['passed']=True;print('PASS native amend unstage',case,'restores expected index, preserves worktree and draft',flush=True)
    finally:
        records.append(record);(out/'amend-unstage-results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),'utf-8');p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()
        if pid:
            literal=str(exe).replace("'","''");script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'Owned QA app did not exit normally'}};exit 0";subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)

    remove_fixture(root,out,'TreeGit ')

profile.close()
