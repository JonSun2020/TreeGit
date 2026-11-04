"""Real OpenSSH clone/fetch/push with isolated host keys and an encrypted repository key.

Requires Paramiko for the loopback-only test server; it is never part of TreeGit.
Server API: https://docs.paramiko.org/en/stable/api/server.html
"""
import json, os, queue, shlex, shutil, socket, subprocess, sys, tempfile, threading, time
from pathlib import Path
from urllib.parse import quote
import paramiko

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
plain='--plain-only' in sys.argv[3:]
trust_first_host='--trust-first-host' in sys.argv[3:]  # Requires the integration-only prompt driver.
paramiko.util.log_to_file(str(out/'ssh-server-debug.log'))
root=Path(tempfile.mkdtemp(prefix="TreeGit SSH E2E O'Brien "));bare=root/'remote.git';seed=root/'seed';fixture=root/'askpass.json'
git_exe=Path(shutil.which('git')).resolve();ssh=git_exe.parent.parent/'usr/bin/ssh.exe';keygen=ssh.with_name('ssh-keygen.exe');assert ssh.exists() and keygen.exists()
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',SSH_AUTH_SOCK='',SOURCEGIT_TEST_ASKPASS_FIXTURE=str(fixture))
env.pop('GIT_SSH_COMMAND',None)
phrase="QA key passphrase's value"
keys={name:root/(name+" 中文 private key's rsa") for name in ['qaA','qaB']}
for user,path in keys.items():
    r=subprocess.run([str(keygen),'-q','-t','rsa','-b','2048','-N',phrase if user=='qaB' and not plain else '', '-f',str(path)],capture_output=True)
    assert r.returncode==0,r.stderr
allowed={user:Path(str(path)+'.pub').read_text().split()[1] for user,path in keys.items()}
host_key=paramiko.RSAKey.generate(2048)
listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen();listener.settimeout(.5);port=listener.getsockname()[1]
known=root/'known_hosts';known.write_text(f'[127.0.0.1]:{port} {host_key.get_name()} {host_key.get_base64()}\n')
config=root/'ssh_config';config.write_text('Host treegit-qa\n  HostName 127.0.0.1\n  User qaA\n  Port '+str(port)+'\nHost *\n  StrictHostKeyChecking yes\n  UserKnownHostsFile "'+known.as_posix()+'"\n  IdentityAgent none\n  IdentitiesOnly yes\n  IdentityFile none\n  ConnectTimeout 10\n')
proxy=Path(os.environ['TREEGIT_TEST_GIT_PROXY']).resolve();assert proxy.exists()
env.update(TREEGIT_TEST_REAL_GIT=str(git_exe),TREEGIT_TEST_SSH_CONFIG=str(config),TREEGIT_TEST_SSH_TRACE=str(root/'native-ssh-commands.log'))
fixture.write_text(json.dumps({'username':'unused','password':phrase}),encoding='utf-8')
commands=[];server_errors=[];transports=[];stopped=threading.Event()

def git(where,*args,extra_env=None):
    r=subprocess.run([str(git_exe),'-C',str(where),*args],env=extra_env or env,capture_output=True,text=True,encoding='utf-8',timeout=30)
    if r.returncode:(out/'last-git-error.log').write_text(r.stdout+r.stderr,encoding='utf-8')
    assert r.returncode==0,r.stderr;return r.stdout.strip()

bare.mkdir();git(bare,'init','--bare');seed.mkdir();git(seed,'init','-b','main');git(seed,'config','user.name','SSH QA');git(seed,'config','user.email','ssh@example.invalid')
(seed/'initial.txt').write_text('initial SSH payload\n');git(seed,'add','.');git(seed,'commit','-m','Initial SSH commit');initial=git(seed,'rev-parse','HEAD');git(seed,'remote','add','origin',str(bare));git(seed,'push','origin','main');git(bare,'symbolic-ref','HEAD','refs/heads/main')

