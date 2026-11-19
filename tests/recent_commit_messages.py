"""Verify ten reachable branch messages, multiline hover, insertion and dismissal.

Runs only in an isolated integration application using routed button clicks.
"""
import base64
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from qa_cleanup import new_fixture, remove_fixture, verify_process

exe, out = (Path(v).resolve() for v in sys.argv[1:3])
out.mkdir(parents=True, exist_ok=True)
root = new_fixture(out, 'TreeGit search interaction ')
repo = root / 'demo'
repo.mkdir()
profile = exe.parent / 'data/preference.json' if os.name == 'nt' else root / 'config/SourceGit/preference.json'
profile.parent.mkdir(parents=True, exist_ok=True)
backup = profile.read_bytes() if profile.exists() else None
if backup:
    saved = json.loads(backup.decode('utf-8-sig'))
    assert not saved.get('RepositoryNodes') and all(not w.get('Repositories') for w in saved.get('Workspaces', []))
temp = new_fixture(Path(tempfile.gettempdir()), 'TreeGit search input temp ')
env = {**os.environ, 'TREEGIT_TEST_MENU_PROBE': '1', 'XDG_CONFIG_HOME': str(root / 'config'),
       'XDG_CACHE_HOME': str(root / 'cache'), 'GIT_CONFIG_GLOBAL': str(root / 'gitconfig'), 'GIT_CONFIG_NOSYSTEM': '1',
       'TMPDIR': str(temp), 'TEMP': str(temp), 'TMP': str(temp)}
checks, records = [], []
process = None
pid = None
failure = None
responses = queue.Queue()
sequence = 0


def git(*args, **kwargs):
    if isinstance(kwargs.get('input'), str):
        kwargs['input'] = kwargs['input'].encode('utf-8')
    return subprocess.check_output([shutil.which('git'), '-C', str(repo), *args], env=env, **kwargs).decode('utf-8').strip()


def rpc(method, params):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': sequence, 'method': method, 'params': params}) + '\n')
    process.stdin.flush()
    answer = json.loads(responses.get(timeout=40))
    assert answer.get('id') == sequence and 'error' not in answer, answer
    return answer['result']


def tool(name, **args):
    for attempt in range(30):
        result = rpc('tools/call', {'name': name, 'arguments': args})
        # This guard runs before raising any input event. A just-switched, occluded
        # Windows table may need another compositor frame; never send to a stale hit.
        if (name == 'ui_layout' and args.get('action') == 'test_menu' and result.get('isError') and
                result.get('content') in [[{'type': 'text', 'text': message}] for message in [
                    'The rendered hit-test scene has not caught up with the table layout.', 'Blank cell space did not receive input.']] and attempt < 29):
            time.sleep(.1)
            continue
        break
    assert not result.get('isError'), result
    return base64.b64decode(result['content'][0]['data']) if name == 'ui_screenshot' else json.loads(result['content'][0]['text'])


def probe(action='inspect', column=0):
    return tool('ui_layout', action='test_menu', text=action, value=column)


def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']).resolve() == repo)


def wait(predicate):
    for _ in range(150):
        result = predicate()
        if result:
            return result
        time.sleep(.08)
    raise AssertionError('Expected search state did not appear')


def check(name, condition, details=None):
    assert condition, (name, details)
    checks.append(name)
    print('PASS', name, flush=True)


