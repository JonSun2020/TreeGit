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
