"""Expand and validate actual nested dialogs at each zoom; inspect buttons and screen bounds."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=new_fixture(Path(tempfile.gettempdir()),'TreeGit nested dialog audit ');repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
def git(*args):
    r=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8');assert r.returncode==0,r.stderr
git('init','-b','main');git('config','user.name','Modal QA');git('config','user.email','modal@example.invalid')
redirect_only='--redirect-only' in sys.argv[3:]
input_only='--input-errors-only' in sys.argv[3:]
credential_dropdown_only='--credential-dropdown-only' in sys.argv[3:]
isolated=IsolatedProfile(exe,out) if credential_dropdown_only else None
server=None
if redirect_only:
    class Redirect(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.headers.get('Authorization'):self.server.credentials_sent=True
            if '.git/info/refs' in self.path:
                self.send_response(401);self.send_header('WWW-Authenticate','Basic realm="Owned TreeGit audit"')
            else:
                time.sleep(.7)  # Keep the remote lookup pending through the dialog's first frame.
                self.send_response(301);self.send_header('Location',self.path.replace('/info/refs','.git/info/refs'))
            self.send_header('Content-Length','0');self.end_headers()
        def log_message(self,*args):pass
    server=ThreadingHTTPServer(('127.0.0.1',0),Redirect);server.credentials_sent=False
    threading.Thread(target=server.serve_forever,daemon=True).start()
    git('remote','add','origin','http://127.0.0.1:'+str(server.server_port)+'/owned-audit')
elif credential_dropdown_only:
    git('remote','add','origin','http://127.0.0.1:1/owned-audit')
    git('remote','add','backup','http://127.0.0.1:1/owned-backup')
else:git('remote','add','origin','https://example.invalid/owned-audit.git')
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
messages=queue.Queue();threading.Thread(target=lambda:[messages.put(line) for line in p.stdout],daemon=True).start();seq=0;pid=None;records=[];failures=[]
def rpc(method,params=None):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params or {}})+'\n');p.stdin.flush()
    r=json.loads(messages.get(timeout=45));assert 'error' not in r,r
    return r['result']
def tool(name,args=None):
    r=rpc('tools/call',{'name':name,'arguments':args or {}});assert not r.get('isError'),r
    return json.loads(r['content'][0]['text'])
def check(name,condition):
    print(('PASS ' if condition else 'FAIL ')+name,flush=True)
    if not condition:failures.append(name)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});pid=tool('ui_state')['processId']
    if credential_dropdown_only:
        verify_process(pid,exe)
        for locale,theme in [('zh_CN','Light'),('zh_TW','Dark'),('en_US','Blue')]:
            tool('ui_window',{'zoom':1.5,'locale':locale,'theme':theme})
            result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
            fixture.write_text(json.dumps({'action':'cancel','operation':'credential_dropdown_scroll'}),'utf-8')
            tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
            deadline=time.monotonic()+30
            while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
            assert result_file.exists(),'Credential dropdown probe timed out'
            result=json.loads(result_file.read_text('utf-8'));assert 'exception' not in result,result
            details=result['operationDetails'];records.append({'locale':locale,'theme':theme,'credentialDropdown':details})
            check(locale+' credential dropdown opens from a clipped position without scrolling',details['clippedAtBottom'] and details['popupAligned'] and details['stable'])
            check(locale+' repository path has a 12 DIP top inset',details['pathTopInset']==12)
            check(locale+' SSH description flows into the preceding paragraph',details['descriptionFlows'])
            check(locale+' identity hint follows the status with a 6 DIP gap',abs(details['hintGap']-6)<.5)
            states=details['modeSnapshots']
            check(locale+' credential mode toggling preserves the window, hint and confirmation positions',
                  len(states)==3 and all(abs(s[key]-states[0][key])<.5 for s in states for key in ('height','top','hintY','confirmY')))
            check(locale+' both modes fit their status and preserve the saved configuration',
                  [s['editable'] for s in states]==[True,False,True] and
                  all(s['status'] and s['fits'] for s in states) and details['switchingPreservesConfig'])
            check(locale+' the credential eye control preserves reveal on returning to the input',details['credentialReveal'])
            check(locale+' switching credential mode preserves both typed fields',
                  all(s['preservesDraft'] for s in states))
        assert not failures,records
        raise SystemExit(0)
    for locale,zoom,theme in ([] if input_only or redirect_only or '--dropdown-only' in sys.argv[3:] else [(language,scale,theme) for language in ['zh_CN','zh_TW','en_US'] for scale in [1,1.5,2,2.5] for theme in ['Light','Dark','Blue']]):
        tool('ui_window',{'width':1280,'height':800,'zoom':zoom,'theme':theme,'locale':locale})
        for dialog in ['remote','credentials']:
            result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
            fixture.write_text(json.dumps({'action':'cancel','operation':'dialog_'+dialog}),encoding='utf-8')
            tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
            deadline=time.monotonic()+30
            while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
            assert result_file.exists(),'Dialog probe timed out'
            result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
            details=result['operationDetails'];bounds=details['bounds'] if dialog=='credentials' else details
            records.append({'dialog':dialog,'locale':locale,'zoom':zoom,'theme':theme,'result':details})
            header=bounds['header']
            check(f'{dialog} {locale} {theme} {zoom} has a centered matching header and close action',header['title']==header['windowTitle'] and abs(header['height']-28)<.1 and header['centered'] and header['closeVisible'] and header['closeEnabled'])
            shutil.copy2(str(fixture)+'.dialog.png',out/f'{dialog}-{locale}-{zoom}-{theme}.png')
            check(f'{dialog} {zoom} stays on screen',bounds['withinScreen'])
            check(f'{dialog} {zoom} keeps all footer actions visible',len(bounds['buttons'])>=2 and all(b['visible'] for b in bounds['buttons']))
            standard=[b for b in bounds['buttons'] if b['standard']]
            check(f'{dialog} {locale} {theme} {zoom} uses matching centered footer dimensions',len(standard)>=2 and all(abs(b['width']-120)<.1 and abs(b['height']-28)<.1 and b['centered'] for b in standard))
            primary=[b for b in standard if b['primary']]
            background,foreground={'Light':('#ffa7dfcb','#ff333333'),'Dark':('#ff16836d','#ffdfdfdf'),'Blue':('#ffb6d9fa','#ff333333')}[theme]
            check(f'{dialog} {locale} {theme} {zoom} confirmation follows the theme',len(primary)==1 and primary[0]['background'].lower()==background and primary[0]['foreground'].lower()==foreground)
            if dialog=='credentials':
                check(f'credentials {zoom} belongs to TreeGit without global topmost',not bounds['topmost'] and bounds['hasOwner'])
                check(f'credentials {zoom} invalid input preserves the authentication mode',details['invalidInputPreservedConfig'])
    tool('ui_window',{'zoom':1,'locale':'zh_CN'})
    scenarios=['redirect_unchanged','redirect_clear_button','redirect_clear_text','redirect_clear_cancel','redirect_empty_password','redirect_early_input'] if redirect_only else ['unchanged','empty_password','explicit_clear','clear_button','clear_text','clear_cancel','oversized','config_locked','commit_failure']
    languages=['zh_CN','zh_TW','en_US']
    cases=[] if '--dropdown-only' in sys.argv[3:] else [('oversized',locale) for locale in languages] if input_only else [(scenario,languages[i%len(languages)] if redirect_only else 'zh_CN') for i,scenario in enumerate(scenarios)]
    for scenario,locale in cases:
        window_options={'locale':locale}
        if redirect_only:window_options['theme']={'zh_CN':'Light','zh_TW':'Dark','en_US':'Blue'}[locale]
        tool('ui_window',window_options)
        if not redirect_only and scenario in ['clear_button','clear_text','clear_cancel']:
            git('remote','set-url','origin','https://embedded-user:embedded-secret@example.invalid/owned-audit.git')
        result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
        fixture.write_text(json.dumps({'action':'cancel','operation':'credential_save','case':scenario}),encoding='utf-8')
        tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        deadline=time.monotonic()+30
        while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
        assert result_file.exists(),'Credential probe timed out'
        result=json.loads(result_file.read_text(encoding='utf-8'));assert 'exception' not in result,result
        details=result['operationDetails'];records.append({'scenario':scenario,'locale':locale,'result':details})
        scenario=scenario.replace('redirect_','')
        if redirect_only:
            caption={'zh_CN':'密码或访问令牌：','zh_TW':'密碼或存取權杖：','en_US':'Password or access token:'}[locale]
            hint={'zh_CN':'密码或访问令牌（可留空）','zh_TW':'密碼或存取權杖（可留空）','en_US':'Password or access token (optional)'}[locale]
            remote_caption={'zh_CN':'远程名称：','zh_TW':'遠端名稱：','en_US':'Remote name:'}[locale]
            check(locale+' system authentication is enabled during the first credential lookup',details['modeEnabledAtOpen'] and details['stillLoadingAtOpen'])
            check(locale+' fields are editable immediately in manual mode and disabled in system mode',
                  details['fieldsMatchModeWhileLoading'] and details['fieldsEnabledAtOpen']==(scenario=='early_input'))
            check(locale+' credential labels align with inputs beneath the mode checkbox',details['inlineFields'] and 0<=details['modeToFieldsGap']<=6)
            check(locale+' the remote selector and right-aligned labels share the credential grid',
                  details['remoteAligned'] and details['rightAligned'] and details['remoteCaption']==remote_caption)
            check(locale+' token field keeps its placeholder while the label omits the saved-password hint',details['passwordCaption']==caption and details['passwordWatermark']==hint)
        if scenario=='early_input':check('a delayed credential lookup preserves the typed account and token',details['earlyInputPreserved'])
        else:check(scenario+' displays the saved account',details['storedAccountShown'])
        check(scenario+' keeps input errors localized and visible',details['localizedInputError'])
        if scenario in ('oversized','config_locked','commit_failure'):
            check(scenario+' rejected credentials keep their original input for correction',details['draftPreserved'])
        check(scenario+' closes only after confirm succeeds or cancel',details['automaticallyClosed']==(scenario not in ['oversized','config_locked','commit_failure']))
        if scenario=='unchanged':check('confirming an existing account preserves its unedited password',details['success'] and details['originalCredentialPreserved'] and not details['systemMode'])
        elif scenario=='empty_password':check('a different account can explicitly use an empty password',details['success'] and details['emptyPasswordSaved'] and details['newUsernameSaved'] and not details['systemMode'])
        elif scenario=='explicit_clear':check('typing and clearing a password saves an intentional empty password',details['success'] and details['emptyPasswordSaved'] and not details['systemMode'])
        elif scenario in ['clear_button','clear_text']:check(scenario+' can save empty credentials and disable system fallback',details['cleared'] and not details['systemMode'])
        elif scenario=='clear_cancel':check('cancelling a clear preserves the saved account and mode',details['originalCredentialPreserved'] and details['configurationUnchanged'] and details['systemMode'])
        elif scenario=='early_input':check('confirming saves the account and token typed during lookup',details['success'] and details['earlyInputSaved'] and not details['systemMode'])
        else:check(scenario+' leaves both original credentials and authentication mode unchanged',not details['success'] and details['configurationUnchanged'] and details['originalCredentialPreserved'] and details['systemMode'])
        if not redirect_only and scenario in ['clear_button','clear_text','clear_cancel']:
            url=subprocess.run(['git','-C',str(repo),'remote','get-url','origin'],env=env,capture_output=True,text=True,check=True).stdout
            check(scenario+' handles credentials embedded in the remote URL',('embedded-secret' in url)==(scenario=='clear_cancel'))
            git('remote','set-url','origin','https://example.invalid/owned-audit.git')
    if server:check('redirect address resolution never sends saved credentials',not server.credentials_sent)
    for locale in ([] if input_only or redirect_only else ['zh_CN','zh_TW','en_US']):
        for theme in ['Light','Dark','Blue']:
            tool('ui_window',{'zoom':1.5,'locale':locale,'theme':theme})
            result_file=Path(str(fixture)+'.result');result_file.unlink(missing_ok=True)
            fixture.write_text(json.dumps({'action':'cancel','operation':'settings_dropdown_scroll'}),'utf-8')
            tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
            deadline=time.monotonic()+30
            while not result_file.exists() and time.monotonic()<deadline:time.sleep(.05)
            assert result_file.exists(),'Settings dropdown probe timed out'
            result=json.loads(result_file.read_text('utf-8'));assert 'exception' not in result,result
            records.append({'locale':locale,'theme':theme,'dropdowns':result['operationDetails']})
            for dropdown in result['operationDetails']:
                check(locale+' '+theme+' '+dropdown['dropdown']+' keeps its right-aligned label, scroll offset and popup anchor',dropdown['stable'] and dropdown['rightAligned'])
                if not dropdown['stable']:print(json.dumps(dropdown),flush=True)
    assert not failures,failures
finally:
    if server:server.shutdown();server.server_close()
    (out/'modal-bounds-results.json').write_text(json.dumps({'records':records,'failures':failures,'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill()
    if pid and credential_dropdown_only:close_owned_gui(exe,pid)
    elif pid:subprocess.run(['taskkill','/PID',str(pid),'/F'],capture_output=True)
    if isolated:isolated.close()
    remove_fixture(root,root.parent,'TreeGit nested dialog audit ')