try:
    profile.write_text(json.dumps({'Locale':'zh_CN','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False}),encoding='utf-8')
    git('init','-b','main');git('config','user.name','TreeGit Demo');git('config','user.email','demo@example.invalid')
    timeline=[]
    def commit(message,name,stamp):
        env.update(GIT_AUTHOR_DATE=f'@{stamp} +0000',GIT_COMMITTER_DATE=f'@{stamp} +0000')
        (repo/name).write_text(message+'\n',encoding='utf-8');git('add',name);git('commit','-m',message);timeline.append((stamp,message))
    commit('Initial demo','base.txt',1000000000);git('branch','feature')
    git('checkout','feature')
    for i,stamp in enumerate([20,60,100]):commit(f'Merged feature {i}\n\nFirst detail\nSecond detail','feature.txt',1000000000+stamp)
    git('checkout','main')
    for i,stamp in enumerate([10,30,50,70,90,110,130,150,170]):
        message=f'Main message {i}' + ('\n\n'+'Long multiline detail\n'*12 if i==8 else '')
        commit(message,'main.txt',1000000000+stamp)
    env.update(GIT_AUTHOR_DATE='@1000000190 +0000',GIT_COMMITTER_DATE='@1000000190 +0000');git('merge','--no-ff','feature','-m','Merge feature into main');timeline.append((1000000190,'Merge feature into main'))
    git('checkout','-b','unrelated','HEAD~1');commit('Unrelated newer history must be excluded','unrelated.txt',1000000300);timeline.pop();git('checkout','main')
    env.pop('GIT_AUTHOR_DATE',None);env.pop('GIT_COMMITTER_DATE',None)
    initial=git('rev-parse','HEAD');expected=[m.rstrip('\r\n')for _,m in sorted(timeline,reverse=True)[:10]]
    (repo/'main.txt').write_text('staged pending change\n',encoding='utf-8');git('add','main.txt')
    process=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=(out/'mcp.log').open('w',encoding='utf-8'),text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    threading.Thread(target=lambda:[responses.put(v)for v in process.stdout],daemon=True).start();rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];verify_process(pid,exe)
    tool('ui_open_repository',repo=str(repo))
    def composition():return(state().get('workingCopyState')or{}).get('composition')
    def popup():return(composition()or{}).get('recentMessages')
    def opened():return bool(composition()and composition()['recentMessagesOpen'])
    def ready():return opened()and popup()and not popup()['loading']and len(popup()['entries'])==10
    def draft(text):tool('ui_working_copy',repo=str(repo),message=text);tool('ui_select_view',repo=str(repo),view='commit');wait(lambda:composition()and composition()['commitEnabled'])
    for locale in ['zh_CN','zh_TW','en_US']:
        for theme in ['Light','Dark','Blue']:
            tool('ui_window',locale=locale,theme=theme,width=1280,height=820,zoom=1);draft('LEFT|RIGHT');probe('recent_caret',4);opened_state=probe('recent_toggle');check(locale+' '+theme+' no loading popup is ever shown',not opened_state['loadingHistoryPopupVisible']);wait(ready)
            entries=popup()['entries'];messages=[e['text']for e in entries]
            check(locale+' '+theme+' shows only the newest ten reachable messages in time order',messages==expected and all(entries[i]['time']>=entries[i+1]['time']for i in range(9)),messages)
            check(locale+' '+theme+' includes merged history and excludes unmerged branches',any(m.startswith('Merged feature')for m in messages)and not any('Unrelated' in m for m in messages))
            check(locale+' '+theme+' long histories scroll instead of escaping the window',popup()['scrollable'])
            chosen=next(i for i,m in enumerate(messages)if m.startswith('Merged feature'))
            probe('recent_hover',chosen);row=popup()['rows'][chosen];color=tool('ui_layout')['selectedBranchBackground']
            check(locale+' '+theme+' the entire multiline row uses the secondary branch hover color',row['background']==color and row['height']>=row['textHeight']+13,row)
            (out/(locale+'-'+theme+'-recent-messages.png')).write_bytes(tool('ui_screenshot'))
            selected=messages[chosen];probe('recent_choose',chosen);wait(lambda:not opened())
            check(locale+' '+theme+' selection inserts at the preserved caret and closes the popup',state()['workingCopyState']['message'].replace('\r\n','\n')==('LEFT'+selected+'|RIGHT').replace('\r\n','\n'))
    draft('unchanged');probe('recent_toggle');wait(ready);probe('recent_toggle');wait(lambda:not opened())
    check('clicking the history button again closes without changing the draft',state()['workingCopyState']['message']=='unchanged')
    probe('recent_toggle');wait(ready);probe('recent_outside');wait(lambda:not opened())
    check('clicking outside dismisses the real light-dismiss overlay',state()['workingCopyState']['message']=='unchanged')
    for _ in range(3):probe('recent_toggle');probe('recent_toggle')
    wait(lambda:not opened());check('closing while loading does not reopen an old query',not opened())
    check('reading and choosing messages never changes HEAD or the index',git('rev-parse','HEAD')==initial and git('diff','--cached','--name-only')=='main.txt')
except BaseException:
    failure = traceback.format_exc()
    if pid:
        try: (out / 'failure.png').write_bytes(tool('ui_screenshot'))
        except Exception: pass
        try: (out / 'failure-state.json').write_text(json.dumps(tool('ui_state'), ensure_ascii=False, indent=2), encoding='utf-8')
        except Exception: pass
    raise
finally:
    if pid:
        if os.name == 'posix':
            path = Path('/proc') / str(pid) / 'exe'
            if path.exists():
                assert path.resolve() == exe
                os.kill(pid, signal.SIGTERM)
                wait(lambda: not path.exists())
        else:
            command = f"$p=Get-Process -Id {pid} -ErrorAction SilentlyContinue;if($p){{if($p.Path -ne '{str(exe).replace(chr(39), chr(39)*2)}'){{throw 'Unexpected test process'}};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(5000)){{$p.Kill();$p.WaitForExit()}}}}"
            subprocess.run(['powershell', '-NoProfile', '-EncodedCommand', base64.b64encode(command.encode('utf-16-le')).decode()], check=True, capture_output=True)
    if process and process.poll() is None:
        process.stdin.close()
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
    if backup is not None: profile.write_bytes(backup)
    elif profile.exists(): profile.unlink()
    (out / 'results.json').write_text(json.dumps({'passed': len(checks), 'checks': checks, 'records': records, 'failure': failure}, ensure_ascii=False, indent=2), encoding='utf-8')
    for attempt in range(30):
        try:
            remove_fixture(root, out, 'TreeGit search interaction ')
            break
        except PermissionError:
            if attempt == 29: raise
            time.sleep(.1)
    remove_fixture(temp, temp.parent, 'TreeGit search input temp ')
print(json.dumps({'passed': len(checks), 'failure': failure}))
