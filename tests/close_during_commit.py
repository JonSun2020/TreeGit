"""Closing a tab must not detach a still-running native commit and its editable state."""
import json, os, queue, subprocess, sys, tempfile, threading, time
from pathlib import Path

exe = Path(sys.argv[1]).resolve(); out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit close while committing ')); repo = root/'repo'; repo.mkdir()
fixture = root/'fixture.json'; started = repo/'hook-started'; release = repo/'hook-release'
env = os.environ.copy(); env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1', TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    result = subprocess.run(['git','-C',str(repo),*args], env=env, capture_output=True, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()
git('init','-b','main'); git('config','user.name','Lifecycle QA'); git('config','user.email','lifecycle@example.invalid')
(repo/'file.txt').write_text('before\n'); git('add','.'); git('commit','-m','Initial')
(repo/'file.txt').write_text('after\n'); git('add','file.txt')
(repo/'.git/hooks/pre-commit').write_text('#!/bin/sh\necho started > hook-started\ni=0\nwhile [ ! -f hook-release ] && [ "$i" -lt 200 ]; do sleep .1; i=$((i+1)); done\n[ -f hook-release ]\n')
fixture.write_text(json.dumps({'action':'cancel','operation':'native_close_during_commit','draft':'Protected native commit','started':str(started),'release':str(release)}),'utf-8')
p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
replies = queue.Queue(); threading.Thread(target=lambda:[replies.put(s) for s in p.stdout],daemon=True).start(); seq=0; pid=None; report={}
def rpc(method,params):
    global seq
    seq+=1; p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n'); p.stdin.flush()
    result=json.loads(replies.get(timeout=45)); assert 'error' not in result,result
    return result['result']
def tool(name,args=None):
    result=rpc('tools/call',{'name':name,'arguments':args or {}}); assert not result.get('isError'),result
    return json.loads(result['content'][0]['text'])
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'}); tool('ui_open_repository',{'repo':str(repo)}); pid=tool('ui_state')['processId']
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    deadline=time.monotonic()+35; result_file=Path(str(fixture)+'.result')
    while not result_file.exists() and time.monotonic()<deadline: time.sleep(.05)
    assert result_file.exists(),'Lifecycle probe did not finish'
    result=json.loads(result_file.read_text('utf-8')); report['fixture']=result
    assert 'exception' not in result,result
    assert result['operationDetails']['busy'],'The test did not attempt closing during the active commit'
    assert result['operationDetails']['stayedOpen'],'Closing the tab detached the active native commit'
    for key in ['windowStayedOpen','quitStayedOpen','otherTabsStayedOpen','rightTabsStayedOpen']:
        assert result['operationDetails'][key],key
    assert git('log','-1','--format=%s')=='Protected native commit'
    tabs=tool('ui_tabs',{'action':'list'})['tabs']; tab=next(t for t in tabs if t['repo'] and Path(t['repo'])==repo)
    tool('ui_tabs',{'action':'close','index':tab['index']})
    assert not any(t['repo'] and Path(t['repo'])==repo for t in tool('ui_tabs',{'action':'list'})['tabs'])
    report['passed']=True; print('PASS running commit keeps its tab; closing works after completion',flush=True)
finally:
    release.write_text('continue')
    (out/'close-during-commit-results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),'utf-8')
    if p.poll() is None:
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit()}}"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
