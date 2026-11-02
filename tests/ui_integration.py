import base64
import json
from pathlib import Path
import subprocess
import sys
import time

exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve()
repo=json.loads((out/'mcp-results.json').read_text(encoding='utf-8'))['repository']
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
i=0
checks=[]
def call(method,params={}):
    global i
    i+=1
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':i,'method':method,'params':params},ensure_ascii=False)+'\n')
    p.stdin.flush()
    r=json.loads(p.stdout.readline())
    assert 'error' not in r,r
    return r['result']
def tool(name,args={},error=False):
    r=call('tools/call',{'name':name,'arguments':args})
    assert bool(r.get('isError')) == error,r
    if error:return r
    if name=='ui_screenshot':return base64.b64decode(r['content'][0]['data'])
    return json.loads(r['content'][0]['text'])
def check(name,value=True):
    assert value,name
    checks.append(name)
    print('PASS',name,flush=True)
call('initialize',{'protocolVersion':'2025-11-25'})
tool('ui_open_repository',{'repo':repo})
tool('ui_command',{'command':'clear_notifications'})
tool('ui_select_view',{'repo':repo,'view':'history'})
time.sleep(1)
for command,kind in [('mcp_connection','McpConnection'),('preferences','Preferences'),('repository_settings','RepositoryConfigure'),('credentials','RepositoryCredentials')]:
    tool('ui_command',{'command':command,'repo':repo})
    time.sleep(0.6)
    state=tool('ui_state')
    check('original/added dialog '+command,any(d['type']==kind for d in state['dialogs']))
    (out/(command+'.png')).write_bytes(tool('ui_screenshot',{'window':'active'}))
    tool('ui_command',{'command':'preferences'},error=True)
    check('modal stacking rejected: '+command)
    check('close dialog: '+command,tool('ui_command',{'command':'close_dialog'})['closed'])
for command in ['create_branch','create_tag','fetch_dialog','pull_dialog','push_dialog','stash_dialog','apply_patch']:
    tool('ui_command',{'command':command,'repo':repo})
    time.sleep(0.3)
    state=tool('ui_state')
    check('upstream popup '+command,any(r['pendingDialog'] for r in state['repositories'] if Path(r['repo'])==Path(repo)))
    check('cancel upstream popup '+command,tool('ui_command',{'command':'cancel_popup','repo':repo})['cancelled'])
tool('ui_command',{'command':'clear_notifications'})
tool('ui_select_view',{'repo':repo,'view':'working_copy'})
tool('ui_window',{'collapseBranches':True})
time.sleep(0.5)
(out/'collapsed-working-copy.png').write_bytes(tool('ui_screenshot'))
tool('ui_window',{'collapseBranches':False})
tool('ui_select_view',{'repo':repo,'view':'history'})
check('working-copy sidebar collapse and restore retains navigation')
tool('ui_window',{'width':1024,'height':720,'theme':'Light'})
time.sleep(1)
(out/'narrow.png').write_bytes(tool('ui_screenshot'))
tool('ui_window',{'width':1440,'height':900,'theme':'Dark'})
time.sleep(0.5)
(out/'dark.png').write_bytes(tool('ui_screenshot'))
tool('ui_window',{'theme':'Light'})
(out/'final.png').write_bytes(tool('ui_screenshot'))
check('unobstructed history, narrow and dark captures')
p.stdin.close();p.wait(timeout=10)
(out/'ui-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks},indent=2),encoding='utf-8')