def backend(channel,command,user):
    process=None
    try:
        parts=shlex.split(command.decode('utf-8'))
        if len(parts)!=2 or parts[0] not in ['git-upload-pack','git-receive-pack']:raise ValueError('Unexpected SSH command')
        path=parts[1]
        if path.startswith('/') and len(path)>3 and path[2]==':':path=path[1:]
        if Path(path).resolve()!=bare.resolve():raise ValueError('SSH request escaped the owned test repository')
        commands.append({'user':user,'command':parts[0]})
        process=subprocess.Popen([str(git_exe),parts[0][4:],str(bare)],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        def input_stream():
            try:
                while data:=channel.recv(32768):process.stdin.write(data);process.stdin.flush()
            except (OSError,EOFError):pass
            finally:
                try:process.stdin.close()
                except OSError:pass
        def output_stream(stream,error=False):
            try:
                while data:=os.read(stream.fileno(),32768):
                    if error:channel.sendall_stderr(data)
                    else:channel.sendall(data)
            except (OSError,EOFError):pass
        threading.Thread(target=input_stream,daemon=True).start()
        outputs=[threading.Thread(target=output_stream,args=(process.stdout,),daemon=True),threading.Thread(target=output_stream,args=(process.stderr,True),daemon=True)]
        for thread in outputs:thread.start()
        code=process.wait(timeout=60)
        for thread in outputs:thread.join(timeout=5)
        channel.send_exit_status(code);channel.shutdown_write()
    except Exception as error:
        server_errors.append(str(error))
        if process and process.poll() is None:process.kill()
    finally:channel.close()

class Server(paramiko.ServerInterface):
    def __init__(self):self.user=None
    def get_allowed_auths(self,username):return 'publickey'
    def check_auth_publickey(self,username,key):
        if allowed.get(username)==key.get_base64():self.user=username;return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED
    def check_channel_request(self,kind,chanid):return paramiko.OPEN_SUCCEEDED if kind=='session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_exec_request(self,channel,command):
        threading.Thread(target=backend,args=(channel,command,self.user),daemon=True).start();return True

def connection(sock):
    transport=paramiko.Transport(sock);transports.append(transport);channels=[]
    try:
        transport.add_server_key(host_key);transport.start_server(server=Server())
        while transport.is_active() and not stopped.is_set():
            channel=transport.accept(timeout=.5)
            if channel is not None:channels.append(channel)
    except (EOFError,OSError,paramiko.SSHException) as error:
        if str(error):server_errors.append(str(error))
    finally:transport.close()

def accept():
    while not stopped.is_set():
        try:sock,_=listener.accept()
        except socket.timeout:continue
        except OSError:break
        threading.Thread(target=connection,args=(sock,),daemon=True).start()
threading.Thread(target=accept,daemon=True).start()
url=lambda user:f'ssh://{user}@127.0.0.1:{port}/'+quote(bare.as_posix(),safe='/:')
checks=[];p=None;pid=None;preferences=exe.parent/'data/preference.json';saved_preferences=preferences.read_bytes() if preferences.exists() else None

def check(name,condition):
    assert condition,name;checks.append(name);print('PASS',name,flush=True)

try:
    direct=env.copy();direct['GIT_SSH_COMMAND']=shlex.quote(ssh.as_posix())+' -F '+shlex.quote(config.as_posix())+' -i '+shlex.quote(keys['qaA'].as_posix())+' -o BatchMode=yes'
    check('loopback SSH server serves the real Git wire protocol',initial in git(seed,'ls-remote',url('qaA'),extra_env=direct))
    if trust_first_host:
        known.write_text('')
        config.write_text(config.read_text().replace('StrictHostKeyChecking yes','StrictHostKeyChecking ask'))
        fixture.write_text(json.dumps({'username':'unused','password':'yes'}),encoding='utf-8')
    # Preserve the app's exact -i argument while isolating host trust and agent settings.
    preferences.parent.mkdir(exist_ok=True)
    settings=json.loads(preferences.read_text(encoding='utf-8-sig')) if preferences.exists() else {}
    settings['GitInstallPath']=str(proxy)
    for workspace in settings.get('Workspaces',[]):workspace['RestoreOnStartup']=False;workspace['Repositories']=[]
    preferences.write_text(json.dumps(settings,ensure_ascii=False),encoding='utf-8')
    p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
    messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0
    def rpc(method,params=None):
        global seq
        seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
        r=json.loads(messages.get(timeout=90));assert 'error' not in r,r;return r['result']
    def tool(name,args=None):
        r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r;return json.loads(r['content'][0]['text'])
    rpc('initialize',{'protocolVersion':'2025-11-25'})
    clone=root/'SSH clone 中文'
    tool('git_clone',{'url':url('qaA'),'destination':str(clone),'sshKey':str(keys['qaA']),'recursive':False})
    pid=tool('ui_state')['processId'];check('TreeGit clones through real OpenSSH with a spaced apostrophe key path',git(clone,'rev-parse','HEAD')==initial)
    if trust_first_host:
        events=Path(str(fixture)+'.events')
        check('first SSH host trust is confirmed in a topmost prompt and stored only in the owned known-hosts file',host_key.get_base64() in known.read_text() and events.exists() and 'topmost-password-dialog' in events.read_text())
        events.unlink();fixture.write_text(json.dumps({'username':'unused','password':phrase}),encoding='utf-8')
    alias_clone=root/'SSH alias clone'
    tool('git_clone',{'url':'treegit-qa:'+bare.as_posix(),'destination':str(alias_clone),'sshKey':str(keys['qaA']),'recursive':False})
    check('an SSH config alias clones through standard scp syntax',git(alias_clone,'rev-parse','HEAD')==initial)
    repo_b=root/'encrypted key repo';repo_b.mkdir();git(repo_b,'init','-b','main');git(repo_b,'remote','add','origin',url('qaB'));git(repo_b,'config','remote.origin.sshkey',str(keys['qaB']))
    opened=tool('ui_open_repository',{'repo':str(repo_b)})
    (out/'second-repository-state.json').write_text(json.dumps({'opened':opened,'state':tool('ui_state')},ensure_ascii=False,indent=2),encoding='utf-8')
    tool('git_action',{'repo':str(repo_b),'action':'fetch'})
    check('a second repository authenticates with its own key' if plain else 'an encrypted repository key authenticates after the real passphrase prompt',git(repo_b,'rev-parse','origin/main')==initial)
    events=Path(str(fixture)+'.events');first_events=events.read_text() if events.exists() else ''
    if not plain:check('SSH passphrase input uses an activated topmost window','topmost-password-dialog' in first_events)
    (clone/'roundtrip.txt').write_text('actual SSH push payload\n');git(clone,'config','user.name','SSH QA');git(clone,'config','user.email','ssh@example.invalid')
    tool('git_action',{'repo':str(clone),'action':'stage','paths':['roundtrip.txt']});tool('git_action',{'repo':str(clone),'action':'commit','message':'SSH round-trip'})
    tool('git_action',{'repo':str(clone),'action':'push'});pushed=git(clone,'rev-parse','HEAD')
    check('TreeGit pushes the exact new commit through OpenSSH',git(bare,'rev-parse','main')==pushed and git(bare,'show','main:roundtrip.txt')=='actual SSH push payload')
    if plain:
        tool('git_action',{'repo':str(repo_b),'action':'fetch'})
        check('the second repository fetches the exact pushed commit',git(repo_b,'rev-parse','origin/main')==pushed)
    else:
        events.unlink(missing_ok=True);fixture.write_text(json.dumps({'username':'unused','password':'wrong test key phrase'}),encoding='utf-8')
        updated=threading.Event()
        def correct_second_prompt():
            deadline=time.monotonic()+40
            while time.monotonic()<deadline:
                if events.exists() and 'password-dialog' in events.read_text():
                    temp=fixture.with_suffix('.next');temp.write_text(json.dumps({'username':'unused','password':phrase}),encoding='utf-8');temp.replace(fixture);updated.set();return
                time.sleep(.01)
        threading.Thread(target=correct_second_prompt,daemon=True).start()
        tool('git_action',{'repo':str(repo_b),'action':'fetch'})
        retry_events=events.read_text() if events.exists() else ''
        check('a wrong SSH key phrase prompts again and then succeeds',updated.is_set() and retry_events.count('topmost-password-dialog')>=2 and git(repo_b,'rev-parse','origin/main')==pushed)
    check('distinct repositories use their configured SSH identities',any(c['user']=='qaA' and c['command']=='git-receive-pack' for c in commands) and sum(c['user']=='qaB' and c['command']=='git-upload-pack' for c in commands)>=2)
    check('all SSH Git backend commands completed without server errors',not server_errors)
finally:
    stopped.set();listener.close()
    for transport in transports:transport.close()
    if p:
        p.stdin.close()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill()
    literal=str(exe).replace("'","''")
    subprocess.run(['powershell','-NoProfile','-Command',"Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '"+literal+"' } | Stop-Process"],capture_output=True)
    if saved_preferences is None:preferences.unlink(missing_ok=True)
    else:preferences.write_bytes(saved_preferences)
    (out/'ssh-transport-results.json').write_text(json.dumps({'checks':checks,'passed':len(checks),'commands':commands,'serverErrors':server_errors,'root':str(root),'exe':str(exe),'paramiko':paramiko.__version__,'plainOnly':plain,'trustFirstHost':trust_first_host},ensure_ascii=False,indent=2),encoding='utf-8')
