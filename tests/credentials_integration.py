"""Check Windows vault isolation and real Git authentication through SourceGit's command pipeline."""
import base64
import http.server
import json
import os
from pathlib import Path
import subprocess
import ssl
import sys
import tempfile
import threading

exe = Path(sys.argv[1]).resolve()
artifacts = Path(sys.argv[2]).resolve()
artifacts.mkdir(parents=True, exist_ok=True)
git = exe.parent/'git/cmd/git.exe'
if not git.exists():
    import shutil
    git = Path(shutil.which('git'))
root = Path(tempfile.mkdtemp(prefix='SourceGit credential 中文 '))
prompt_mode = '--prompt' in sys.argv[3:]
https_mode = '--https' in sys.argv[3:]
scheme = 'https' if https_mode else 'http'
fixture = root/'askpass-fixture.json'
seen = []
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        auth = self.headers.get('Authorization','')
        credential = base64.b64decode(auth[6:]).decode() if auth.startswith('Basic ') else ''
        if credential not in ['alice:alpha-test', 'bob:beta-test', 'charlie:']:
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
if https_mode:
    openssl=git.parent.parent/'usr/bin/openssl.exe'
    if not openssl.exists():
        # MinGit ships OpenSSL libraries for transport, not the openssl CLI.
        # The fixture certificate may be generated with the test host's tool.
        import shutil
        host_git=shutil.which('git')
        openssl=Path(host_git).parent.parent/'usr/bin/openssl.exe' if host_git else Path(shutil.which('openssl') or '')
    assert openssl.exists(),openssl
    certificate=root/'localhost-cert.pem';private_key=root/'localhost-key.pem'
    generated=subprocess.run([str(openssl),'req','-x509','-newkey','rsa:2048','-nodes','-days','1',
        '-subj','/CN=localhost','-addext','subjectAltName=IP:127.0.0.1','-keyout',str(private_key),'-out',str(certificate)],capture_output=True)
    assert generated.returncode==0,generated.stderr
    context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.load_cert_chain(certificate,private_key)
    server.socket=context.wrap_socket(server.socket,server_side=True)
