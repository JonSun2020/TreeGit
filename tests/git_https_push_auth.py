"""Real HTTPS Git receive-pack: wrong credentials, MCP/native push, and cancellation."""
import base64
import http.server
import json
import os
from pathlib import Path
from qa_profile import IsolatedProfile
from qa_cleanup import remove_fixture, new_fixture
import queue
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

source_exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True,exist_ok=True)
profile = IsolatedProfile(source_exe, out)
app = profile.reset()
(app/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Check4UpdatesOnStartup':False,
    'GitInstallPath':str(source_exe.parent/'git/cmd/git.exe')}),'utf-8')
exe = app/source_exe.name
root = new_fixture(out,prefix='TreeGit HTTPS real Git push ')
repo = root/'client'
project = root/'server'
bare = project/'repo.git'
repo.mkdir()
bare.mkdir(parents=True)
fixture = root/'askpass.json'
settings = root/'settings.json'
env = dict(os.environ,GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',
    SOURCEGIT_TEST_ASKPASS_FIXTURE=str(fixture),TREEGIT_TEST_SETTINGS_FIXTURE=str(settings))

def git(*args,cwd=repo):
    r = subprocess.run(['git','-C',str(cwd),*args],env=env,capture_output=True,text=True,encoding='utf-8',timeout=30)
    assert r.returncode == 0,r.stderr
    return r.stdout.strip()

git('init','-b','main')
git('config','user.name','HTTPS push QA')
git('config','user.email','https-push@example.invalid')
git('config','push.default','current')
git('init','--bare',cwd=bare)
git('config','http.receivepack','true',cwd=bare)
openssl = Path(shutil.which('git')).parent.parent/'usr/bin/openssl.exe'
certificate = root/'localhost-cert.pem'
key = root/'localhost-key.pem'
subprocess.run([str(openssl),'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
    '-subj','/CN=127.0.0.1','-addext','subjectAltName=IP:127.0.0.1','-keyout',str(key),'-out',str(certificate)],
    check=True,capture_output=True,timeout=30)
