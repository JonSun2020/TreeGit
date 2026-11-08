"""Actual Git output must not block a query or authentication configuration lookup."""
import json, os, subprocess, sys, tempfile, time
from pathlib import Path
from qa_cleanup import remove_fixture

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
    modes=['safety-'+mode for mode in ['status-failure','status-cancelled','bounded-output','quoted-arguments','normal','unborn','obstruction','selected-obstruction','selected-literal','nested-repository','selected-nested-repository','junction','failed-discard','merge-ignored','merge-benign-directory','merge-file-ancestor','merge-nested-repository']]
if '--pull-safety' in sys.argv[3:]:
    modes=['pull-safety-'+mode for mode in ['merge','rebase','rewritten-upstream','config-false','config-true','config-only','ignored','cancelled','fetch-hook','ff-only']]
try:
    for mode in modes:
        report = root/(mode+'.json'); started = time.monotonic()
        p = subprocess.Popen([str(exe), '--integration-command-pipes', str(root/mode), mode, str(report)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        timed_out = False
        try:
            stdout, stderr = p.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            timed_out = True
            assert p.poll() is None
            script = "$p=Get-Process -Id "+str(p.pid)+" -ErrorAction SilentlyContinue;if($p){if($p.Path -ne '"+str(exe).replace("'", "''")+"'){throw 'Unexpected probe; left unchanged'};taskkill /PID "+str(p.pid)+" /T /F | Out-Null;if($LASTEXITCODE -ne 0 -and (Get-Process -Id "+str(p.pid)+" -ErrorAction SilentlyContinue)){throw 'Owned probe did not exit'}}"
            subprocess.run(['powershell', '-NoProfile', '-Command', script], check=True, capture_output=True)
            stdout, stderr = p.communicate(timeout=10)
        if p.returncode != 0 or timed_out:
            (out/(mode+'-stdout.log')).write_bytes(stdout[-65536:]); (out/(mode+'-stderr.log')).write_bytes(stderr[-65536:])
        result = json.loads(report.read_text('utf-8')) if report.exists() else None
        if timed_out or p.returncode != 0 or not result or not result.get('success'):
            trace = Path(str(report)+'.trace.log')
            if trace.exists(): (out/(mode+'-trace.log')).write_bytes(trace.read_bytes()[-65536:])
        record = {'mode':mode, 'timedOut':timed_out, 'seconds':round(time.monotonic()-started, 2), 'exitCode':p.returncode, 'result':result}
        records.append(record)
        (out/'command-pipes-results.json').write_text(json.dumps(records, indent=2), 'utf-8')
        print(('PASS ' if not timed_out and p.returncode == 0 and result and result['success'] else 'FAIL ')+mode, flush=True)
        remove_fixture(root/mode, root, mode)
finally:
    # Preserve compact diagnostics even when a timeout or infrastructure failure
    # interrupts the normal per-case reporting path.
    if len(records) < len(modes):
        for trace in root.glob('*.trace.log'):
            if trace.is_symlink(): continue
            with trace.open('rb') as stream:
                stream.seek(max(0, trace.stat().st_size-65536))
                (out/trace.name).write_bytes(stream.read(65536))
    remove_fixture(root, out, 'command-pipes-')
assert all(not r['timedOut'] and r['exitCode'] == 0 and r['result'] and r['result']['success'] for r in records), records
