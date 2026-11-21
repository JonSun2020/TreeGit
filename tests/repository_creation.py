"""Exercise welcome actions, real local init, and mocked GitHub creation/recovery.

The only HTTP client used by this fixture is an injected in-memory handler.
No GitHub repository or real account is created or changed.
"""
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import traceback
import xml.etree.ElementTree as ET
from qa_cleanup import new_fixture, remove_fixture, verify_process
from qa_process import close_owned_gui

exe, out = (Path(v).resolve() for v in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
root = new_fixture(out, 'TreeGit creation fixture ')
profile = exe.parent/'data/preference.json' if os.name == 'nt' else root/'config/SourceGit/preference.json'
profile.parent.mkdir(parents=True, exist_ok=True)
backup = profile.read_bytes() if profile.exists() else None
assert not backup, 'Use an isolated application profile'
env = {**os.environ, 'TREEGIT_TEST_MENU_PROBE': '1', 'XDG_CONFIG_HOME': str(root/'config'),
       'XDG_CACHE_HOME': str(root/'cache'), 'GIT_CONFIG_GLOBAL': str(root/'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1'}
profile.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','EnableAutoFetch':False,'Check4UpdatesOnStartup':False}),encoding='utf-8')
checks, records = [], []
responses = queue.Queue()
sequence = 0
process = None
pid = None
failure = None

def rpc(method, params):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':params})+'\n')
    process.stdin.flush()
    d=json.loads(responses.get(timeout=45));assert d.get('id')==sequence and 'error' not in d,d
    return d['result']

def tool(name, **args):
    d=rpc('tools/call',{'name':name,'arguments':args});assert not d.get('isError'),d
    return base64.b64decode(d['content'][0]['data']) if name=='ui_screenshot' else json.loads(d['content'][0]['text'])

def probe(action='inspect', value=0):return tool('ui_layout',action='test_menu',text=action,value=value)
def click(name, window='main'):return tool('ui_layout',window=window,action='test_click',button=name)
def settext(name,text):return tool('ui_layout',action='test_set',button=name,text=text)
def toggle(name,value):return tool('ui_layout',action='test_set',button=name,value=value)
def wait(predicate):
    for _ in range(180):
        value=predicate()
        if value:return value
        time.sleep(.08)
    raise AssertionError('Expected fixture state did not appear')
def check(label,condition,details=None):
    assert condition,(label,details)
    checks.append(label);print('PASS',label,flush=True)
def popup():return probe()['creationState']
def git(repo,*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env,stderr=subprocess.DEVNULL).decode().strip()
def welcome():
    tool('ui_command',command='new_tab');wait(lambda:tool('ui_state')['activePage']['kind']=='Welcome')
def create_form(name, github=False, scenario=0):
    if tool('ui_state')['activePage']['kind']!='Welcome':welcome()
    probe('welcome_mock',scenario);probe('welcome_action',2);wait(popup)
    settext('CreateRepositoryParent',str(root));settext('CreateRepositoryName',name)
    if github:
        toggle('CreateRepositoryGitHub',1);settext('CreateRepositoryToken','fixture-token');click('VerifyGitHubAccount')
        if scenario not in [1,8,9,10]:wait(lambda:popup()['account']=='treegit-demo')

try:
    with (out/'mcp.log').open('w',encoding='utf-8') as log:
        process=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    threading.Thread(target=lambda:[responses.put(v) for v in process.stdout],daemon=True).start()
    rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];verify_process(pid,exe)
    labels={'zh_CN':['克隆 / 创建仓库','创建'],'zh_TW':['複製 / 建立儲存庫','建立'],'en_US':['Clone / create repository','Create']}
    for locale,expected in labels.items():
        tool('ui_window',locale=locale,theme='Light')
        probe('reset');probe('enter',0);probe('press',0);probe('release',0)
        items=probe()['headers'][0]['items']
        check(locale+' File has only clone/create and quit',len(items)==2 and items[0]['text']==expected[0],items)
        probe('outside_content');before=len(tool('ui_tabs',action='list')['tabs']);probe('file_repository')
        wait(lambda:len(tool('ui_tabs',action='list')['tabs'])==before+1)
        check(locale+' File entry opens a welcome tab',tool('ui_state')['activePage']['kind']=='Welcome')
        buttons=probe()['welcomeButtons'];names=[b['name'] for b in buttons]
        check(locale+' welcome replaces terminal with create',names==['WelcomeClone','WelcomeOpen','WelcomeCreate','WelcomeGroup','WelcomeScan'],names)
        check(locale+' all five actions are on the left and vertically aligned',all(b['bounds']['x']<500 for b in buttons) and len({b['bounds']['y'] for b in buttons})==1,buttons)
        check(locale+' recessed separator leaves a gap before group',buttons[3]['bounds']['x']-buttons[2]['bounds']['right']>=20,buttons)
        (out/(locale+'-welcome.png')).write_bytes(tool('ui_screenshot'))
        # Actual command targets for all welcome buttons; cancel preserves the filesystem.
        for index,kind in [(0,'Clone'),(1,'OpenLocalRepository'),(3,'CreateGroup'),(4,'ScanRepositories')]:
            probe('welcome_action',index)
            wait(lambda:tool('ui_state')['activePage']['pendingDialog']==kind)
            click('CancelPopup')
        check(locale+' clone, open, grouping and scan all have live targets',tool('ui_state')['activePage']['kind']=='Welcome')
        create_form('cancel-'+locale,True)
        check(locale+' token verification identifies the account',popup()['account']=='treegit-demo')
        layout=tool('ui_layout');check(locale+' normal inspection never returns the token','fixture-token' not in json.dumps(layout))
        (out/(locale+'-create-github.png')).write_bytes(tool('ui_screenshot'))
        click('CancelPopup')
        check(locale+' cancelling does not create local or remote',not (root/('cancel-'+locale)).exists() and probe('welcome_mock_state')['posts']==0)
    tool('ui_window',locale='en_US')
    create_form('local-only');click('ConfirmPopup');wait(lambda:tool('ui_state')['activePage']['kind']=='Repository')
    local=root/'local-only';check('local create initializes and opens an empty repository',git(local,'rev-parse','--is-inside-work-tree')=='true')
    check('local create never contacts GitHub',probe('welcome_mock_state')['posts']==0)
    welcome();create_form('../escape');click('ConfirmPopup');time.sleep(.1)
    check('path traversal cannot create a repository',popup() is not None and not (root.parent/'escape').exists())
    click('CancelPopup')
    existing=root/'preserved';existing.mkdir();(existing/'keep.txt').write_text('keep',encoding='utf-8')
    create_form('preserved');click('ConfirmPopup');wait(lambda:popup() and popup()['error'])
    check('existing directories are never initialized or overwritten',(existing/'keep.txt').read_text()=='keep' and not (existing/'.git').exists())
    click('CancelPopup')
    create_form('created-on-github',True);click('ConfirmPopup');wait(lambda:tool('ui_state')['activePage']['kind']=='Repository')
    repo=root/'created-on-github';state=probe('welcome_mock_state')
    check('GitHub creation happens exactly once',state['posts']==1 and state['exists'],state)
    check('GitHub repositories default to private',state['isPrivate'])
    check('GitHub remote is connected automatically',git(repo,'remote','get-url','origin')=='https://github.com/treegit-demo/created-on-github.git')
    check('access token is absent from Git configuration','fixture-token' not in (repo/'.git/config').read_text())
    # The new account management entry is in repository settings, not global tools.
    probe('reset');probe('enter',2);probe('press',2);probe('release',2)
    items=probe()['headers'][2]['items']
    check('Tools contains third-party integration, MCP and SSH helpers',len(items)==3 and sum(item['name']=='ThirdPartyIntegrationMenu' for item in items)==1)
    probe('outside_content');tool('ui_command',repo=str(repo),command='repository_settings')
    wait(lambda:any(d['type']=='RepositorySettings' for d in tool('ui_state')['dialogs']))
    click('RepositorySettingsCredentials',window='active')
    wait(lambda:any(d['type']=='RepositoryCredentials' for d in tool('ui_state')['dialogs']))
    check('repository settings exposes its account manager',any(d['type']=='RepositoryCredentials' for d in tool('ui_state')['dialogs']))
    tool('ui_command',command='close_dialog');tool('ui_command',command='close_dialog')
    for scenario,name in [(1,'bad-account'),(2,'no-permission'),(5,'already-exists')]:
        welcome();create_form(name,True,scenario)
        if scenario==1:wait(lambda:popup()['error'])
        else:click('ConfirmPopup');wait(lambda:popup() and popup()['error'])
        check(name+' failure leaves local files untouched',not (root/name).exists())
        check(name+' failure stays available for correction',popup() is not None)
        if scenario==5:check('an existing remote is never silently adopted',probe('welcome_mock_state')['posts']==0 and not popup()['canConnectExisting'])
        click('CancelPopup')
    welcome();create_form('lost-response',True,3);click('ConfirmPopup');wait(lambda:popup() and popup()['error'])
    check('lost creation response does not create local files',not (root/'lost-response').exists())
    click('ConfirmPopup');wait(lambda:popup()['canConnectExisting'])
    check('retry checks remote and does not repeat POST',probe('welcome_mock_state')['posts']==1)
    # Confirm the previously created remote explicitly through its recovery button.
    # The recovery button has a stable name for the same real Click handler.
    click('ConnectCreatedRemote');click('ConfirmPopup');wait(lambda:tool('ui_state')['activePage']['kind']=='Repository')
    check('confirmed recovery completes local setup',git(root/'lost-response','remote','get-url','origin').endswith('/lost-response.git'))
    check('confirmed recovery still has exactly one remote creation',probe('welcome_mock_state')['posts']==1)
    welcome();create_form('remote-ready-local-blocked',True,6);click('ConfirmPopup');wait(lambda:popup() and popup()['error'])
    check('remote success with local failure keeps the remote and allows a different folder',probe('welcome_mock_state')['posts']==1 and popup()['canEditParentFolder'] and not popup()['canEditName'])
    check('partial success clearly says the GitHub repository is ready',any(t['text']=='The GitHub repository is ready. Local setup is not yet complete.' for t in tool('ui_layout')['labels']))
    recovery=root/'recovery';recovery.mkdir();settext('CreateRepositoryParent',str(recovery));click('ConfirmPopup')
    wait(lambda:tool('ui_state')['activePage']['kind']=='Repository')
    check('retrying local setup never repeats GitHub creation',probe('welcome_mock_state')['posts']==1 and git(recovery/'remote-ready-local-blocked','remote','get-url','origin').endswith('/remote-ready-local-blocked.git'))
    check('local failure preserves the conflicting file',(root/'remote-ready-local-blocked').read_text()=='preserve blocker')
    welcome();create_form('account-race',False,7);toggle('CreateRepositoryGitHub',1)
    settext('CreateRepositoryToken','fixture-token');click('VerifyGitHubAccount')
    check('verification blocks duplicate requests while in flight',not popup()['canVerify'])
    settext('CreateRepositoryToken','edited-token');wait(lambda:popup()['canVerify'])
    check('a stale verification result cannot select the wrong account',not popup()['account'])
    click('CancelPopup')
    for locale in ['en_US','zh_CN','zh_TW']:
        tool('ui_window',locale=locale)
        catalog=ET.parse(Path(__file__).resolve().parent.parent/'src/Resources/Locales'/(locale+'.axaml'))
        expected=next(v.text for v in catalog.getroot() if v.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Key')=='Text.TreeGit.CreateRepository.RateLimited')
        for scenario in [8,9,10]:
            welcome();create_form('limited-'+locale+'-'+str(scenario),True,scenario)
            wait(lambda:popup()['error'])
            check(locale+' rate-limit response '+str(scenario)+' gives a wait message',popup()['error']==expected,popup())
            check(locale+' rate-limit response '+str(scenario)+' preserves local data and never creates remotely',not (root/('limited-'+locale+'-'+str(scenario))).exists() and probe('welcome_mock_state')['posts']==0)
            click('CancelPopup')
except Exception as ex:
    failure=str(ex);traceback.print_exc()
    if process and process.poll() is None:
        try:(out/'failure.png').write_bytes(tool('ui_screenshot'))
        except Exception:pass
finally:
    if process:
        try:process.stdin.close()
        except Exception:pass
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
    if pid:close_owned_gui(exe,pid)
    if profile.exists():profile.unlink()
    (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'records':records,'failure':failure},ensure_ascii=False,indent=2),encoding='utf-8')
    remove_fixture(root,out,'TreeGit creation fixture ')
if failure:raise SystemExit(1)
print(json.dumps({'passed':len(checks),'failure':None,'network':'mock-only'}))
