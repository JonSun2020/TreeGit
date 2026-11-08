"""A late file lock must offer a usable close choice without losing the live draft."""
import ctypes,json,os,queue,shutil,subprocess,sys,tempfile,threading,time
from ctypes import wintypes
from pathlib import Path
from qa_profile import IsolatedProfile
from qa_cleanup import remove_fixture, new_fixture
kernel=ctypes.WinDLL('kernel32',use_last_error=True)
kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];kernel.OpenProcess.restype=wintypes.HANDLE
kernel.QueryFullProcessImageNameW.argtypes=[wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)]
kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD];kernel.WaitForSingleObject.restype=wintypes.DWORD
kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
kernel.TerminateProcess.argtypes=[wintypes.HANDLE,wintypes.UINT]
kernel.CloseHandle.argtypes=[wintypes.HANDLE]

def hold_owned_gui(pid,exe):
    handle=kernel.OpenProcess(0x100000|0x1000|1,False,pid)
    if not handle:raise ctypes.WinError(ctypes.get_last_error())
    size=wintypes.DWORD(32768);path=ctypes.create_unicode_buffer(size.value)
    if not kernel.QueryFullProcessImageNameW(handle,0,path,ctypes.byref(size)):
        kernel.CloseHandle(handle);raise ctypes.WinError(ctypes.get_last_error())
    if Path(path.value).resolve()!=exe:
        kernel.CloseHandle(handle);raise AssertionError('Unexpected GUI executable')
    return handle
source=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True);records=[]
profile=IsolatedProfile(source,out)
for target,choice in ([('preferences','exit')] if '--exit-only' in sys.argv[3:] else [('preferences','cancel'),('draft','cancel'),('preferences','exit')]):
    name=target+'-'+choice;app=profile.reset()
    (app/'data/preference.json').write_text('{"Locale":"zh_CN","Theme":"Blue","Check4UpdatesOnStartup":false}','utf-8')
    root=new_fixture(out,prefix='TreeGit close save failure ').resolve();repo=root/'repo';repo.mkdir();fixture=root/'fixture.json'
    env=os.environ.copy();env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',TREEGIT_TEST_SETTINGS_FIXTURE=str(fixture))
    def git(*args):
        result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW);assert result.returncode==0,result.stderr
    git('init','-b','main');git('config','user.name','Close save QA');git('config','user.email','close@example.invalid');(repo/'file.txt').write_text('base\n');git('add','.');git('commit','-m','Initial')
    draft='Keep the pending draft '+name
    fixture.write_text(json.dumps({'action':'cancel','operation':'native_close_save_failure','target':target,'choice':choice,'draft':draft}),'utf-8')
    exe=app/source.name;p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,creationflags=subprocess.CREATE_NO_WINDOW)
    messages=queue.Queue();threading.Thread(target=lambda:[messages.put(s) for s in p.stdout],daemon=True).start();seq=0;pid=None;gui_handle=None;record={'case':name}
    def rpc(method,params):
        global seq
        seq+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':seq,'method':method,'params':params})+'\n');p.stdin.flush();result=json.loads(messages.get(timeout=40));assert 'error' not in result,result;return result['result']
    def tool(name,args=None):
        result=rpc('tools/call',{'name':name,'arguments':args or {}});assert not result.get('isError'),result;return json.loads(result['content'][0]['text'])
    def finish_gui(send_close):
        if send_close and kernel.WaitForSingleObject(gui_handle,0)!=0:
            literal=str(exe).replace("'","''")
            script="$p=Get-Process -Id "+str(pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+literal+"'){throw 'Wrong owned process'};$null=$p.CloseMainWindow()};exit 0"
            result=subprocess.run(['powershell','-NoProfile','-Command',script],capture_output=True)
            if result.returncode:
                (out/(name+'-exit-error.log')).write_bytes(result.stdout+b'\n'+result.stderr)
                raise RuntimeError('Owned GUI close request failed: '+result.stderr.decode('utf-8',errors='replace'))
        # Keep a handle opened while the GUI is alive. Looking up a PID after an
        # explicit exit can observe a vanished executable path or a reused PID.
        if kernel.WaitForSingleObject(gui_handle,15000)!=0:
            record['exitTimedOut']=True
            kernel.TerminateProcess(gui_handle,99);kernel.WaitForSingleObject(gui_handle,15000)
            raise AssertionError('Owned QA app did not exit normally')
        code=wintypes.DWORD()
        assert kernel.GetExitCodeProcess(gui_handle,ctypes.byref(code)) and code.value==0,code.value
        record['verifiedGuiExitCode']=code.value
    try:
        rpc('initialize',{'protocolVersion':'2025-11-25'});pid=tool('ui_state')['processId'];gui_handle=hold_owned_gui(pid,exe);tool('ui_open_repository',{'repo':str(repo)});tool('ui_command',{'repo':str(repo),'command':'repository_settings'})
        result_file=Path(str(fixture)+'.result');deadline=time.monotonic()+20
        while not result_file.exists() and time.monotonic()<deadline:time.sleep(.03)
        assert result_file.exists(),'Close-save fixture did not return';record['result']=json.loads(result_file.read_text('utf-8'));assert 'exception' not in record['result'],record
        details=record['result']['operationDetails'];assert details['visible'] and '取消' in details['message'] and '不保存并退出' in details['message'],details
        if choice=='cancel':
            assert details['draft']==draft and details['theme']=='Dark' and tool('ui_state')['processId']==pid
        finish_gui(choice=='cancel');pid=None
        saved=json.loads((app/'data/preference.json').read_text('utf-8-sig'));assert saved['Theme']==('Dark' if choice=='cancel' else 'Blue'),saved['Theme']
        if choice=='cancel':assert json.loads((repo/'.git/sourcegit.uistates').read_text('utf-8-sig'))['LastCommitMessage']==draft
        record['passed']=True;print('PASS',name,'clear choice, retained state, normal exit',flush=True)
        shutil.copy2(Path(str(fixture)+'.close.png'),out/(name+'.png'))
    finally:
        records.append(record);(out/'close-save-results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),'utf-8')
        if p.poll() is None:
            p.stdin.close()
            try:p.wait(timeout=10)
            except subprocess.TimeoutExpired:p.kill();p.wait()
        if pid:finish_gui(True)
        if gui_handle:kernel.CloseHandle(gui_handle)

    remove_fixture(root,out,'TreeGit ')

profile.close()
