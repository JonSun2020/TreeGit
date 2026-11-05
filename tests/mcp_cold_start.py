"""Concurrent first connections must start exactly one desktop and release all stdio clients."""
from concurrent.futures import ThreadPoolExecutor
import json, queue, subprocess, sys, threading, time
from pathlib import Path
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
barrier=threading.Barrier(12);start=time.monotonic()
def client(index):
    p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    replies=queue.Queue();threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start()
    def request(n,method,params=None):
        p.stdin.write(json.dumps({'jsonrpc':'2.0','id':n,'method':method,'params':params or {}})+'\n');p.stdin.flush()
        r=json.loads(replies.get(timeout=60));assert 'error' not in r,r;return r['result']
    try:
        request(1,'initialize',{'protocolVersion':'2025-11-25'});barrier.wait(timeout=20)
        r=request(2,'tools/call',{'name':'ui_state','arguments':{}});assert not r.get('isError'),r
        state=json.loads(r['content'][0]['text']);assert state['application']=='TreeGit'
        return {'client':index,'desktop':state['processId'],'seconds':round(time.monotonic()-start,2)}
    finally:
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();raise AssertionError('Stdio endpoint inherited a desktop pipe handle')
        assert p.returncode==0
records=[]
try:
    with ThreadPoolExecutor(max_workers=12) as pool:records=list(pool.map(client,range(12)))
    assert len({r['desktop'] for r in records})==1,records
    print('PASS 12 concurrent cold clients start one desktop and all exit on EOF',flush=True)
finally:
    (out/'cold-start-results.json').write_text(json.dumps({'records':records,'seconds':round(time.monotonic()-start,2),'exe':str(exe)},indent=2),encoding='utf-8')
    literal=str(exe).replace("'", "''")
    script=("$ErrorActionPreference='Stop';$target='"+literal+"';"
            "Get-Process | Where-Object {$_.Path -eq $target} | ForEach-Object {"
            "if($_.Path -ne $target){throw 'Unexpected process'};"
            "$null=$_.CloseMainWindow();if(-not $_.WaitForExit(15000)){"
            "$_.Kill();$_.WaitForExit();throw 'Owned QA desktop did not exit normally'}};exit 0")
    subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
