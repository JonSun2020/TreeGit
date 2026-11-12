"""Concurrent clients and bounded output, with one disposable repository per run."""
from concurrent.futures import ThreadPoolExecutor
import json, os, queue, shutil, subprocess, sys, tempfile, threading, time
from pathlib import Path
from qa_cleanup import new_fixture, remove_fixture
from qa_process import close_owned_gui

assert os.environ.get('CI') == 'true', 'Use only an owned CI application'
exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
parent=Path(tempfile.gettempdir()).resolve();root=new_fixture(parent,'TreeGit m ');repo=root/'repo';repo.mkdir()
git=shutil.which('git');assert git
clients=[];pid=None;report={};start=time.monotonic()

def run(*args):
    subprocess.run([git,'-C',str(repo),*args],check=True,capture_output=True,timeout=30)

class Client:
    def __init__(self):
        self.p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        self.responses=queue.Queue();self.sequence=0;clients.append(self)
        threading.Thread(target=lambda:[self.responses.put(s) for s in self.p.stdout],daemon=True).start()
        self.request('initialize',{'protocolVersion':'2025-11-25'})
    def request(self,method,params):
        self.sequence+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.sequence,'method':method,'params':params})+'\n');self.p.stdin.flush()
        result=json.loads(self.responses.get(timeout=45));assert 'error' not in result,result;return result['result']
    def tool(self,name,args):
        r=self.request('tools/call',{'name':name,'arguments':args});assert not r.get('isError'),r
        return json.loads(r['content'][0]['text'])
    def close(self):
        if self.p.poll() is None:
            self.p.stdin.close()
            try:self.p.wait(timeout=10)
            except subprocess.TimeoutExpired:self.p.kill();self.p.wait(timeout=10);raise
        assert self.p.returncode==0

try:
    run('init','-b','main');run('config','user.name','Concurrency QA');run('config','user.email','concurrency@example.invalid')
    file=repo/'large-output.txt';file.write_text('baseline\n',encoding='utf-8');run('add','.');run('commit','-m','Baseline')
    owner=Client();pid=owner.tool('ui_state',{})['processId'];owner.tool('ui_open_repository',{'repo':str(repo)})
    def client(index):
        c=Client()
        try:
            for _ in range(5):c.tool('git_status',{'repo':str(repo)})
        finally:c.close()
        return index
    with ThreadPoolExecutor(max_workers=12) as pool:results=list(pool.map(client,range(12)))
    assert len(results)==12
    print('PASS 12 simultaneous clients, 60 GUI-backed Git requests',flush=True)
    file.write_text('x'*1400000+'\n',encoding='utf-8')
    data=owner.tool('git_diff',{'repo':str(repo),'paths':['large-output.txt']})
    assert data['truncated'] and len(data['stdout'])==1024*1024
    print('PASS large diff bounded to 1 MiB with explicit truncation',flush=True)
    report.update(clients=12,requests=60,all_passed=True,large_output_limit=1048576,large_output_truncation_verified=True)
except BaseException as error:
    report['failure']=repr(error);raise
finally:
    for c in clients:
        if c.p.poll() is None:
            c.p.stdin.close()
            try:c.p.wait(timeout=10)
            except subprocess.TimeoutExpired:c.p.kill();c.p.wait(timeout=10)
    if pid:close_owned_gui(exe,pid)
    remove_fixture(root,parent,'TreeGit m ')
    report.update(seconds=round(time.monotonic()-start,2),fixtureRemoved=not root.exists())
    (out/'concurrency-results.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
