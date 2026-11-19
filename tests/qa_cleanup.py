"""Remove a positively identified, owned QA fixture without traversing links."""
import os
import atexit
import tempfile
from pathlib import Path
import stat


def verify_process(pid, executable):
    """Verify the exact owned GUI before a fixture changes or closes it."""
    expected = Path(executable).resolve()
    if os.name != 'nt':
        assert (Path('/proc') / str(pid) / 'exe').resolve() == expected
        return
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x1000, False, pid)
    assert handle, 'Cannot identify fixture process'
    try:
        image = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(image))
        assert kernel.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(size))
        assert Path(image.value).resolve() == expected, 'Unexpected fixture process'
    finally:
        kernel.CloseHandle(handle)


def verify_local_runtime(executable):
    """Positive identity for a hidden local fixture; never enables native input."""
    import hashlib
    import json
    assert os.name == 'nt' and os.environ.get('CI') == 'true'
    assert os.environ.get('GITHUB_ACTIONS') != 'true'
    root = Path(os.environ['TREEGIT_LOCAL_QA_ROOT']).absolute()
    executable = Path(executable).resolve()
    assert root.resolve() == executable.parent and root.name.startswith('TreeGit isolated UI runtime ')
    assert not root.is_symlink() and not root.is_junction()
    marker = root / '.treegit-local-qa.json'
    assert marker.is_file() and not marker.is_symlink() and marker.stat().st_size <= 8192
    data = json.loads(marker.read_text('utf-8'))
    with executable.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    assert data['purpose'] == 'TreeGit local QA' and data['executableSha256'] == digest
    return root


def new_fixture(parent, prefix):
    parent = Path(parent).resolve()
    assert prefix.startswith('TreeGit '), 'QA fixture requires an explicit owned prefix'
    parent.mkdir(parents=True, exist_ok=True)
    path = Path(tempfile.mkdtemp(prefix=prefix, dir=parent)).resolve()
    atexit.register(remove_fixture, path, parent, prefix)
    return path


def remove_fixture(path, parent, prefix):
    path = Path(path).absolute(); parent = Path(parent).resolve()
    assert path.parent.resolve() == parent and path.name.startswith(prefix), 'Unexpected QA cleanup target'
    assert not path.is_symlink() and not path.is_junction(), 'QA root is a link'
    if not path.exists(): return
    root = path.resolve()
    assert root.parent == parent and root.name.startswith(prefix)
    io_root = root
    if os.name == 'nt':
        # .NET can extract deep update payloads even when legacy Win32 path
        # limits are enabled. Keep ownership checks above on canonical paths,
        # then use the extended absolute form only for filesystem operations.
        raw = str(root)
        if not raw.startswith('\\\\?\\'):
            io_root = Path('\\\\?\\UNC\\' + raw[2:] if raw.startswith('\\\\') else '\\\\?\\' + raw)

    def remove(directory):
        with os.scandir(directory) as entries:
            children = list(entries)
        for entry in children:
            item = Path(entry.path).absolute()
            assert item.is_relative_to(io_root)
            if entry.is_symlink():
                item.unlink()
            elif item.is_junction():
                # Removing the link itself never enumerates or deletes its target.
                os.rmdir(item)
            elif entry.is_dir(follow_symlinks=False):
                remove(item)
            else:
                try:
                    item.unlink()
                except PermissionError:
                    item.chmod(item.stat().st_mode | stat.S_IWRITE)
                    item.unlink()
        os.rmdir(directory)

    remove(io_root)
