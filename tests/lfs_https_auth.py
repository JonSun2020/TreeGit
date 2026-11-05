"""Exercise actual LFS HTTPS authentication, uploads and native retry through owned fixtures."""
import base64
import hashlib
import http.server
import json
import os
import queue
import shutil
import ssl
import sys
import subprocess
import tempfile
import threading
import time
from pathlib import Path

source_exe = Path(sys.argv[1]).resolve()
source = source_exe.parent
out = Path(sys.argv[2]).resolve()
app = out/'app'
shutil.copytree(source, app, ignore=shutil.ignore_patterns('data', 'log', '.treegit-update'))
(app/'data').mkdir()
(app/'data/preference.json').write_text(json.dumps({
    'Locale':'zh_CN', 'Check4UpdatesOnStartup':False,
    'GitInstallPath':str(source/'git/cmd/git.exe')}), 'utf-8')
exe = app/source_exe.name
root = Path(tempfile.mkdtemp(prefix='TreeGit HTTPS LFS authentication '))
repo = root/'repo'
repo.mkdir()
fixture = root/'askpass.json'
settings = root/'settings.json'
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'), GIT_CONFIG_NOSYSTEM='1',
           SOURCEGIT_TEST_ASKPASS_FIXTURE=str(fixture), TREEGIT_TEST_SETTINGS_FIXTURE=str(settings))

def git(*args):
    r = subprocess.run(['git','-C',str(repo),*args], env=env, capture_output=True,
                       text=True, encoding='utf-8', timeout=30)
    assert r.returncode == 0, r.stderr
    return r.stdout.strip()

git('init','-b','main')
git('config','user.name','LFS HTTPS QA')
git('config','user.email','lfs@example.invalid')
git('lfs','install','--local')
git('lfs','track','*.bin')
payload = bytes(range(256))*337
oid = hashlib.sha256(payload).hexdigest()
(repo/'firmware.bin').write_bytes(payload)
git('add','.')
git('commit','-m','Initial LFS firmware')
head = git('rev-parse','HEAD')
git('config','lfs.locksverify','false')
git('config','lfs.transfer.maxretries','0')
openssl = Path(shutil.which('git')).parent.parent/'usr/bin/openssl.exe'
certificate = root/'localhost-cert.pem'
key = root/'localhost-key.pem'
subprocess.run([str(openssl),'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
                '-subj','/CN=127.0.0.1','-addext','subjectAltName=IP:127.0.0.1',
                '-keyout',str(key),'-out',str(certificate)], check=True, capture_output=True, timeout=30)
env['GIT_SSL_CAINFO'] = str(certificate)
requests = []
uploads = []
checks = []

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        auth = self.headers.get('Authorization', '')
        credential = base64.b64decode(auth[6:]).decode() if auth.startswith('Basic ') else ''
        requests.append({'path':self.path, 'account':credential.split(':')[0],
                         'authorized':credential == 'alice:correct-lfs-test'})
        if credential != 'alice:correct-lfs-test':
            self.send_response(401)
            self.send_header('WWW-Authenticate','Basic realm="TreeGit LFS QA"')
            self.end_headers()
            return
        if self.path.endswith('/objects/batch'):
            batch = json.loads(raw)
            objects = []
            for obj in batch['objects']:
                assert obj['oid'] == oid and obj['size'] == len(payload), obj
                objects.append({'oid':oid, 'size':len(payload), 'actions':{
                    'upload':{'href':url+'/upload/'+oid,
                              'header':{'Authorization':auth}}}})
            data = json.dumps({'transfer':'basic','objects':objects}).encode()
        elif self.path.endswith('/locks/verify'):
            data = b'{"ours":[],"theirs":[]}'
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type','application/vnd.git-lfs+json')
        self.send_header('Content-Length',str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_PUT(self):
        data = self.rfile.read(int(self.headers['Content-Length']))
        assert data == payload and hashlib.sha256(data).hexdigest() == oid
        uploads.append({'oid':oid,'bytes':len(data)})
        self.send_response(200)
        self.send_header('Content-Length','0')
        self.end_headers()

server = http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(certificate,key)
server.socket = context.wrap_socket(server.socket,server_side=True)
threading.Thread(target=server.serve_forever,daemon=True).start()
host = f'127.0.0.1:{server.server_port}'
url = 'https://'+host+'/repo.git'
git('remote','add','origin',url)

def credential(action, password=None, path='repo.git'):
    helper_env = env.copy()
    helper_env['SOURCEGIT_CREDENTIAL_SCOPE'] = str(repo)
    content = 'protocol=https\nhost='+host+'\npath='+path+'\n'
    if password is not None:
        content += 'username=alice\npassword='+password+'\n'
    r = subprocess.run([str(exe),'--credential-helper',action],input=content+'\n',
                       env=helper_env,capture_output=True,text=True,encoding='utf-8',timeout=15)
    assert r.returncode == 0,r.stderr
    return r.stdout

p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,
                     creationflags=subprocess.CREATE_NO_WINDOW)
