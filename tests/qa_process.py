"""Inspect and stop an exact owned CI desktop, without process-name wildcards."""
import json
import os
from pathlib import Path
import signal
import subprocess
import time


def process_sample(exe, pid):
    assert os.environ.get('CI') == 'true', 'Process checks require an owned CI application'
    exe = Path(exe).resolve()
    if os.name == 'posix':
        proc = Path('/proc') / str(pid)
        assert (proc / 'exe').resolve() == exe, 'Unexpected CI process identity'
        values = dict(line.split(':', 1) for line in (proc / 'status').read_text().splitlines() if ':' in line)
        memory = dict(line.split(':', 1) for line in (proc / 'smaps_rollup').read_text().splitlines() if ':' in line)
        private = sum(int(memory.get(key, '0 kB').split()[0]) * 1024 for key in ['Private_Clean', 'Private_Dirty', 'Private_Hugetlb'])
        return {'Id': pid, 'Path': str(exe), 'HandleCount': len(list((proc / 'fd').iterdir())),
                'PrivateMemorySize64': private, 'WorkingSet64': int(values['VmRSS'].split()[0]) * 1024,
                'memoryMetric': 'private resident bytes on Linux'}
    script = '[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); Get-Process -Id ' + str(int(pid)) + ' | Select-Object Id,Path,HandleCount,PrivateMemorySize64,WorkingSet64 | ConvertTo-Json -Compress'
    result = subprocess.run(['powershell', '-NoProfile', '-Command', script], capture_output=True, text=True, encoding='utf-8', check=True)
    sample = json.loads(result.stdout)
    assert Path(sample['Path']).resolve() == exe, 'Unexpected CI process identity'
    sample['memoryMetric'] = 'private committed bytes on Windows'
    return sample


def close_owned_gui(exe, pid):
    assert os.environ.get('CI') == 'true', 'Shutdown checks require an owned CI application'
    exe = Path(exe).resolve()
    if os.name == 'posix':
        target = Path('/proc') / str(int(pid)) / 'exe'
        if not target.exists():
            return
        assert target.resolve() == exe, 'Unexpected process; left unchanged'
        os.kill(int(pid), signal.SIGTERM)
        end = time.monotonic() + 15
        while target.exists() and time.monotonic() < end:
            time.sleep(.05)
        assert not target.exists(), 'Owned CI application did not exit'
        return
    literal = str(exe).replace("'", "''")
    script = "$p=Get-Process -Id " + str(int(pid)) + " -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '" + literal + "'){throw 'Unexpected process; left unchanged'};$null=$p.CloseMainWindow();if(-not $p.WaitForExit(15000)){$p.Kill();$p.WaitForExit()}}"
    subprocess.run(['powershell', '-NoProfile', '-Command', script], check=True, capture_output=True, timeout=30)


def owned_handle_types(exe, pid):
    """Count one verified Windows QA process's handles; never close its handles."""
    import collections
    import ctypes as c
    from ctypes import wintypes as w
    assert os.name == 'nt'
    kernel, nt = c.WinDLL('kernel32', use_last_error=True), c.WinDLL('ntdll')
    class Entry(c.Structure):
        _fields_ = [('object', c.c_void_p), ('pid', c.c_size_t), ('handle', c.c_size_t),
                    ('access', w.ULONG), ('trace', w.USHORT), ('type', w.USHORT), ('attributes', w.ULONG), ('reserved', w.ULONG)]
    class Unicode(c.Structure):
        _fields_ = [('length', w.USHORT), ('maximum', w.USHORT), ('buffer', c.c_void_p)]
    nt.NtQuerySystemInformation.argtypes = [w.ULONG, c.c_void_p, w.ULONG, c.POINTER(w.ULONG)]
    nt.NtQuerySystemInformation.restype = w.LONG
    nt.NtQueryObject.argtypes = [w.HANDLE, w.ULONG, c.c_void_p, w.ULONG, c.POINTER(w.ULONG)]
    nt.NtQueryObject.restype = w.LONG
    kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
    kernel.OpenProcess.restype = w.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [w.HANDLE, w.DWORD, w.LPWSTR, c.POINTER(w.DWORD)]
    kernel.DuplicateHandle.argtypes = [w.HANDLE, w.HANDLE, w.HANDLE, c.POINTER(w.HANDLE), w.DWORD, w.BOOL, w.DWORD]
    kernel.CloseHandle.argtypes = [w.HANDLE]
    kernel.GetFileType.argtypes = [w.HANDLE]
    kernel.GetFileType.restype = w.DWORD
    process = kernel.OpenProcess(0x1040, False, int(pid))
    assert process, c.WinError(c.get_last_error())
    try:
        image, length = c.create_unicode_buffer(32768), w.DWORD(32768)
        assert kernel.QueryFullProcessImageNameW(process, 0, image, c.byref(length))
        assert Path(image.value).resolve() == Path(exe).resolve(), 'Unexpected QA process; handles left unchanged'
        needed, size = w.ULONG(), 2 ** 20
        while True:
            assert size <= 64 * 2 ** 20
            data = c.create_string_buffer(size)
            status = nt.NtQuerySystemInformation(64, data, size, c.byref(needed))
            if status == 0: break
            assert status & 0xffffffff == 0xc0000004, hex(status & 0xffffffff)
            size = max(size * 2, needed.value + 4096)
        count = c.c_size_t.from_buffer(data).value
        assert 2 * c.sizeof(c.c_size_t) + count * c.sizeof(Entry) <= size
        entries = (Entry * count).from_buffer(data, 2 * c.sizeof(c.c_size_t))
        counts = collections.Counter(e.type for e in entries if e.pid == pid)
        names, file_kinds = {}, collections.Counter()
        for entry in entries:
            if entry.pid != pid or (entry.type in names and names[entry.type] != 'File'): continue
            duplicate = w.HANDLE()
            if not kernel.DuplicateHandle(process, w.HANDLE(entry.handle), w.HANDLE(-1), c.byref(duplicate), 0, False, 2): continue
            try:
                if entry.type not in names:
                    value = c.create_string_buffer(4096)
                    if nt.NtQueryObject(duplicate, 2, value, len(value), c.byref(needed)) == 0:
                        text = Unicode.from_buffer(value)
                        names[entry.type] = c.wstring_at(text.buffer, text.length // 2)
                if names.get(entry.type) == 'File':
                    # Windows also calls active Git stdin/stdout pipes "File".
                    # Classify without querying pipe names (which can block).
                    file_kinds[{1: 'DiskFile', 2: 'CharacterFile', 3: 'PipeFile'}.get(kernel.GetFileType(duplicate), 'OtherFile')] += 1
            finally:
                kernel.CloseHandle(duplicate)
        assert 'File' in names.values(), 'File handle accounting is unavailable'
        return {**{names.get(key, str(key)): value for key, value in counts.items()}, **file_kinds}
    finally:
        kernel.CloseHandle(process)
