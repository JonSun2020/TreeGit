"""Cancellation must affect the target tree, not a sibling in its private console.

The coordinator gets a NEW hidden console. The probe is DETACHED. Before sending
anything, assert that the console contains ONLY the four owned helper processes.
"""
import json,os,subprocess,sys,tempfile,time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();helper=Path(sys.argv[2]).resolve();out=Path(sys.argv[3]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit private cancellation audit '));records=[];ids=None
startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
coordinator=subprocess.Popen([str(helper),'coordinate',str(root)],creationflags=subprocess.CREATE_NEW_CONSOLE,startupinfo=startup,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
try:
    ready=root/'processes.json';deadline=time.monotonic()+30
    while not ready.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert ready.exists(),'The isolated helper console did not initialize'
    ids=json.loads(ready.read_text('utf-8'));owned={ids['coordinator'],ids['target'],ids['sentinel'],ids['descendant']}
    assert ids['coordinator']==coordinator.pid and set(ids['consoleProcesses'])==owned,ids
    report=out/'native-cancellation.json'
    result=subprocess.run([str(exe),'--integration-cancel-process',str(ids['target']),str(ids['sentinel']),str(helper),str(report)],creationflags=subprocess.DETACHED_PROCESS,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=15)
    (out/'probe-stdout.log').write_bytes(result.stdout);(out/'probe-stderr.log').write_bytes(result.stderr)
    child=subprocess.run(['powershell','-NoProfile','-Command','Get-Process -Id '+str(ids['descendant'])+' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id'],capture_output=True)
    record={'callerExit':result.returncode,'ownedConsole':ids,'native':json.loads(report.read_text('utf-8')) if report.exists() else None,'coordinatorAlive':coordinator.poll() is None,'descendantAlive':bool(child.stdout.strip())}
    records.append(record);print(json.dumps(record),flush=True)
    assert result.returncode==0 and record['native']['ended'] and record['native']['sentinelAlive'] and record['coordinatorAlive'] and not record['descendantAlive'],'Cancellation affected another process or retained a descendant'
    print('PASS cancellation terminates the selected process while its console siblings survive',flush=True)
    git_report=out/'cancelled-git-command.json'
    result=subprocess.run([str(exe),'--integration-cancel-git',str(root/'git-query'),str(git_report)],creationflags=subprocess.DETACHED_PROCESS,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=15)
    (out/'git-stdout.log').write_bytes(result.stdout);(out/'git-stderr.log').write_bytes(result.stderr)
    git_result=json.loads(git_report.read_text('utf-8')) if git_report.exists() else None
    records.append({'actualGitCancellation':git_result,'callerExit':result.returncode})
    assert result.returncode==0 and git_result['success'],records[-1]
    print('PASS actual Git command cancellation leaves the caller and later Git commands usable',flush=True)
finally:
    # Only helpers whose executable path still matches the known owned binary.
    if ids:
        pid_list=','.join(str(ids[k]) for k in ['coordinator','target','sentinel','descendant'])
        literal=str(helper).replace("'","''")
        subprocess.run(['powershell','-NoProfile','-Command',"Get-Process -Id "+pid_list+" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '"+literal+"' } | Stop-Process"],capture_output=True)
    elif coordinator.poll() is None:coordinator.kill()
    (out/'process-cancellation-results.json').write_text(json.dumps({'records':records,'root':str(root)},indent=2),'utf-8')
