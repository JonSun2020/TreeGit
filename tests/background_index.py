"""Cold-start background status must not prevent an external index write."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = Path(tempfile.mkdtemp(prefix='TreeGit index concurrency '))
git = shutil.which('git')
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
seq = 0

def request(method, params={}):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0', 'id':seq, 'method':method, 'params':params})+'\n'); p.stdin.flush()
    result = json.loads(p.stdout.readline()); assert 'error' not in result, result
    return result['result']

def tool(name, args):
    result = request('tools/call', {'name':name, 'arguments':args}); assert not result.get('isError'), result

def run(repo, *args):
    result = subprocess.run([git, *args], cwd=repo, capture_output=True, text=True, encoding='utf-8')
    assert result.returncode == 0, result.stderr

request('initialize', {'protocolVersion':'2025-11-25'})
for index in range(10):
    repo = root / str(index); repo.mkdir()
    run(repo, 'init', '-b', 'main'); run(repo, 'config', 'user.name', 'TreeGit QA'); run(repo, 'config', 'user.email', 'qa@example.invalid')
    file = repo / 'sample.txt'; file.write_text('initial\n')
    run(repo, 'add', '.'); run(repo, 'commit', '-m', 'Initial')
    tool('ui_open_repository', {'repo':str(repo)})
    for revision in range(5):
        file.write_text(str(revision)+'\n')
        tool('ui_refresh', {'repo':str(repo)})
        run(repo, 'add', 'sample.txt')
        tool('git_status', {'repo':str(repo)})
        run(repo, 'commit', '-m', 'Update '+str(revision))
    assert not (repo/'.git/index.lock').exists()
    print('PASS cold-open, refresh, status and external writes:', index+1, flush=True)
p.stdin.close(); p.wait(timeout=10)
(out/'background-index-results.json').write_text(json.dumps({'repositories':10, 'external_add_commit_cycles':50, 'all_passed':True, 'root':str(root)}, indent=2), encoding='utf-8')
