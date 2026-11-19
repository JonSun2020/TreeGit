"""Recover corrupt settings without discarding the original or overwriting a locked file."""
import json, msvcrt, queue, subprocess, sys, threading
from pathlib import Path
from qa_profile import IsolatedProfile
from qa_process import close_owned_gui

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
profile = IsolatedProfile(exe, out)
records = []
def run_case(case, original):
    data = profile.reset()/'data'; preference = data/'preference.json'; preference.write_text(original,'utf-8')
    (data/'sentinel.txt').write_text('keep user files','utf-8')
    held = None
    if case == 'locked':
        held = preference.open('r+b'); msvcrt.locking(held.fileno(), msvcrt.LK_NBLCK, 1)
    p = subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    replies = queue.Queue(); threading.Thread(target=lambda:[replies.put(line) for line in p.stdout],daemon=True).start(); seq = 0; pid = None
    def rpc(method, params):
        nonlocal seq
        seq += 1; p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n'); p.stdin.flush()
        response = json.loads(replies.get(timeout=40)); assert 'error' not in response, response
        return response['result']
    def tool(name, args=None):
        result = rpc('tools/call',{'name':name,'arguments':args or {}}); assert not result.get('isError'),result
        return json.loads(result['content'][0]['text'])
    def close_gui():
        if pid:close_owned_gui(exe,pid)
    try:
        rpc('initialize',{'protocolVersion':'2025-11-25'})
        state = tool('ui_state'); pid = state['processId']; assert state['application']=='TreeGit'
        if held:
            tool('ui_window',{'theme':'Dark'})
            msvcrt.locking(held.fileno(),msvcrt.LK_UNLCK,1); held.close(); held=None
            assert preference.read_text('utf-8') == original
        else:
            backups=list(data.glob('preference-recovery-*.json'))
            assert len(backups)==1 and backups[0].read_text('utf-8')==original
        p.stdin.close();p.wait(timeout=10);close_gui();pid=None
        assert (data/'sentinel.txt').read_text('utf-8')=='keep user files'
        if case=='locked':assert preference.read_text('utf-8')==original
        else:assert isinstance(json.loads(preference.read_text('utf-8-sig')),dict)
        records.append({'case':case,'opened':True,'originalPreserved':True,'settingsSaved':case!='locked'})
        (out/'preferences-recovery-results.json').write_text(json.dumps(records,indent=2),'utf-8')
        print('PASS',case,'settings recovery and preservation',flush=True)
    finally:
        if held:msvcrt.locking(held.fileno(),msvcrt.LK_UNLCK,1);held.close()
        if p.poll() is None:
            if not p.stdin.closed:p.stdin.close()
            try:p.wait(timeout=10)
            except subprocess.TimeoutExpired:p.kill();p.wait()
        if pid:close_gui()
try:
    for case, original in [('null','null'), ('malformed','{"Locale":'), ('layout-null','{"Layout":null}'), ('workspaces-null','{"Workspaces":null}'), ('nested-null','{"RepositoryNodes":[{"Id":"folder","SubNodes":[null]}]}'), ('locked',json.dumps({'Locale':'zh_CN','Theme':'Blue','Check4UpdatesOnStartup':False}))]:
        run_case(case, original)
finally:
    (out/'preferences-recovery-results.json').write_text(json.dumps(records,indent=2),'utf-8')
    profile.close()
