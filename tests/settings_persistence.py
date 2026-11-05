"""Edit real repository-settings controls; verify cancellation and write failures on disk."""
import base64
import json
import os
from pathlib import Path
import queue
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit settings audit '))
repo = root / 'repo'
fixture = root / 'settings-fixture.json'
global_config = root / 'global.gitconfig'
env = os.environ.copy()
env.update(GIT_CONFIG_GLOBAL=str(global_config), GIT_CONFIG_NOSYSTEM='1', TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
checks = []
fixture_read_retries = []

def git(*args, success=True):
    p = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, text=True, encoding='utf-8', env=env)
    if success:
        assert p.returncode == 0, p.stderr
    return p.stdout.strip()

repo.mkdir()
git('init', '-b', 'main')
git('config', '--global', 'user.name', 'Global identity')
git('config', '--global', 'user.email', 'global@example.invalid')
git('config', 'user.name', 'Local identity')
git('config', 'user.email', 'local@example.invalid')
git('remote', 'add', 'origin', str(root / 'origin.git'))
git('remote', 'add', 'backup', str(root / 'backup.git'))
(repo / 'initial.txt').write_text('initial\n', encoding='utf-8')
shared_file=repo/'.issuetracker'
shared_file.write_text('[issuetracker "SHARED"]\n\tregex = "#([0-9]+)"\n\turl = "https://example.invalid/shared/$1"\n',encoding='utf-8')
git('add', '.')
git('commit', '-m', 'Initial')
settings_file = repo / '.git/sourcegit.settings'
settings_file.write_text(json.dumps({'DefaultRemote':'origin', 'PreferredMergeMode':0,
    'PreferredOpenAIService':'keep-existing-provider', 'CommitTemplates':[{'Name':'Existing', 'Content':'Original content'}]}), encoding='utf-8')

p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, encoding='utf-8', env=env, creationflags=subprocess.CREATE_NO_WINDOW)
messages = queue.Queue()
threading.Thread(target=lambda: [messages.put(line) for line in p.stdout], daemon=True).start()
seq = 0
pid = None

def rpc(method, params=None):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0', 'id':seq, 'method':method, 'params':params or {}})+'\n')
    p.stdin.flush()
    result = json.loads(messages.get(timeout=45))
    assert result.get('id') == seq and 'error' not in result, result
    return result['result']

def tool(name, args=None):
    r = rpc('tools/call', {'name':name, 'arguments':args or {}})
    assert not r.get('isError'), r
    return json.loads(r['content'][0]['text'])

def check(name, value=True):
    assert value, name
    checks.append(name)
    print('PASS', name, flush=True)

def edit(action, fields=None, templates=None, links=None, operation=None, external_link=None):
    result_file = Path(str(fixture)+'.result')
    result_file.unlink(missing_ok=True)
    fixture.write_text(json.dumps({'action':action, 'fields':fields or {}, 'templates':templates or [], 'links':links or [], 'operation':operation, 'externalLink':external_link}), encoding='utf-8')
    tool('ui_command', {'repo':str(repo), 'command':'repository_settings'})
    deadline = time.monotonic()+30
    while time.monotonic() < deadline:
        if result_file.exists():
            try:
                result = json.loads(result_file.read_text(encoding='utf-8-sig'))
            except (json.JSONDecodeError, PermissionError, FileNotFoundError) as error:
                # Only wait for the owned test reply to become readable. Never
                # repeat the save command or suppress a product-reported error.
                fixture_read_retries.append({'action':action,'error':type(error).__name__,
                                             'message':str(error)})
                time.sleep(.05)
                continue
            assert 'exception' not in result, result
            return result
        time.sleep(.05)
    raise AssertionError('Settings dialog did not finish: '+str(tool('ui_state')))

