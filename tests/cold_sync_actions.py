"""Use a real delayed Git query to click the normal sync buttons before metadata arrives."""
import json,os,queue,shutil,subprocess,sys,tempfile,threading,time
from pathlib import Path

source=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();proxy=Path(sys.argv[3]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit cold sync actions ')).resolve();repo=root/'repo';repo.mkdir();remote=root/'remote.git';remote.mkdir()
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_REAL_GIT=shutil.which('git'))
def git(path,*args):
    result=subprocess.run([env['TREEGIT_TEST_REAL_GIT'],'-C',str(path),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,result.stderr
git(remote,'init','--bare');git(repo,'init','-b','main');git(repo,'config','user.name','Cold sync QA');git(repo,'config','user.email','cold@example.invalid')
(repo/'file.txt').write_text('base\n');git(repo,'add','.');git(repo,'commit','-m','Initial');git(repo,'remote','add','origin',str(remote));git(repo,'push','-u','origin','main')
records=[]
for command,expected in [('fetch_dialog','Fetch'),('pull_dialog','Pull'),('push_dialog','Push')]:
    app=out/(command+'-app');shutil.copytree(source.parent,app,ignore=shutil.ignore_patterns('data','log','.treegit-update'));(app/'data').mkdir()
    (app/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','GitInstallPath':str(proxy),'Check4UpdatesOnStartup':False}),'utf-8')
    gate=root/command;env['TREEGIT_TEST_BRANCH_GATE']=str(gate);exe=app/source.name;pid=None;record={'command':command}
    p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
    replies=queue.Queue();threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start();seq=0
    def rpc(method,params):
        global seq
        seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
        result=json.loads(replies.get(timeout=40));assert 'error' not in result,result
        return result['result']
    def tool(name,args=None):
        result=rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result
        return json.loads(result['content'][0]['text'])
    try:
        rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];tool('ui_open_repository',{'repo':str(repo)})
        deadline=time.monotonic()+8
        while not Path(str(gate)+'.observed').exists() and time.monotonic()<deadline:time.sleep(.02)
        assert Path(str(gate)+'.observed').exists(),'The initial branch query did not pause'
        before=tool('ui_state')['repositories'];record['before']=before;assert any(r['branch'] is None for r in before),'The click was not made during initial metadata loading'
        tool('ui_command',{'repo':str(repo),'command':command});Path(str(gate)+'.release').write_text('continue')
        deadline=time.monotonic()+8;state={}
        while time.monotonic()<deadline:
            state=tool('ui_state');current=next(r for r in state['repositories'] if r['active'])
            if current['pendingDialog']==expected:break
            time.sleep(.05)
        record['after']=current;record['passed']=current['pendingDialog']==expected;print(('PASS ' if record['passed'] else 'FAIL ')+command+' waits for initial metadata and opens the correct dialog',flush=True)
        if current['pendingDialog']:tool('ui_command',{'repo':str(repo),'command':'cancel_popup'})
    finally:
        Path(str(gate)+'.release').write_text('continue');records.append(record)
        (out/'cold-sync-results.json').write_text(json.dumps({'root':str(root),'records':records},ensure_ascii=False,indent=2),'utf-8')
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()
        if pid:
            literal=str(exe).replace("'","''")
            script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'Owned QA app did not exit normally'}};exit 0"
            subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
assert all(record.get('passed') for record in records),records