replies = queue.Queue()
threading.Thread(target=lambda:[replies.put(x) for x in p.stdout],daemon=True).start()
seq = 0
pid = None
failure = None

def rpc(method, params):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n')
    p.stdin.flush()
    result = json.loads(replies.get(timeout=75))
    assert 'error' not in result,result
    return result['result']

def tool(name, args=None, accept_error=False):
    result = rpc('tools/call',{'name':name,'arguments':args or {}})
    if result.get('isError'):
        if accept_error:
            return result
        raise AssertionError(result)
    return json.loads(result['content'][0]['text'])

try:
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    pid = tool('ui_state')['processId']
    fixture.write_text(json.dumps({'attempts':[
        {'username':'wrong-lfs','password':'bad-one'},
        {'username':'alice','password':'bad-two'},
        {'username':'alice','password':'correct-lfs-test'}]}),'utf-8')
    tool('ui_open_repository',{'repo':str(repo)})
    result = tool('git_run',{'repo':str(repo),'arguments':['lfs','push','--all','origin']},True)
    (out/'mcp-response.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),'utf-8')
    assert not result.get('isError') and result.get('isSuccess'),result
    assert uploads and git('rev-parse','HEAD') == head and git('status','--porcelain') == ''
    checks.append('MCP LFS HTTPS upload reprompts after two wrong credentials and transfers the exact bytes')
    print('PASS',checks[-1],flush=True)
    credential('store','expired-lfs-test')
    fixture.write_text(json.dumps({'username':'alice','password':'correct-lfs-test'}),'utf-8')
    settings.write_text(json.dumps({'action':'cancel','operation':'native_lfs_push'}),'utf-8')
    tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
    reply = Path(str(settings)+'.result')
    deadline = time.monotonic()+60
    native = None
    while time.monotonic()<deadline:
        try:
            native = json.loads(reply.read_text('utf-8-sig'))
            break
        except (FileNotFoundError,PermissionError,json.JSONDecodeError):
            time.sleep(.05)
    assert native and native.get('operationResult') and len(uploads)>=2,native
    checks.append('Native LFS upload replaces an expired stored credential and closes only after exact-byte transfer')
    print('PASS',checks[-1],flush=True)
    assert git('rev-parse','HEAD') == head and git('status','--porcelain') == ''
    credential('erase')
    fixture.write_text(json.dumps({'username':'cancelled-lfs','password':'','cancel':True}),'utf-8')
    count_before_cancel = len(uploads)
    cancelled = tool('git_run',{'repo':str(repo),'arguments':['lfs','push','--all','origin']},True)
    assert cancelled.get('isError') or cancelled.get('isSuccess') is False,cancelled
    assert len(uploads) == count_before_cancel and credential('get') == ''
    assert git('rev-parse','HEAD') == head and git('status','--porcelain') == ''
    checks.append('Cancelling LFS authentication returns promptly without upload, repository changes or saved credentials')
    print('PASS',checks[-1],flush=True)
except BaseException as error:
    failure = repr(error)
    raise
finally:
    events = Path(str(fixture)+'.events')
    (out/'results.json').write_text(json.dumps({'checks':checks,'failure':failure,
        'requests':requests,'uploads':uploads,'askpassEvents':events.read_text('utf-8').splitlines() if events.exists() else [],
        'root':str(root)},ensure_ascii=False,indent=2),'utf-8')
    for path in ['repo.git','repo.git/','repo.git/info/lfs','repo.git/info/lfs/','repo.git/info/lfs/objects/batch']:
        credential('erase',path=path)
        assert credential('get',path=path) == ''
    p.stdin.close()
    try:
        p.wait(timeout=15)
    except subprocess.TimeoutExpired:
        p.kill()
    if pid:
        literal=str(exe).replace("'","''")
        script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong QA process'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){throw 'Owned QA GUI did not close'}};exit 0"
        subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True,timeout=30)
    server.shutdown()