try:
    rpc('initialize', {'protocolVersion':'2025-11-25'})
    tool('ui_open_repository', {'repo':str(repo)})
    pid = tool('ui_state')['processId']
    shared_before=shared_file.read_bytes()
    result=edit('save')
    check('cold settings load the actual shared rules before background metadata is ready',{'name':'SHARED','shared':True} in result['links'])
    check('confirming unchanged settings preserves shared rules byte-for-byte',not result['staysOpen'] and shared_file.read_bytes()==shared_before)
    before_git = (repo/'.git/config').read_bytes()
    before_settings = settings_file.read_bytes()
    result = edit('cancel', {'UseGlobalIdentity':False, 'CommitterName':'Cancelled user',
        'CommitterEmail':'cancel@example.invalid', 'DefaultRemote':'backup', 'MergeMode':1,
        'HttpProxy':'http://127.0.0.1:9', 'PruneOnFetch':True, 'AutoRefresh':False,
        'BackgroundFetch':False, 'SigningKey':'Cancelled key', 'SignCommits':True,
        'SignTags':True, 'AskSubmodules':True, 'RecursiveSubmodules':False},
        [{'name':'Cancelled template', 'content':'Must not be saved'}])
    check('cancel leaves actual Git configuration byte-for-byte unchanged', (repo/'.git/config').read_bytes()==before_git)
    check('cancel leaves repository settings byte-for-byte unchanged', settings_file.read_bytes()==before_settings)
    check('redundant other-configuration button is absent', not result['hasOtherConfiguration'])
    check('useful signing, synchronization, submodule and template settings remain reachable',
        all(c in result['controls'] for c in ['SigningKey','SignCommits','SignTags','DefaultRemote','MergeMode','HttpProxy','PruneOnFetch','AskSubmodules','RecursiveSubmodules','CommitTemplates']))
    result = edit('cancel')
    check('cancelled edits do not leak into the next dialog', result['before']=={'defaultRemote':'origin','mergeMode':0,'templates':1})
    check('opening settings preserves existing provider configuration', json.loads(settings_file.read_text())['PreferredOpenAIService']=='keep-existing-provider')

    result = edit('save', {'UseGlobalIdentity':False, 'CommitterName':'Saved user',
        'CommitterEmail':'saved@example.invalid', 'DefaultRemote':'backup', 'MergeMode':1,
        'HttpProxy':'http://127.0.0.1:9', 'PruneOnFetch':True, 'AutoRefresh':False,
        'BackgroundFetch':False, 'SigningKey':'Saved key', 'SignCommits':True,
        'SignTags':True, 'AskSubmodules':True, 'RecursiveSubmodules':False},
        [{'name':'Saved template', 'content':'Summary\n\nBody'}])
    check('successful confirmation closes the dialog without an error', not result['staysOpen'] and result['error'] is None)
    check('confirmed identity is saved locally', git('config','--local','user.name')=='Saved user' and git('config','--local','user.email')=='saved@example.invalid')
    check('proxy, prune and signing switches persist in Git configuration',
        git('config','http.proxy')=='http://127.0.0.1:9' and git('config','fetch.prune')=='true' and
        git('config','commit.gpgsign')=='true' and git('config','tag.gpgsign')=='true' and git('config','user.signingkey')=='Saved key')
    saved = json.loads(settings_file.read_text())
    check('confirmed synchronization and submodule choices persist', saved['DefaultRemote']=='backup' and saved['PreferredMergeMode']==1 and
        saved['AutoRefreshEnabled'] is False and saved['BackgroundFetchEnabled'] is False and saved['AskBeforeAutoUpdatingSubmodules'] is True and saved['EnableRecursiveWhenAutoUpdatingSubmodules'] is False)
    check('confirmed templates preserve multiline content', saved['CommitTemplates']==[{'Name':'Existing','Content':'Original content'}, {'Name':'Saved template','Content':'Summary\n\nBody'}])

    result = edit('save', {'UseGlobalIdentity':True})
    check('global identity removes both local overrides', git('config','--local','--get','user.name',success=False)=='' and git('config','--local','--get','user.email',success=False)=='')
    check('effective identity falls back to isolated global configuration', git('config','user.name')=='Global identity' and git('config','user.email')=='global@example.invalid')
    result = edit('save', {'UseGlobalIdentity':True})
    check('saving global identity twice does not fail on absent local keys', not result['staysOpen'] and result['error'] is None)

    before_git = (repo/'.git/config').read_bytes()
    before_settings = settings_file.read_bytes()
    lock = repo/'.git/config.lock'
    lock.write_text('owned test lock', encoding='utf-8')
    try:
        result = edit('save', {'UseGlobalIdentity':False, 'CommitterName':'Blocked user', 'CommitterEmail':'blocked@example.invalid'})
        check('locked Git configuration keeps the dialog open with an error', result['staysOpen'] and bool(result['error']))
        check('locked configuration does not report success or alter files', (repo/'.git/config').read_bytes()==before_git and settings_file.read_bytes()==before_settings)
    finally:
        lock.unlink()

    tmp = repo/'.git/sourcegit.settings.tmp'
    tmp.mkdir()
    try:
        result = edit('save', {'DefaultRemote':'origin', 'UseGlobalIdentity':False, 'CommitterName':'Must roll back', 'CommitterEmail':'rollback@example.invalid'}, [{'name':'Blocked template','content':'Cannot save'}])
        check('unwritable repository-settings file keeps the dialog open with an error', result['staysOpen'] and bool(result['error']))
        check('failed settings write preserves the existing settings file', settings_file.read_bytes()==before_settings)
        check('failed settings write rolls back Git identity changes as well', (repo/'.git/config').read_bytes()==before_git)
    finally:
        tmp.rmdir()
    settings_file.chmod(stat.S_IREAD)
    try:
        result = edit('save', {'DefaultRemote':'origin', 'UseGlobalIdentity':False, 'CommitterName':'Atomic rollback', 'CommitterEmail':'atomic@example.invalid'},
            links=[{'name':'ROLLBACK','regex':r'#(\d+)','url':'https://example.invalid/$1','shared':True}])
        check('a failure during final replacement remains visible',result['staysOpen'] and bool(result['error']))
        check('a final settings failure restores Git configuration and issue links', (repo/'.git/config').read_bytes()==before_git and settings_file.read_bytes()==before_settings)
        check('a final settings failure also restores the shared rules file',shared_file.read_bytes()==shared_before)
        check('failed saves release all owned Git and settings locks',not any(p.exists() for p in [repo/'.git/config.lock',repo/'.issuetracker.lock',repo/'.git/sourcegit.settings.lock']))
    finally:
        settings_file.chmod(stat.S_IREAD|stat.S_IWRITE)
    result = edit('cancel')
    check('failed settings write rolls back in-memory settings and templates', result['before']=={'defaultRemote':'backup','mergeMode':1,'templates':2})
    result = edit('save', {'DefaultRemote':'origin'})
    check('saving works again after the lock is removed', not result['staysOpen'] and json.loads(settings_file.read_text())['DefaultRemote']=='origin')
    before_git = (repo/'.git/config').read_bytes()
    result = edit('save', links=[{'name':'Invalid pattern','regex':'[','url':'https://example.invalid/$1'}])
    check('invalid issue-link patterns are rejected before writing Git configuration', result['staysOpen'] and bool(result['error']) and (repo/'.git/config').read_bytes()==before_git)
    link={'name':'ISSUE','regex':r'#(\d+)','url':'https://example.invalid/$1'}
    result = edit('save', links=[link, link])
    check('duplicate issue-link names are rejected without partial writes', result['staysOpen'] and bool(result['error']) and (repo/'.git/config').read_bytes()==before_git)
    lock.write_text('owned test lock',encoding='utf-8')
    try:
        result = edit('save',links=[link])
        check('issue-link configuration errors keep the dialog open',result['staysOpen'] and bool(result['error']) and (repo/'.git/config').read_bytes()==before_git)
    finally:
        lock.unlink()
    result = edit('save',links=[link])
    check('a valid issue link persists after a previous failure',not result['staysOpen'] and git('config','issuetracker.ISSUE.regex')==r'#(\d+)' and git('config','issuetracker.ISSUE.url')=='https://example.invalid/$1')
    result=edit('save',external_link={'name':'EXTERNAL','regex':r'EXT-([0-9]+)','url':'https://example.invalid/external/$1'})
    check('confirming an open settings window preserves a newly loaded external rule',not result['staysOpen'] and git('config','issuetracker.EXTERNAL.url')=='https://example.invalid/external/$1')
    result=edit('cancel')
    check('reopening settings displays the preserved external rule',{'name':'EXTERNAL','shared':False} in result['links'])
    gc_lock=repo/'.git/gc.pid'
    gc_lock.write_text(f'{os.getpid()} {socket.gethostname()}\n',encoding='utf-8')
    try:
        command=subprocess.run(['git','-C',str(repo),'gc','--prune=now'],env=env,capture_output=True)
        assert command.returncode!=0,command.stdout+command.stderr
        result=edit('cancel',operation='cleanup')
        check('native cleanup view model reports failure when another GC holds the lock',result['operationResult'] is False)
    finally:
        gc_lock.unlink(missing_ok=True)
    result=edit('cancel',operation='cleanup')
    check('native cleanup succeeds after the GC lock is released',result['operationResult'] is True)
    (out/'settings-persistence-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe),'fixtureReadRetries':fixture_read_retries},ensure_ascii=False,indent=2),encoding='utf-8')
finally:
    (out/'fixture-read-retries.json').write_text(json.dumps(fixture_read_retries,ensure_ascii=False,indent=2),encoding='utf-8')
    p.stdin.close()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
    if pid:
        subprocess.run(['taskkill','/PID',str(pid),'/F'], capture_output=True)
