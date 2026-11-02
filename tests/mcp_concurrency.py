"""Concurrent clients and bounded large output through the actual desktop bridge."""
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import subprocess
import sys
import time

exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve()
repo=Path(json.loads((out/'mcp-results.json').read_text(encoding='utf-8'))['repository'])
def client(index):
    p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    def request(i,method,params={}):
        p.stdin.write(json.dumps({'jsonrpc':'2.0','id':i,'method':method,'params':params})+'\n');p.stdin.flush()
        response=json.loads(p.stdout.readline())
        assert 'error' not in response,response
        return response['result']
    request(1,'initialize',{'protocolVersion':'2025-11-25'})
    for n in range(5):
        r=request(n+2,'tools/call',{'name':'git_status','arguments':{'repo':str(repo)}})
        assert not r.get('isError'),r
    p.stdin.close();p.wait(timeout=10)
    assert p.returncode==0
    return index
start=time.monotonic()
with ThreadPoolExecutor(max_workers=12) as pool:
    results=list(pool.map(client,range(12)))
assert len(results)==12
print('PASS 12 simultaneous clients, 60 GUI-backed Git requests',flush=True)
git=exe.parent/'git/cmd/git.exe'
def run(*args):subprocess.run([str(git),'-C',str(repo),*args],check=True,capture_output=True)
file=repo/'large-output.txt'
file.write_text('baseline\n',encoding='utf-8')
run('add','--','large-output.txt');run('commit','-m','Large diff baseline')
file.write_text('x'*1400000+'\n',encoding='utf-8')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
def exchange(i,method,params):
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':i,'method':method,'params':params})+'\n');p.stdin.flush()
    return json.loads(p.stdout.readline())['result']
exchange(1,'initialize',{'protocolVersion':'2025-11-25'})
r=exchange(2,'tools/call',{'name':'git_diff','arguments':{'repo':str(repo),'paths':['large-output.txt']}})
assert not r.get('isError'),r
data=json.loads(r['content'][0]['text'])
assert data['truncated'] and len(data['stdout'])==1024*1024
p.stdin.close();p.wait(timeout=10)
run('restore','--','large-output.txt')
print('PASS large diff bounded to 1 MiB with explicit truncation',flush=True)
(out/'concurrency-results.json').write_text(json.dumps({'clients':12,'requests':60,'all_passed':True,'large_output_limit':1048576,'large_output_truncation_verified':True,'seconds':round(time.monotonic()-start,2)},indent=2),encoding='utf-8')