threading.Thread(target=server.serve_forever, daemon=True).start()
host = f'127.0.0.1:{server.server_port}'
url = f'{scheme}://{host}/repo.git'
def helper(repo, action, username='', password='', path='repo.git'):
    env = dict(os.environ, SOURCEGIT_CREDENTIAL_SCOPE=str(repo))
    content = f'protocol={scheme}\nhost={host}\npath={path}\n'
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
    if https_mode:
        env['GIT_SSL_CAINFO']=str(certificate)
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
        if prompt_mode and user=='alice':
            helper(repo,'store',user,'expired-test-secret')
            retry=call('tools/call',{'name':'git_action','arguments':{'repo':str(repo),'action':'fetch'}})
            assert not retry.get('isError'),retry
            assert 'password=alpha-test' in helper(repo,'get')
    assert seen == (['alice','alice','bob'] if prompt_mode else ['alice','bob']),seen
    if prompt_mode:
        fixture.write_text(json.dumps({'username':'alice','password':'alpha-test'}), encoding='utf-8')
        helper(repos[0],'store','alice','expired-native-test')
        native=subprocess.run([str(exe),'--integration-auth-fetch',str(repos[0]),url], env=env, capture_output=True, timeout=45)
        assert native.returncode == 0, native.stderr
        assert seen == ['alice','alice','bob','alice'],seen
        assert 'password=alpha-test' in helper(repos[0],'get')
        passwordless = root/'passwordless'
        passwordless.mkdir(); repos.append(passwordless)
        subprocess.run([str(git),'init',str(passwordless)], capture_output=True, check=True)
        subprocess.run([str(git),'-C',str(passwordless),'remote','add','origin',url], capture_output=True, check=True)
        fixture.write_text(json.dumps({'username':'charlie','password':''}), encoding='utf-8')
        opened=call('tools/call',{'name':'ui_open_repository','arguments':{'repo':str(passwordless)}});assert not opened.get('isError'),opened
        fetched=call('tools/call',{'name':'git_action','arguments':{'repo':str(passwordless),'action':'fetch'}});assert not fetched.get('isError'),fetched
        assert seen == ['alice','alice','bob','alice','charlie'],seen
        empty=helper(passwordless,'get'); assert 'username=charlie\n' in empty and 'password=\n' in empty,empty
        retries = root/'retries'
        retries.mkdir(); repos.append(retries)
        subprocess.run([str(git),'init',str(retries)], capture_output=True, check=True)
        subprocess.run([str(git),'-C',str(retries),'remote','add','origin',url], capture_output=True, check=True)
        fixture.write_text(json.dumps({'attempts':[
            {'username':'wrong-account','password':'bad-first'},
            {'username':'alice','password':'bad-second'},
            {'username':'alice','password':'alpha-test'}]}),encoding='utf-8')
        opened=call('tools/call',{'name':'ui_open_repository','arguments':{'repo':str(retries)}});assert not opened.get('isError'),opened
        fetched=call('tools/call',{'name':'git_action','arguments':{'repo':str(retries),'action':'fetch'}});assert not fetched.get('isError'),fetched
        assert seen == ['alice','alice','bob','alice','charlie','alice'],seen
        native_retries = root/'native-retries'
        native_retries.mkdir(); repos.append(native_retries)
        subprocess.run([str(git),'init',str(native_retries)], capture_output=True, check=True)
        subprocess.run([str(git),'-C',str(native_retries),'remote','add','origin',url], capture_output=True, check=True)
        fixture.write_text(json.dumps({'attempts':[
            {'username':'wrong-account','password':'native-bad-first'},
            {'username':'alice','password':'native-bad-second'},
            {'username':'alice','password':'alpha-test'}]}),encoding='utf-8')
        native=subprocess.run([str(exe),'--integration-auth-fetch',str(native_retries),url],env=env,capture_output=True,timeout=45)
        assert native.returncode == 0,native.stderr
        assert seen == ['alice','alice','bob','alice','charlie','alice','alice'],seen
        cancelled = root/'cancelled'
        cancelled.mkdir(); repos.append(cancelled)
        subprocess.run([str(git),'init',str(cancelled)], capture_output=True, check=True)
        subprocess.run([str(git),'-C',str(cancelled),'remote','add','origin',url], capture_output=True, check=True)
        fixture.write_text(json.dumps({'username':'cancelled','password':'','cancel':True}), encoding='utf-8')
        opened=call('tools/call',{'name':'ui_open_repository','arguments':{'repo':str(cancelled)}});assert not opened.get('isError'),opened
        fetched=call('tools/call',{'name':'git_action','arguments':{'repo':str(cancelled),'action':'fetch'}});assert fetched.get('isError'),fetched
        assert helper(cancelled,'get') == ''
    p.stdin.close()
    p.wait(timeout=10)
    assert p.returncode == 0
    for repo in repos:
        helper(repo,'erase')
        assert helper(repo,'get') == ''
    result={'windows_vault_roundtrip':True,'same_server_different_repository_accounts':seen,'actual_gui_git_fetch_authentication':True,'erase':True}
    if prompt_mode:
        events=Path(str(fixture)+'.events').read_text(encoding='utf-8').splitlines()
        assert events.count('combined-dialog') == 12 and events.count('topmost-combined-dialog') == 12,events
        assert events.count('visibility-toggle') == 12,events
        assert events.count('retry-message') == 6,events
        assert 'username-dialog' not in events and 'password-dialog' not in events,events
        result['automatic_missing_credential_dialog']=events
        result['expired_credential_reprompts_and_retries']=True
        result['upstream_native_fetch_reprompts_and_retries']=True
        result['all_twelve_combined_authentication_prompts_stay_topmost']=True
        result['username_and_password_in_one_window']=True
        result['empty_password_authenticates_and_is_saved']=True
        result['cancel_does_not_prompt_again_or_store_credentials']=True
        result['eye_toggle_restores_star_masking']=True
        result['two_wrong_credentials_then_success_reprompt_until_correct']=True
        result['native_fetch_two_wrong_credentials_then_success']=True
        import shutil
        shutil.copy2(str(fixture)+'.png', artifacts/'combined-authentication.png')
    else:
        result.update(username_mismatch_ignored=True,different_remote_path_isolated=True)
    result['transport']=scheme
    result['openssl_tls_with_verified_local_certificate']=https_mode
    (artifacts/('credentials-https-prompt-results.json' if https_mode and prompt_mode else 'credentials-https-results.json' if https_mode else 'credentials-prompt-results.json' if prompt_mode else 'credentials-results.json')).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print('PASS vault roundtrip, repository isolation, username/path isolation, real authenticated fetch for alice/bob, erase',flush=True)
finally:
    for repo in repos: helper(repo,'erase')
    server.shutdown()
