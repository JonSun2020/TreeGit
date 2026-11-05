"""Open real repositories with damaged app metadata; keep exact original files recoverable."""
import json,os,queue,shutil,subprocess,sys,tempfile,threading,time
from pathlib import Path
source=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
app=out/'app';shutil.copytree(source.parent,app,ignore=shutil.ignore_patterns('data','log','.treegit-update'));(app/'data').mkdir()
(app/'data/preference.json').write_text('{"Locale":"zh_CN","Check4UpdatesOnStartup":false}','utf-8');exe=app/source.name
root=Path(tempfile.mkdtemp(prefix='TreeGit repository config recovery ')).resolve();env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
replies=queue.Queue();threading.Thread(target=lambda:[replies.put(s) for s in p.stdout],daemon=True).start();seq=0;pid=None;records=[]
def tool(name,args=None,allow_error=False):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':'tools/call','params':{'name':name,'arguments':args or {}}})+'\n');p.stdin.flush()
    result=json.loads(replies.get(timeout=40));assert 'error' not in result,result;result=result['result']
    if result.get('isError') and allow_error:return {'error':result['content'][0]['text']}
    assert not result.get('isError'),result
    return json.loads(result['content'][0]['text'])
def git(repo,*args):
    result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW);assert result.returncode==0,result.stderr
try:
    p.stdin.write('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}\n');p.stdin.flush();assert 'error' not in json.loads(replies.get(timeout=40));pid=tool('ui_state')['processId']
    for index,(name,original) in enumerate([('sourcegit.settings','null'),('sourcegit.settings','{"DefaultRemote":'),('sourcegit.uistates','null'),('sourcegit.uistates','{"LastCommitMessage":'),('sourcegit.uistates','{"HistoryFilters":null,"LastCommitMessage":"Keep this draft"}')]):
        repo=root/str(index);repo.mkdir();git(repo,'init','-b','main');git(repo,'config','user.name','Recovery QA');git(repo,'config','user.email','recovery@example.invalid');(repo/'file.txt').write_text('base\n');git(repo,'add','.');git(repo,'commit','-m','Initial')
        target=repo/'.git'/name;target.write_text(original,'utf-8');opened=tool('ui_open_repository',{'repo':str(repo)},True);state=tool('ui_state')
        loaded=any(Path(r['repo']).resolve()==repo for r in state['repositories']);backups=list(target.parent.glob(name+'.recovery-*'))
        preserved=len(backups)==1 and backups[0].read_text('utf-8')==original
        record={'case':index,'file':name,'opened':loaded,'originalBackedUp':preserved,'response':opened};records.append(record)
        print(('PASS ' if loaded and preserved else 'FAIL ')+name+' case '+str(index),flush=True)
        for tab in reversed(tool('ui_tabs',{'action':'list'})['tabs']):
            if tab['repo']:tool('ui_tabs',{'action':'close','index':tab['index']})
        if index==4 and loaded:
            record['draftPreserved']=json.loads(target.read_text('utf-8-sig'))['LastCommitMessage']=='Keep this draft'
            assert record['draftPreserved'],'A readable draft was lost while repairing unrelated settings'
    assert all(r['opened'] and r['originalBackedUp'] for r in records),records
finally:
    (out/'repository-config-recovery-results.json').write_text(json.dumps({'root':str(root),'records':records},ensure_ascii=False,indent=2),'utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait()
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit();throw 'Owned QA app did not exit normally'}};exit 0"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
