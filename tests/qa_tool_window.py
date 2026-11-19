"""Close only a positively identified disposable external comparison window."""
from contextlib import contextmanager
from pathlib import Path
import ctypes as c
from ctypes import wintypes as w
import hashlib,json,os,threading,time

@contextmanager
def close_owned_comparison(executable,title_fragment):
    assert os.name=='nt' and os.environ.get('CI')=='true'
    executable=Path(executable).resolve()
    assert executable.is_file()
    if 'TreeGit external tools ' not in str(executable):
        marker=executable.parent/'.treegit-owned-tool.json'
        assert any(parent.name.startswith('TreeGit ext tmp ') for parent in executable.parents)
        assert marker.is_file() and not marker.is_symlink() and marker.stat().st_size<8192
        record=json.loads(marker.read_text('utf-8'))
        assert record['purpose']=='TreeGit disposable external comparison'
        assert record['executables'][executable.name]==hashlib.sha256(executable.read_bytes()).hexdigest()
    kernel,user=c.WinDLL('kernel32',use_last_error=True),c.WinDLL('user32',use_last_error=True)
    kernel.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];kernel.OpenProcess.restype=w.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes=[w.HANDLE,w.DWORD,w.LPWSTR,c.POINTER(w.DWORD)]
    kernel.CloseHandle.argtypes=[w.HANDLE]
    callback=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
    user.EnumWindows.argtypes=[callback,w.LPARAM]
    user.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)]
    user.GetWindowTextW.argtypes=[w.HWND,w.LPWSTR,c.c_int]
    user.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
    stopped=threading.Event();closed=[];errors=[];sent={}
    def find(hwnd,_):
        if stopped.is_set():return True
        pid=w.DWORD();user.GetWindowThreadProcessId(hwnd,c.byref(pid))
        process=kernel.OpenProcess(0x1000,False,pid.value)
        if not process:return True
        try:
            image=c.create_unicode_buffer(32768);length=w.DWORD(len(image))
            if not kernel.QueryFullProcessImageNameW(process,0,image,c.byref(length)) or Path(image.value).resolve()!=executable:return True
            title=c.create_unicode_buffer(2048);user.GetWindowTextW(hwnd,title,len(title))
            if title_fragment not in title.value:return True
            now=time.monotonic()
            if now-sent.get(hwnd,0)<1:return True
            current=w.DWORD();user.GetWindowThreadProcessId(hwnd,c.byref(current))
            if current.value==pid.value:
                assert user.PostMessageW(hwnd,0x10,0,0)
                sent[hwnd]=now
                if not closed:closed.append({'windowTitle':title.value,'normalCloseRequested':True})
        except BaseException as error:errors.append(repr(error));stopped.set()
        finally:kernel.CloseHandle(process)
        return True
    def watch():
        deadline=time.monotonic()+25
        while not stopped.wait(.05) and time.monotonic()<deadline:user.EnumWindows(callback(find),0)
    watcher=threading.Thread(target=watch,daemon=True);watcher.start()
    try:
        yield closed
        stopped.set();watcher.join(timeout=2)
        assert closed and not errors,{'closed':closed,'errors':errors}
    finally:stopped.set();watcher.join(timeout=2)
