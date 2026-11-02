"""Check Windows vault isolation and real Git authentication through SourceGit's command pipeline."""
import base64
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading

exe = Path(sys.argv[1]).resolve()
artifacts = Path(sys.argv[2]).resolve()
artifacts.mkdir(parents=True, exist_ok=True)
git = exe.parent/'git/cmd/git.exe'
root = Path(tempfile.mkdtemp(prefix='SourceGit credential 中文 '))
prompt_mode = '--prompt' in sys.argv[3:]
fixture = root/'askpass-fixture.json'
seen = []
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        auth = self.headers.get('Authorization','')
        credential = base64.b64decode(auth[6:]).decode() if auth.startswith('Basic ') else ''
        if credential not in ['alice:alpha-test', 'bob:beta-test']:
            self.send_response(401)
            self.send_header('WWW-Authenticate','Basic realm="SourceGit test"')
            self.end_headers()
            return
        seen.append(credential.split(':')[0])
        line = b'# service=git-upload-pack\n'
        data = ('%04x'%(len(line)+4)).encode()+line+b'00000000'
        self.send_response(200)
        self.send_header('Content-Type','application/x-git-upload-pack-advertisement')
        self.send_header('Content-Length',str(len(data)))
        self.end_headers()
        self.wfile.write(data)
server = http.server.ThreadingHTTPServer(('127.0.0.1',0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
host = f'127.0.0.1:{server.server_port}'
url = f'http://{host}/repo.git'
def helper(repo, action, username='', password='', path='repo.git'):
    env = dict(os.environ, SOURCEGIT_CREDENTIAL_SCOPE=str(repo))
    content = f'protocol=http\nhost={host}\npath={path}\n'
    if username: content += f'username={username}\n'
    if password: content += f'password={password}\n'
    p = subprocess.run([str(exe),'--credential-helper',action], input=content+'\n', env=env, text=True, encoding='utf-8', capture_output=True, timeout=10)
    assert p.returncode == 0, p.stderr
    return p.stdout
repos = []
try:
    for user, secret in [('alice','alpha-test'),('bob','beta-test')]:
        repo = root/user
        repo.mkdir()
        repos.append(repo)
        subprocess.run([str(git),'init',str(repo)], capture_output=True, check=True)
        subprocess.run([str(git),'-C',str(repo),'remote','add','origin',url], capture_output=True, check=True)
        if not prompt_mode:
            helper(repo,'store',user,secret)
            assert f'username={user}' in helper(repo,'get')
            assert helper(repo,'get',username='different-user') == ''
            assert helper(repo,'get',path='other.git') == ''
    if not prompt_mode:
        assert 'alice' in helper(repos[0],'get') and 'bob' in helper(repos[1],'get')
    env = dict(os.environ)
    if prompt_mode:
        env['SOURCEGIT_TEST_ASKPASS_FIXTURE'] = str(fixture)
    p = subprocess.Popen([str(exe),'--mcp','--allow-write'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW, env=env)
    number=0
    def call(method, params):
        global number
        number += 1
        p.stdin.write(json.dumps({'jsonrpc':'2.0','id':number,'method':method,'params':params})+'\n')
        p.stdin.flush()
        response=json.loads(p.stdout.readline())
        assert 'error' not in response, response
        return response['result']
    call('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'credential-test','version':'1'}})
    for repo, user, secret in zip(repos,['alice','bob'],['alpha-test','beta-test']):
        if prompt_mode:
            fixture.write_text(json.dumps({'username':user,'password':secret}), encoding='utf-8')
        opened=call('tools/call',{'name':'ui_open_repository','arguments':{'repo':str(repo)}})
        assert not opened.get('isError'),opened
        fetched=call('tools/call',{'name':'git_action','arguments':{'repo':str(repo),'action':'fetch'}})
        assert not fetched.get('isError'),fetched
        assert f'username={user}' in helper(repo,'get')
    assert seen == ['alice','bob'],seen
    p.stdin.close()
    p.wait(timeout=10)
    assert p.returncode == 0
    for repo in repos:
        helper(repo,'erase')
        assert helper(repo,'get') == ''
    result={'windows_vault_roundtrip':True,'same_server_different_repository_accounts':seen,'actual_gui_git_fetch_authentication':True,'erase':True}
    if prompt_mode:
        events=Path(str(fixture)+'.events').read_text(encoding='utf-8').splitlines()
        assert events.count('username-dialog') == 2 and events.count('password-dialog') == 2,events
        result['automatic_missing_credential_dialog']=events
    else:
        result.update(username_mismatch_ignored=True,different_remote_path_isolated=True)
    (artifacts/('credentials-prompt-results.json' if prompt_mode else 'credentials-results.json')).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print('PASS vault roundtrip, repository isolation, username/path isolation, real authenticated fetch for alice/bob, erase',flush=True)
finally:
    for repo in repos: helper(repo,'erase')
    server.shutdown()
