"""Metadata read failures must not offer to initialize an existing repository."""
import json, os, queue, shutil, subprocess, sys, tempfile, threading, time
from pathlib import Path

source=Path(sys.argv[1]).resolve(); out=Path(sys.argv[2]).resolve()
proxy=Path(sys.argv[3]).resolve(); out.mkdir(parents=True,exist_ok=True)
records=[]
for case in ['config','silent-once','silent-always','mcp-once','mcp-always','nonrepo']:
    app=out/(case+'-app')
    shutil.copytree(source.parent,app,ignore=shutil.ignore_patterns('data','log','.treegit-update'))
    (app/'data').mkdir()
    (app/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','GitInstallPath':str(proxy),'Check4UpdatesOnStartup':False}),'utf-8')
    root=Path(tempfile.mkdtemp(prefix='TreeGit repository read '+case+' ')).resolve()
    repo=root/'repo'; repo.mkdir(); marker=root/'metadata-probe'
    env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1',
         'TREEGIT_TEST_REAL_GIT':shutil.which('git'),'TREEGIT_TEST_PROBE_MARKER':str(marker),
         'TREEGIT_TEST_PROBE_TARGET':'--absolute-git-dir' if case.startswith('mcp-') else '--show-toplevel',
         'TREEGIT_TEST_PROBE_FAILURE':'once' if case.endswith('-once') else 'always' if case.endswith('-always') else 'pass'}
    def git(*args):
        r=subprocess.run([env['TREEGIT_TEST_REAL_GIT'],'-C',str(repo),*args],env=env,capture_output=True)
        assert r.returncode==0,r.stderr
    if case!='nonrepo':
        git('init','-b','main');git('config','user.name','Read failure QA');git('config','user.email','read@example.invalid')
        (repo/'file.txt').write_text('keep original content\n','utf-8');git('add','.');git('commit','-m','Original')
        if case=='config':(repo/'.git/config').write_bytes(b'[broken configuration\n')
        originals={str(p.relative_to(repo)):p.read_bytes() for p in [repo/'.git/config',repo/'.git/HEAD',repo/'file.txt']}
    else: originals={}
    worktree_before={str(p.relative_to(repo)):p.read_bytes() for p in repo.rglob('*') if p.is_file() and '.git' not in p.relative_to(repo).parts}
    exe=app/source.name; startup=None; p=None; pid=None; record={'case':case}
    try:
        if not case.startswith('mcp-'):
            startup=subprocess.Popen([str(exe),str(repo)],env=env,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,
                                     stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
            # Test the startup argument separately from cold-start concurrency.
            # Wait for this GUI before allowing MCP to launch a second first instance.
            literal=str(exe).replace("'","''")
            script=("$ErrorActionPreference='Stop';$p=Get-Process -Id "+str(startup.pid)+";"
                    "if($p.Path -ne '"+literal+"'){throw 'Unexpected startup process'};"
                    "$watch=[Diagnostics.Stopwatch]::StartNew();"
                    "while($p.MainWindowHandle -eq 0){if($p.HasExited){throw 'Startup GUI exited'};"
                    "if($watch.Elapsed.TotalSeconds -gt 35){throw 'Startup GUI did not become visible'};"
                    "Start-Sleep -Milliseconds 50;$p.Refresh()}")
            subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
        p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
        messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0
        def rpc(method,params):
            global seq
            seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
            result=json.loads(messages.get(timeout=60));assert 'error' not in result,result
            return result['result']
        def tool(name,args=None,allow_error=False):
            r=rpc('tools/call',{'name':name,'arguments':args or {}})
            if not allow_error:assert not r.get('isError'),r
            return r
        def state():return json.loads(tool('ui_state')['content'][0]['text'])
        rpc('initialize',{'protocolVersion':'2025-11-25'});current=state();pid=current['processId']
        if startup:assert startup.pid==pid,'The startup-argument GUI was not the selected desktop'
        if case.startswith('mcp-'):
            result=tool('ui_open_repository',{'repo':str(repo)},allow_error=True);record['openResult']=result
            assert bool(result.get('isError'))==(case=='mcp-always'),result
            if case=='mcp-always':
                message=result['content'][0]['text'];assert 'Try opening the repository again' in message and 'not a trusted' not in message,message
            current=state()
        record['state']=current
        if case in ('silent-once','mcp-once'):
            assert any(Path(r['repo']).resolve()==repo for r in current['repositories']),current
        elif case=='nonrepo':assert current['activePage']['pendingDialog']=='Init',current
        else:
            assert current['activePage']['pendingDialog'] is None and not current['repositories'],current
            if not case.startswith('mcp-'):assert current['activePage']['notifications']>0,current
        calls=Path(str(marker)+'.calls')
        if case.endswith('-always'):
            record['probeCalls']=len(calls.read_text().splitlines());assert record['probeCalls']==2,record['probeCalls']
        for name,content in originals.items():assert (repo/name).read_bytes()==content,name
        assert {str(p.relative_to(repo)):p.read_bytes() for p in repo.rglob('*') if p.is_file() and '.git' not in p.relative_to(repo).parts}==worktree_before,'Opening the repository changed worktree files'
        if case=='nonrepo':assert not (repo/'.git').exists(),'Opening the initialization form created a repository'
        record['passed']=True;print('PASS',case,'bounded read recovery, correct UI, original files retained',flush=True)
    finally:
        records.append(record);(out/'repository-open-results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),'utf-8')
        if p:
            p.stdin.close()
            try:p.wait(timeout=10)
            except subprocess.TimeoutExpired:p.kill();p.wait()
        literal=str(exe).replace("'","''")
        script=("$ErrorActionPreference='Stop';$target='"+literal+"';"
                "Get-Process | Where-Object {$_.Path -eq $target} | ForEach-Object {"
                "if($_.Path -ne $target){throw 'Unexpected owned app'};$null=$_.CloseMainWindow();"
                "if(-not $_.WaitForExit(15000)){$_.Kill();$_.WaitForExit();throw 'Owned GUI did not exit'}};exit 0")
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
