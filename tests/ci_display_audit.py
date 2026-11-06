"""Additional real-desktop coverage on an ephemeral GitHub Windows runner only.

Uses an enumerated, driver-tested display mode; never writes display registry
settings, and restores the original mode even when the child audit fails.
"""
import ctypes
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import subprocess
import sys

if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Windows':
    raise SystemExit('This display audit is restricted to an ephemeral GitHub Windows runner.')

class Mode(ctypes.Structure):
    _fields_ = [('device', w.WCHAR * 32), ('spec', w.WORD), ('driver', w.WORD), ('size', w.WORD), ('extra', w.WORD),
                ('fields', w.DWORD), ('displayUnion', ctypes.c_byte * 16), ('color', w.SHORT), ('duplex', w.SHORT),
                ('yResolution', w.SHORT), ('ttOption', w.SHORT), ('collate', w.SHORT), ('form', w.WCHAR * 32),
                ('logPixels', w.WORD), ('bits', w.DWORD), ('width', w.DWORD), ('height', w.DWORD),
                ('flags', w.DWORD), ('frequency', w.DWORD), ('icmMethod', w.DWORD), ('icmIntent', w.DWORD),
                ('media', w.DWORD), ('dither', w.DWORD), ('reserved1', w.DWORD), ('reserved2', w.DWORD),
                ('panningWidth', w.DWORD), ('panningHeight', w.DWORD)]

assert ctypes.sizeof(Mode) == 220
api = ctypes.WinDLL('user32', use_last_error=True)
api.EnumDisplaySettingsW.argtypes = [w.LPCWSTR, w.DWORD, ctypes.POINTER(Mode)]
api.EnumDisplaySettingsW.restype = w.BOOL
api.ChangeDisplaySettingsW.argtypes = [ctypes.POINTER(Mode), w.DWORD]
api.ChangeDisplaySettingsW.restype = w.LONG

def read(index):
    mode = Mode()
    mode.size = ctypes.sizeof(Mode)
    return mode if api.EnumDisplaySettingsW(None, index, ctypes.byref(mode)) else None

def describe(mode):
    return None if mode is None else {'width': mode.width, 'height': mode.height, 'bits': mode.bits, 'frequency': mode.frequency}

out = Path(sys.argv[2]).resolve()
out.mkdir(parents=True, exist_ok=True)
before = read(0xffffffff)
assert before
modes = []
for index in range(1000):
    mode = read(index)
    if mode is None: break
    if mode.bits == 32 and 1280 <= mode.width <= 2560 and 800 <= mode.height <= 1440:
        modes.append(mode)
modes.sort(key=lambda m: (abs(m.width - 1920) + abs(m.height - 1080), abs(m.frequency - before.frequency)))
record = {'before': describe(before), 'candidateCount': len(modes), 'status': 'no supported wider mode'}
changed = False
exit_code = 0
try:
    for mode in modes:
        if api.ChangeDisplaySettingsW(ctypes.byref(mode), 2) != 0: continue  # CDS_TEST
        result = api.ChangeDisplaySettingsW(ctypes.byref(mode), 0)  # temporary; no registry flag
        if result == 0:
            changed = True
            record.update(status='changed', selected=describe(mode), actual=describe(read(0xffffffff)))
            break
    if changed:
        result = subprocess.run([sys.executable, str(Path(__file__).with_name('layout_audit.py')), sys.argv[1], str(out / 'wide')])
        exit_code = result.returncode
        record['auditExitCode'] = exit_code
finally:
    if changed:
        record['restoreResult'] = api.ChangeDisplaySettingsW(ctypes.byref(before), 0)
        record['restored'] = describe(read(0xffffffff))
        if record['restoreResult'] != 0 or record['restored'] != describe(before): exit_code = 1
    (out / 'display-audit.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
print(json.dumps(record), flush=True)
raise SystemExit(exit_code)