env['GIT_SSL_CAINFO'] = str(certificate)
requests = []
backend_errors = []

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self,*_):
        pass

    def serve(self):
        body = self.rfile.read(int(self.headers.get('Content-Length','0')))
        auth = self.headers.get('Authorization','')
        value = base64.b64decode(auth[6:]).decode() if auth.startswith('Basic ') else ''
        requests.append({'method':self.command,'path':self.path,'account':value.split(':')[0],
                         'authorized':value=='alice:correct-git-test'})
        if value != 'alice:correct-git-test':
            self.send_response(401)
            self.send_header('WWW-Authenticate','Basic realm="TreeGit Git QA"')
            self.send_header('Content-Length','0')
            self.end_headers()
            return
        parsed = urllib.parse.urlsplit(self.path)
        cgi = dict(env,GIT_PROJECT_ROOT=project.as_posix(),GIT_HTTP_EXPORT_ALL='1',
            REQUEST_METHOD=self.command,PATH_INFO=urllib.parse.unquote(parsed.path),
            QUERY_STRING=parsed.query,CONTENT_TYPE=self.headers.get('Content-Type',''),
            CONTENT_LENGTH=str(len(body)),REMOTE_USER='alice',REMOTE_ADDR='127.0.0.1',
            SERVER_PROTOCOL='HTTP/1.1',SERVER_NAME='127.0.0.1',SERVER_PORT=str(server.server_port),HTTPS='on')
        if self.headers.get('Git-Protocol'):
            cgi['HTTP_GIT_PROTOCOL'] = self.headers['Git-Protocol']
        response = subprocess.run(['git','http-backend'],input=body,env=cgi,cwd=project,
                                  capture_output=True,timeout=30)
        headers,separator,data = response.stdout.partition(b'\r\n\r\n')
        if not separator:
            headers,separator,data = response.stdout.partition(b'\n\n')
        if response.returncode or not separator:
            backend_errors.append(response.stderr.decode('utf-8','replace'))
            self.send_error(500)
            return
        fields = [line.split(':',1) for line in headers.decode('ascii').splitlines() if ':' in line]
        status = next((int(value.strip().split()[0]) for name,value in fields if name.lower()=='status'),200)
        self.send_response(status)
        for name,value in fields:
            if name.lower() not in ['status','content-length','connection','transfer-encoding']:
                self.send_header(name,value.strip())
        self.send_header('Content-Length',str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    do_GET = serve
    do_POST = serve

server = http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(certificate,key)
server.socket = context.wrap_socket(server.socket,server_side=True)
threading.Thread(target=server.serve_forever,daemon=True).start()
host = f'127.0.0.1:{server.server_port}'
url = 'https://'+host+'/repo.git'
git('remote','add','origin',url)

def credential(action,password=None):
    scope = dict(env,SOURCEGIT_CREDENTIAL_SCOPE=str(repo))
    text = 'protocol=https\nhost='+host+'\npath=repo.git\n'
    if password is not None:
        text += 'username=alice\npassword='+password+'\n'
    r = subprocess.run([str(exe),'--credential-helper',action],input=text+'\n',env=scope,
                       capture_output=True,text=True,encoding='utf-8',timeout=15)
    assert r.returncode == 0,r.stderr
    return r.stdout

p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
    text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue()
threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start()
seq = 0
pid = None
checks = []
failure = None

def rpc(method,params):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n')
    p.stdin.flush()
    r = json.loads(messages.get(timeout=75))
    assert 'error' not in r,r
    return r['result']

def tool(name,args=None,allow_error=False):
    r = rpc('tools/call',{'name':name,'arguments':args or {}})
    if r.get('isError'):
        if allow_error:
            return r
        raise AssertionError(r)
    return json.loads(r['content'][0]['text'])

try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    pid = tool('ui_state')['processId']
    tool('ui_open_repository',{'repo':str(repo)})
    for mode in ['action','run','native','cancel']:
        (repo/'file.txt').write_text('HTTPS commit '+mode+'\n','utf-8')
        git('add','.')
        git('commit','-m','Commit '+mode)
        head = git('rev-parse','HEAD')
        (repo/'file.txt').write_text('Keep staged '+mode+'\n','utf-8')
        git('add','file.txt')
        index = git('write-tree')
        (repo/'file.txt').write_text('Keep worktree '+mode+'\n','utf-8')
        worktree = (repo/'file.txt').read_bytes()
        if mode == 'cancel':
            remote_before_cancel = git('rev-parse','refs/heads/main',cwd=bare)
            credential('erase')
            fixture.write_text(json.dumps({'username':'cancelled','password':'','cancel':True}),'utf-8')
        else:
            credential('store','expired-test')
            fixture.write_text(json.dumps({'attempts':[
                {'username':'wrong-user','password':'bad-first'},
                {'username':'alice','password':'bad-second'},
                {'username':'alice','password':'correct-git-test'}]}),'utf-8')
        if mode == 'native':
            reply = Path(str(settings)+'.result')
            settings.write_text(json.dumps({'action':'cancel','operation':'native_push_hook'}),'utf-8')
            tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
            deadline = time.monotonic()+60
            while time.monotonic()<deadline:
                try:
                    result = json.loads(reply.read_text('utf-8-sig'))
                    break
                except (FileNotFoundError,PermissionError,json.JSONDecodeError):
                    time.sleep(.05)
            assert result.get('operationResult'),result
        else:
            name = 'git_run' if mode == 'run' else 'git_action'
            args = {'repo':str(repo),'arguments':['push','origin','main']} if mode=='run' else {'repo':str(repo),'action':'push'}
            result = tool(name,args,True)
            if mode == 'cancel':
                assert result.get('isError') or result.get('isSuccess') is False,result
            else:
                assert result.get('isSuccess'),result
        remote = git('rev-parse','refs/heads/main',cwd=bare)
        if mode == 'cancel':
            assert remote == remote_before_cancel and remote != head
        else:
            assert remote == head
        assert git('rev-parse','HEAD')==head and git('write-tree')==index and (repo/'file.txt').read_bytes()==worktree
        checks.append(mode+' HTTPS push preserves real authentication retries and local state' if mode!='cancel' else 'Cancelling HTTPS push leaves the remote and local staged/worktree data unchanged')
        print('PASS',checks[-1],flush=True)
    assert not backend_errors,backend_errors
except BaseException as error:
    failure = repr(error)
    raise
finally:
    events = Path(str(fixture)+'.events')
    (out/'results.json').write_text(json.dumps({'checks':checks,'failure':failure,'requests':requests,
        'backendErrors':backend_errors,'askpassEvents':events.read_text('utf-8').splitlines() if events.exists() else [],
        'root':str(root)},ensure_ascii=False,indent=2),'utf-8')
    credential('erase')
    p.stdin.close()
    try:
        p.wait(timeout=15)
    except subprocess.TimeoutExpired:
        p.kill();p.wait(timeout=10)
    if pid:
        literal = str(exe).replace("'","''")
        script = "$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong QA process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Owned QA GUI did not close'}};exit 0"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True,timeout=30)
    server.shutdown()

remove_fixture(root,out,'TreeGit ')
profile.close()
