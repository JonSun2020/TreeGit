"""Stress the real GUI with 10k commits and hundreds of branch references in CI."""
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
from qa_cleanup import new_fixture, remove_fixture
from qa_profile import IsolatedProfile

assert os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_OS')=='Windows'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
profile=IsolatedProfile(exe,out);root=new_fixture(out,'TreeGit large repository ')
repo=root/'repo';repo.mkdir();env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
verifier=shutil.which('git');assert verifier
checks=[];samples=[];timings={};failure=None;p=None;pid=None;seq=0;max_response=0
messages=queue.Queue();started=time.monotonic()

def git(*args,input=None):
    result=subprocess.run([verifier,'--no-optional-locks','-C',str(repo),*args],env=env,input=input.encode('utf-8') if input is not None else None,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,(args,result.stderr.decode('utf-8',errors='replace')[-2048:]);return result.stdout.decode('utf-8').strip()

def tool(name,args=None):
    global seq,max_response
    seq+=1;method='initialize' if name=='initialize' else 'tools/call'
    params={'protocolVersion':'2025-11-25'} if method=='initialize' else {'name':name,'arguments':args or {}}
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    raw=messages.get(timeout=120);max_response=max(max_response,len(raw.encode('utf-8')))
    assert len(raw)<64*1024*1024,'Unbounded GUI response'
    response=json.loads(raw);assert 'error' not in response,response
    if method=='initialize':return response['result']
    result=response['result'];assert not result.get('isError'),result
    return base64.b64decode(result['content'][0]['data']) if name=='ui_screenshot' else json.loads(result['content'][0]['text'])

def state(**page):return next(r for r in tool('ui_state',page)['repositories'] if Path(r['repo']).resolve()==repo)
def wait(label,fn):
    before=time.monotonic()
    while time.monotonic()-before<120:
        value=fn()
        if value:timings[label]=round(time.monotonic()-before,3);return value
        time.sleep(.15)
    raise AssertionError(label+' timed out')

def check(label,condition):
    assert condition,label
    checks.append(label);print('PASS '+label,flush=True)

def sample(label):
    script="[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);Get-Process -Id "+str(pid)+"|Select-Object Id,Path,PrivateMemorySize64,WorkingSet64,HandleCount|ConvertTo-Json -Compress"
    result=subprocess.run(['powershell','-NoProfile','-Command',script],capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode==0,result.stderr
    process=json.loads(result.stdout);assert process['Path'].lower()==str(exe).lower()
    size=sum(f.stat().st_size for f in root.rglob('*') if f.is_file() and not f.is_symlink())
    samples.append({'label':label,'seconds':round(time.monotonic()-started,2),'process':process,'fixtureBytes':size})
    assert process['PrivateMemorySize64']<1536*1024**2,'Application exceeded the isolated memory budget'
    assert size<2*1024**3,'Fixture exceeded 2 GiB'

try:
    profile.reset();(exe.parent/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Theme':'Blue','MaxHistoryCommits':20000,'EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
    git('init','-b','main');git('config','user.name','Large history QA');git('config','user.email','large@example.invalid');git('config','core.autocrlf','false')
    stream=root/'fast-import.txt';marks_file=root/'marks.txt';mark=0
    with stream.open('wb') as output:
        def emit(branch,parent,message,content):
            global mark
            mark+=1
            msg=message.encode('utf-8');data=content.encode('utf-8')
            output.write(('commit '+branch+'\nmark :'+str(mark)+'\ncommitter Large QA <large@example.invalid> '+str(1700000000+mark)+' +0000\ndata '+str(len(msg))+'\n').encode()+msg+b'\n')
            if parent:output.write(('from :'+str(parent)+'\n').encode())
            output.write(('M 100644 inline history.txt\ndata '+str(len(data))+'\n').encode()+data+b'\n\n')
        for index in range(1,10001):
            emit('refs/heads/main',mark,'Stress history '+str(index)+(' stress-needle' if index%1000==0 else ''),'Main value '+str(index)+'\n')
        for branch in range(12):
            parent=8000+branch*40
            for index in range(3):
                emit('refs/heads/feature/deep/'+str(branch),parent,'Side branch '+str(branch)+' step '+str(index),'Side '+str(branch)+' value '+str(index)+'\n');parent=mark
    with stream.open('rb') as input:
        imported=subprocess.run([verifier,'-C',str(repo),'fast-import','--quiet','--export-marks='+str(marks_file)],env=env,stdin=input,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
    assert imported.returncode==0,imported.stderr.decode('utf-8',errors='replace')[-2048:]
    marks={int(line.split()[0][1:]):line.split()[1] for line in marks_file.read_text().splitlines()}
    updates=[]
    for index in range(160):
        sha=marks[5000+index*5000//159]
        for prefix in ['refs/heads/topic','refs/remotes/origin/topic']:
            updates.append('update '+prefix+'/group'+str(index//10)+'/task'+str(index)+' '+sha)
    for index in range(20):updates.append('update refs/tags/audit-'+str(index)+' '+marks[1000+index*400])
    git('update-ref','--stdin',input='\n'.join(updates)+'\n');git('remote','add','origin',str(root/'unused-remote.git'));git('reset','--hard','main')
    head=git('rev-parse','HEAD');expected_count=int(git('rev-list','--all','--count'));before_index=git('ls-files','--stage','-z')
    stream.unlink();marks_file.unlink()
    p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start()
    tool('initialize');pid=tool('ui_state')['processId'];tool('ui_window',{'width':1440,'height':900,'theme':'Blue'})
    tool('ui_open_repository',{'repo':str(repo)});tool('ui_graph',{'repo':str(repo),'mode':'all','showRemotes':True})
    wait('initial graph',lambda:not state()['graphLoading'] and state()['graphTotal']==expected_count)
    snapshot=state();check('all 10036 commits and 333 local/remote branches load',snapshot['graphTotal']==10036 and len(snapshot['loadedBranches'])==333)
    current=next(c for c in snapshot['graphCommits'] if c['sha']==head)
    check('current branch remains in the leftmost graph lane',current['x']==min(c['x'] for c in snapshot['graphCommits']))
    check('a clean large repository has no synthetic uncommitted row',not any(c['subject']=='未提交的更改' for c in snapshot['graphCommits']))
    sample('large graph');(out/'large-history.png').write_bytes(tool('ui_screenshot'))
    tool('ui_graph',{'repo':str(repo),'branch':'refs/heads/feature/deep/3'})
    wait('navigate other branch',lambda:not state()['graphLoading'])
    navigated=state();check('branch navigation preserves current HEAD and graph lane',git('rev-parse','HEAD')==head and next(c for c in navigated['graphCommits'] if c['sha']==head)['x']==current['x'])
    tool('ui_search',{'repo':str(repo),'query':'Stress history','method':'message','currentBranchOnly':False})
    wait('1000 result search',lambda:not state()['searchQuerying'] and state()['searchResultCount']==1000)
    results=state()['searchResults'];check('search reaches its 1000 result limit with branch coverage',all(r['branchCount'] and r['branches'] for r in results))
    check('default state bounds its search payload without hiding the full count',0<len(results)<1000 and state()['searchResultsTruncated'])
    all_results=[]
    for offset in range(0,1000,256):
        page=state(searchOffset=offset,searchLimit=256,searchBranchLimit=0)
        check('search page preserves complete totals '+str(offset),page['searchResultCount']==1000 and page['searchResultOffset']==offset and page['searchResultsTruncated']==(offset+256<1000))
        all_results.extend(page['searchResults'])
    check('all 1000 search results remain readable through pagination',len(all_results)==1000 and len({r['sha'] for r in all_results})==1000)
    for result in results[:5]:
        expected=git('for-each-ref','--contains',result['sha'],'--format=%(refname)','refs/heads','refs/remotes').splitlines()
        check('branch containment matches native Git '+result['sha'][:10],sorted(result['branches'])==sorted(expected))
    sample('1000 covered results');(out/'large-search.png').write_bytes(tool('ui_screenshot'))
    tool('ui_search',{'repo':str(repo),'query':'stress-needle','method':'message','currentBranchOnly':False})
    wait('sparse full history search',lambda:not state()['searchQuerying'] and len(state()['searchResults'] or [])==10)
    results=state()['searchResults'];oldest=min(results,key=lambda r:int(r['subject'].split()[2]))
    expected=git('for-each-ref','--contains',oldest['sha'],'--format=%(refname)','refs/heads','refs/remotes').splitlines()
    old_index=results.index(oldest);full_branches=[]
    for offset in range(0,oldest['branchCount'],256):
        page=state(searchOffset=old_index,searchLimit=1,searchBranchOffset=offset,searchBranchLimit=256)
        result=page['searchResults'][0]
        check('containing branch page preserves counts '+str(offset),result['branchCount']==333 and result['branchesTruncated']==(offset+256<333))
        full_branches.extend(result['branches'])
    check('old commits expose every actual descendant branch',sorted(full_branches)==sorted(expected) and len(expected)==333)
    tool('ui_search',{'repo':str(repo),'select':oldest['sha'],'open':True})
    wait('open old result',lambda:state()['detailCommit']==oldest['sha'] and not state()['graphLoading'])
    check('opening a deep search result selects the exact commit with all branches visible',state()['graphMode']==0 and state()['graphShowRemotes'] and not state()['searchActive'])
    for theme,zoom in [('Dark',1.5),('Light',1),('Blue',1)]:
        tool('ui_window',{'width':1024,'height':720,'theme':theme,'zoom':zoom});sample(theme+' resized')
    check('graph, search and view navigation never modify HEAD, index or worktree',git('rev-parse','HEAD')==head and git('ls-files','--stage','-z')==before_index and not git('status','--porcelain'))
    tool('ui_search',{'repo':str(repo),'query':'','method':'message'});tool('ui_select_view',{'repo':str(repo),'view':'history'})
    for tab in reversed(tool('ui_tabs',{'action':'list'})['tabs']):
        if tab['repo']:tool('ui_tabs',{'action':'close','index':tab['index']})
    sample('all tabs closed')
    check('paged MCP responses stay below 512 KiB for the large fixture',max_response<512*1024)
except BaseException as error:
    failure=repr(error);raise
finally:
    record={'passed':len(checks),'checks':checks,'failure':failure,'timings':timings,'maxResponseBytes':max_response,'samples':samples,'fixtureRemoved':False,'ownedApplicationExited':False}
    (out/'large-history-results.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
    if p:
        p.stdin.close()
        try:p.wait(timeout=15)
        except subprocess.TimeoutExpired:p.kill();p.wait(timeout=10)
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned GUI'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){taskkill /PID "+str(pid)+" /T /F|Out-Null;if(-not $p.WaitForExit(10000)){throw 'Owned GUI survived'}}}"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    record['ownedApplicationExited']=True
    remove_fixture(root,out,'TreeGit large repository ');record['fixtureRemoved']=True;profile.close()
    (out/'large-history-results.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
