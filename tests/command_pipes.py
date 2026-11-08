"""Actual Git output must not block a query or authentication configuration lookup."""
import json, os, subprocess, sys, tempfile, time
from pathlib import Path

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='command-pipes-', dir=out))
env = os.environ.copy(); env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'))
records = []
modes = ['stream-'+mode for mode in ['diff','commits','changes','hashes','names','objects','pickable','compare','conflict','save','save-failure','lfs','save-lfs','save-lfs-failure']] if '--streaming' in sys.argv[3:] else ['sync', 'async', 'sync-failure', 'async-failure', 'credential-config']
if '--pointers' in sys.argv[3:]:
    modes=['stream-pointer-'+mode for mode in ['valid','added','deleted','invalid-size','overflow','short-oid','extra-text','crlf','zero','version']]
if '--indexes' in sys.argv[3:]:
    modes=['stream-index-'+mode for mode in ['input','sha256','path','mode','rename','symlink']]
if '--launches' in sys.argv[3:]:
    modes=['stream-launch-'+mode for mode in ['sync','async','exec','permanent','exit','missing']]
if '--keygen' in sys.argv[3:]:
    modes=['stream-keygen-'+mode for mode in ['plain','protected','existing','pub-only','private-only','invalid','missing']]
if '--safety' in sys.argv[3:]:
    modes=['safety-'+mode for mode in ['status-failure','status-cancelled','normal','unborn','obstruction','selected-obstruction','selected-literal','nested-repository','selected-nested-repository','junction','failed-discard','merge-ignored','merge-benign-directory','merge-file-ancestor','merge-nested-repository']]
for mode in modes:
    report = root/(mode+'.json'); started = time.monotonic()
    p = subprocess.Popen([str(exe), '--integration-command-pipes', str(root/mode), mode, str(report)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    timed_out = False
    try:
        stdout, stderr = p.communicate(timeout=30)
    except subprocess.TimeoutExpired:
        timed_out = True
        assert p.poll() is None
        script = "$p=Get-Process -Id "+str(p.pid)+" -ErrorAction Stop;if($p.Path -ne '"+str(exe).replace("'", "''")+"'){throw 'Unexpected probe; left unchanged'};taskkill /PID "+str(p.pid)+" /T /F | Out-Null"
        subprocess.run(['powershell', '-NoProfile', '-Command', script], check=True, capture_output=True)
        stdout, stderr = p.communicate(timeout=10)
    (root/(mode+'-stdout.log')).write_bytes(stdout); (root/(mode+'-stderr.log')).write_bytes(stderr)
    result = json.loads(report.read_text('utf-8')) if report.exists() else None
    record = {'mode':mode, 'timedOut':timed_out, 'seconds':round(time.monotonic()-started, 2), 'exitCode':p.returncode, 'result':result}
    records.append(record)
    (out/'command-pipes-results.json').write_text(json.dumps(records, indent=2), 'utf-8')
    print(('PASS ' if not timed_out and p.returncode == 0 and result and result['success'] else 'FAIL ')+mode, flush=True)
assert all(not r['timedOut'] and r['exitCode'] == 0 and r['result'] and r['result']['success'] for r in records), records
