"""Real HTTPS Git receive-pack: wrong credentials, MCP/native push, and cancellation."""
import base64
import http.server
import json
import os
from pathlib import Path
from qa_profile import IsolatedProfile
from qa_cleanup import remove_fixture, new_fixture
from qa_process import close_owned_gui
import queue
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse
from qa_git_http import serve_git_backend

source_exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True,exist_ok=True)
profile = IsolatedProfile(source_exe, out)
app = profile.reset()
(app/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN','Check4UpdatesOnStartup':False,
    'EnableAutoFetch':False,'GitInstallPath':str(source_exe.parent/'git/cmd/git.exe')}),'utf-8')
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
    SOURCEGIT_TEST_ASKPASS_FIXTURE=str(fixture),TREEGIT_TEST_SETTINGS_FIXTURE=str(settings),TREEGIT_TEST_MENU_PROBE='1')

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
delayed_advertisement = {'armed':False,'count':0}
advertisement_waiting = threading.Event()

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
        if delayed_advertisement['armed'] and self.command == 'GET' and 'service=git-receive-pack' in parsed.query:
            delayed_advertisement['count'] += 1
            if delayed_advertisement['count'] == 2:
                advertisement_waiting.set()
                time.sleep(3)
        serve_git_backend(self, project, env, body, backend_errors)

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
    if '--operation-panels' in sys.argv[3:]:
        settings.unlink(missing_ok=True)
        def probe():return tool('ui_layout',{'action':'test_menu','text':'inspect','value':0})
        def wait_until(test):
            deadline=time.monotonic()+35
            while time.monotonic()<deadline:
                result=test()
                if result:return result
                time.sleep(.04)
            raise AssertionError('Expected operation state was not reached')
        def click(name):
            wait_until(lambda:any(b.get('name')==name and b['enabled'] for b in tool('ui_layout',{'action':'list'})['buttons']))
            return tool('ui_layout',{'action':'test_click','button':name})
        def form_ready():return any(b.get('name')=='CancelPopup' and b['enabled'] for b in tool('ui_layout',{'action':'list'})['buttons'])
        git('branch','--set-upstream-to=origin/main','main')
        git('branch','sample-branch','HEAD')
        git('update-ref','refs/remotes/origin/sample-remote',head)
        tool('ui_refresh',{'repo':str(repo)})
        def options(action):return tool('ui_layout',{'action':'test_menu','text':action,'value':0})
        def radio_color(value):
            return {'BLACK':'#FF000000','WHITE':'#FFFFFFFF','TRANSPARENT':'#00FFFFFF'}.get(value.upper(),value.upper())
        def check_radios(group, selected, ring, dot):
            assert [i for i,s in enumerate(group) if s['selected']]==[selected],group
            for i,s in enumerate(group):
                if i!=selected:
                    assert radio_color(s['dot']).startswith('#00'),s
                    continue
                assert radio_color(s['dot'])==dot and radio_color(s['ring'])==ring and ring!=dot,s
                assert s['inner']['width']<s['outer']['width'] and s['inner']['height']<s['outer']['height'],s
        blue_dot='#FF002B64'
        def check_blue_pixels(item):
            import io
            from PIL import Image
            result=rpc('tools/call',{'name':'ui_screenshot','arguments':{}})
            outer=item['outer']
            with Image.open(io.BytesIO(base64.b64decode(result['content'][0]['data']))) as image:
                glyph=image.crop((round(outer['x']),round(outer['y']),round(outer['right']),round(outer['y']+outer['height']))).convert('RGB')
            assert glyph.getpixel((glyph.width//2,glyph.height//2))==tuple(bytes.fromhex(blue_dot[-6:]))
            assert any(rgb==(0,120,215) for _,rgb in glyph.getcolors()),'Missing blue ring around the deep blue dot'
        for theme,fill,ink in [('Light','#FFA7E1A7','#FF123E34'),('Dark','#FF31743A','#FFFFFFFF'),('Blue','#FF0078D7','#FFFFFFFF')]:
            tool('ui_window',{'theme':theme})
            for form in ['stash_dialog','pull_dialog']:
                tool('ui_command',{'repo':str(repo),'command':form})
                wait_until(form_ready)
                field=wait_until(lambda:s if (s:=options('closed_combo_focus'))['visible'] else None)
                assert field['closed'] and field['focused'] and field['fill'].upper() in ['TRANSPARENT','#00FFFFFF','#00000000'],(theme,form,field)
                assert field['border']==field['themeBorder'] and field['surface']==field['themeSurface'],(theme,form,field)
                checks.append(theme+' '+form+' initially focused field uses the normal surface and a themed outline')
                if form=='stash_dialog':click('CancelPopup')
            for action in ['pull_remote_choices','pull_branch_choices']:
                options(action)
                item=wait_until(lambda:s if (s:=options('pull_selected_colors'))['background'] else None)
                foreground=item['foreground'].upper()
                assert item['background'].upper()==fill and ('#FFFFFFFF' if foreground=='WHITE' else foreground)==ink,(theme,action,item)
                checks.append(theme+' '+action+' uses the theme selection fill and text')
            click('CancelPopup')
            radio_forms=[('pull_dialog',{}),('checkout_dialog',{'to':'sample-branch'}),('checkout_dialog',{'to':'origin/sample-remote'}),
                         ('checkout_fast_forward_dialog',{'base':'sample-branch','to':'origin/main'}),('checkout_commit_dialog',{'revision':head}),
                         ('create_branch',{}),('create_tag',{}),('apply_patch',{})]
            normal_dot={'Light':'#FF195F69','Dark':'#FF68C1CB','Blue':blue_dot}[theme]
            focus_dot=blue_dot if theme=='Blue' else ink
            for form,fields in radio_forms:
                tool('ui_command',{'repo':str(repo),'command':form,**fields})
                if form=='create_branch':
                    tool('ui_command',{'repo':str(repo),'command':'create_branch_options','checkoutAfterCreated':True})
                wait_until(form_ready)
                def radios(action='radio_inspect',choice=0):
                    return tool('ui_layout',{'action':'test_menu','text':action,'value':choice})['items']
                states=radios()
                selected=next(i for i,s in enumerate(states) if s['selected'])
                check_radios(states,selected,fill,focus_dot if states[selected]['focused'] else normal_dot)
                if theme=='Blue' and form=='pull_dialog':check_blue_pixels(states[selected])
                for offset in range(1,len(states)):
                    other=(selected+offset)%len(states)
                    group=radios('radio_select',other)
                    check_radios(group,other,fill,focus_dot if group[other]['focused'] else normal_dot)
                group=radios('radio_hover',other)
                assert group[other]['hovered'],(theme,form,group)
                check_radios(group,other,fill,focus_dot if group[other]['focused'] else normal_dot)
                if theme=='Blue' and form=='pull_dialog':check_blue_pixels(group[other])
                group=radios('radio_focus',other)
                assert group[other]['focused'],(theme,form,group)
                check_radios(group,other,fill,focus_dot)
                if theme=='Blue':check_blue_pixels(group[other])
                click('CancelPopup')
                checks.append(theme+' '+form+' '+str(fields.get('to',''))+' radio choices')
        for command in ['push_dialog','fetch_dialog','pull_dialog']:
            credential('erase')
            fixture.write_text(json.dumps({'username':'alice','password':'correct-git-test','delayMs':1500}),'utf-8')
            event_file=Path(str(fixture)+'.events');before_events=len(event_file.read_text('utf-8'))
            tool('ui_command',{'repo':str(repo),'command':command});click('ConfirmPopup')
            wait_until(lambda:':request-get' in event_file.read_text('utf-8')[before_events:])
            state=probe();assert not state['operationPanels'] and state['pageDialogs'],state
            checks.append(command+' verifies credentials before showing the operation panel')
            complete=wait_until(lambda:next((g for g in probe()['operationPanels'] if g['closeEnabled'] and g['selectableText'] and g['progress']==100),None))
            assert not complete['cancelEnabled'] and complete['lines']>0 and complete['progress']==100 and not complete['indeterminate'],complete
            assert 'password=' not in complete['selectableText'] and 'correct-git-test' not in complete['selectableText'],complete
            assert git('rev-parse','HEAD')==head and git('write-tree')==index and (repo/'file.txt').read_bytes()==worktree
            if command=='push_dialog':assert git('rev-parse','refs/heads/main',cwd=bare)==head
            click('CloseOperation');wait_until(lambda:not probe()['operationPanels'] and not probe()['pageDialogs'])
            checks.append(command+' keeps the result until close and preserves the index/worktree')
            tool('ui_select_commit',{'repo':str(repo),'revision':head})
            wait_until(lambda:any(r['selectedCommits']==[head] and r['historySelectedCommits']==[head] and r['detailCommit']==head for r in tool('ui_state',{})['repositories']))
            checks.append(command+' leaves the selected history row and its details in agreement')
        credential('erase')
        fixture.write_text(json.dumps({'username':'cancelled','password':'','cancel':True,'delayMs':500}),'utf-8')
        cancel_event_start=len(event_file.read_text('utf-8'))
        remote_before_cancel=git('rev-parse','refs/heads/main',cwd=bare)
        tool('ui_command',{'repo':str(repo),'command':'push_dialog'});click('ConfirmPopup')
        wait_until(lambda:':window-closed-False-False' in event_file.read_text('utf-8')[cancel_event_start:])
        wait_until(form_ready)
        assert not probe()['operationPanels'] and probe()['pageDialogs']
        assert git('rev-parse','refs/heads/main',cwd=bare)==remote_before_cancel
        click('CancelPopup');checks.append('Cancelling preflight authentication never shows the operation panel or starts a push')
        credential('store','correct-git-test');fixture.write_text(json.dumps({'username':'alice','password':'correct-git-test'}),'utf-8')
        delayed_advertisement.update(armed=True,count=0);advertisement_waiting.clear()
        tool('ui_command',{'repo':str(repo),'command':'push_dialog'});click('ConfirmPopup')
        wait_until(lambda:advertisement_waiting.is_set())
        active=probe()['operationPanels'][0];assert active['cancelEnabled'] and not active['closeEnabled'],active
        click('CancelOperation');wait_until(lambda:probe()['pageDialogs'] and not probe()['operationPanels'])
        delayed_advertisement['armed']=False
        time.sleep(3.2)
        assert git('rev-parse','refs/heads/main',cwd=bare)==remote_before_cancel
        assert git('rev-parse','HEAD')==head and git('write-tree')==index and (repo/'file.txt').read_bytes()==worktree
        click('CancelPopup');checks.append('Cancelling an active HTTPS operation returns to its form and preserves real local/remote state')
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
    if pid:close_owned_gui(exe,pid)
    server.shutdown()

remove_fixture(root,out,'TreeGit ')
profile.close()
