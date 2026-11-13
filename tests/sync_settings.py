"""Real ahead/behind states, compact settings, and shared maximize/restore behavior."""
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit sync settings '));repo=root/'TreeGit';peer=root/'Peer';remote=root/'Remote.git'
git=shutil.which('git');checks=[]
def run(*args,cwd=None):
    p=subprocess.run([git,*args],cwd=cwd,capture_output=True,text=True,encoding='utf-8');assert p.returncode==0,p.stderr
    return p.stdout.strip()
run('init','--bare',str(remote));run('init','-b','main',str(repo))
def identity(folder):
    run('config','user.name','TreeGit QA',cwd=folder);run('config','user.email','qa@example.invalid',cwd=folder)
identity(repo);(repo/'README.txt').write_text('Initial\n');run('add','.',cwd=repo);run('commit','-m','Initial',cwd=repo)
run('remote','add','origin',str(remote),cwd=repo);run('push','-u','origin','main',cwd=repo)
run('--git-dir='+str(remote),'symbolic-ref','HEAD','refs/heads/main');run('clone',str(remote),str(peer));identity(peer)
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW);seq=0
def rpc(method,params={}):
    global seq
    seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush()
    result=json.loads(p.stdout.readline());assert 'error' not in result,result
    return result['result']
def tool(name,args={}):
    r=rpc('tools/call',{'name':name,'arguments':args});assert not r.get('isError'),r
    return base64.b64decode(r['content'][0]['data']) if name=='ui_screenshot' else json.loads(r['content'][0]['text'])
def check(name,ok=True):
    assert ok,name
    checks.append(name);print('PASS',name,flush=True)
def state():return next(r for r in tool('ui_state')['repositories'] if Path(r['repo'])==repo)
def wait_graph(revisions):
    expected=set(run('rev-list',*revisions,cwd=repo).splitlines())
    for _ in range(100):
        s=state()
        if not s['graphLoading'] and {c['sha'] for c in s['graphCommits']}==expected:return s
        time.sleep(.1)
    raise AssertionError(s)

def head_badge(s):
    head=run('rev-parse','HEAD',cwd=repo)
    return next(c for c in s['graphCommits'] if c['sha']==head)

def wait_counts(a,b):
    for _ in range(100):
        s=state()
        if s['ahead']==a and s['behind']==b:return
        time.sleep(.1)
    raise AssertionError(s)
rpc('initialize',{'protocolVersion':'2025-11-25'});tool('ui_open_repository',{'repo':str(repo)});tool('ui_window',{'theme':'Blue','width':1440,'height':900});wait_counts(0,0)
check('synced branch has no pending counts')
(repo/'local.txt').write_text('Local\n');run('add','.',cwd=repo);run('commit','-m','Local change',cwd=repo);tool('ui_refresh',{'repo':str(repo)});wait_counts(1,0)
check('un-pushed local commit reports one outgoing change');(out/'outgoing.png').write_bytes(tool('ui_screenshot'))
(peer/'peer.txt').write_text('Peer\n');run('add','.',cwd=peer);run('commit','-m','Remote change',cwd=peer);run('push',cwd=peer)
tool('git_action',{'repo':str(repo),'action':'fetch'});wait_counts(1,1)
check('fetched remote commit reports both incoming and outgoing changes');(out/'diverged.png').write_bytes(tool('ui_screenshot'))
tool('ui_graph',{'repo':str(repo),'mode':'current','showRemotes':True});s=wait_graph(['main','origin/main'])
check('current mode includes the upstream history when remote branches are shown',any('origin/main' in c['refs'] for c in s['graphCommits']))
check('local tip carries the same outgoing and incoming counts',head_badge(s)['tracking']==[{'branch':'main','status':'1↑ 1↓'}])
check('local tip is marked for bold rendering',head_badge(s)['localHead'])
tool('ui_graph',{'repo':str(repo),'showRemotes':False});s=wait_graph(['main'])
check('turning remote branches off hides upstream-only commits and labels',all(not any(ref.startswith('origin/') for ref in c['refs']) for c in s['graphCommits']))
tool('ui_graph',{'repo':str(repo),'showRemotes':True});wait_graph(['main','origin/main'])
(out/'current-with-remote.png').write_bytes(tool('ui_screenshot'))

tool('git_run',{'repo':str(repo),'arguments':['merge','--no-edit','origin/main']});wait_counts(2,0)
check('merging incoming commits leaves outgoing merge history')
tool('git_action',{'repo':str(repo),'action':'push'});wait_counts(0,0)
check('push clears synchronized counts')
run('pull','--ff-only',cwd=peer)
(peer/'incoming.txt').write_text('Incoming only\n');run('add','.',cwd=peer);run('commit','-m','Incoming update',cwd=peer);run('push',cwd=peer)
run('checkout','-b','feature/unrelated',cwd=peer)
(peer/'unrelated.txt').write_text('Unrelated branch\n');run('add','.',cwd=peer);run('commit','-m','Unrelated remote branch',cwd=peer);run('push','-u','origin','feature/unrelated',cwd=peer);run('checkout','main',cwd=peer)
tool('git_action',{'repo':str(repo),'action':'fetch'});wait_counts(0,1);s=wait_graph(['main','origin/main'])
for _ in range(100):
    s=state()
    if head_badge(s)['tracking']==[{'branch':'main','status':'1↓'}]:break
    time.sleep(.1)
check('incoming-only update has the same pull count on the local tip',head_badge(s)['tracking']==[{'branch':'main','status':'1↓'}])
check('current mode hides unrelated remote branches',all('origin/feature/unrelated' not in c['refs'] for c in s['graphCommits']))
remote=next(c for c in s['graphCommits'] if 'origin/main' in c['refs'])
check('remote-only tip is not treated as a local bold tip',not remote['localHead'])
(out/'incoming-current.png').write_bytes(tool('ui_screenshot'))
tool('ui_graph',{'repo':str(repo),'mode':'all'});s=wait_graph(['--all'])
check('all mode includes unrelated remote branches',any('origin/feature/unrelated' in c['refs'] for c in s['graphCommits']))
tool('ui_graph',{'repo':str(repo),'mode':'current'});wait_graph(['main','origin/main'])

before=run('config','--local','--list',cwd=repo)
tool('ui_command',{'repo':str(repo),'command':'repository_settings'});time.sleep(.4)
check('unified repository settings opens',any(w['type']=='RepositorySettings' for w in tool('ui_state')['dialogs']))
(out/'settings-remotes.png').write_bytes(tool('ui_screenshot',{'window':'active'}))
tool('ui_command',{'command':'repository_settings_advanced'});time.sleep(.3)
(out/'settings-advanced.png').write_bytes(tool('ui_screenshot',{'window':'active'}))
check('advanced settings page is reachable')
tool('ui_command',{'command':'close_dialog'})
check('closing settings without confirmation preserves Git configuration',before==run('config','--local','--list',cwd=repo))
tool('ui_window',{'state':'normal'})
check('shared title-bar action maximizes',tool('ui_window',{'state':'toggle'})['state']=='Maximized')
check('shared title-bar action restores',tool('ui_window',{'state':'toggle'})['state']=='Normal')
p.stdin.close();p.wait(timeout=10)
(out/'sync-settings-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'repo':str(repo),'exe':str(exe)},ensure_ascii=False,indent=2),encoding='utf-8')
