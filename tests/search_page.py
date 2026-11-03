"""Search-page navigation, all original search methods, and off-graph details."""
import base64
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

exe = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
repo = Path(tempfile.mkdtemp(prefix='TreeGit search '))
checks = []

def git(*args):
    p = subprocess.run([shutil.which('git'), *args], cwd=repo, capture_output=True, text=True, encoding='utf-8')
    assert p.returncode == 0, p.stderr
    return p.stdout.strip()

git('init', '-b', 'main'); git('config', 'user.name', 'TreeGit QA'); git('config', 'user.email', 'qa@example.invalid')
(repo/'sensor.txt').write_text('sensor initial\n'); git('add', '.'); git('commit', '-m', 'Initial sensor')
git('checkout', '-b', 'feature/search')
(repo/'calibration.txt').write_text('unique_calibration_value\n'); git('add', '.'); git('commit', '-m', 'Calibrate sensor')
feature = git('rev-parse', 'HEAD'); git('checkout', 'main')
p = subprocess.Popen([str(exe), '--mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
seq = 0

def rpc(method, params={}):
    global seq
    seq += 1
    p.stdin.write(json.dumps({'jsonrpc':'2.0', 'id':seq, 'method':method, 'params':params})+'\n'); p.stdin.flush()
    r = json.loads(p.stdout.readline()); assert 'error' not in r, r
    return r['result']

def tool(name, args={}):
    r = rpc('tools/call', {'name':name, 'arguments':args}); assert not r.get('isError'), r
    return base64.b64decode(r['content'][0]['data']) if name == 'ui_screenshot' else json.loads(r['content'][0]['text'])

def state():
    return next(r for r in tool('ui_state')['repositories'] if Path(r['repo']) == repo)

def check(name, ok):
    assert ok, name
    checks.append(name); print('PASS', name, flush=True)

def search(query, method='message', current=False):
    tool('ui_search', {'repo':str(repo), 'query':query, 'method':method, 'currentBranchOnly':current})
    for _ in range(150):
        s = state()
        if not s['searchQuerying']: return s
        time.sleep(.1)
    raise AssertionError('Search did not finish')

rpc('initialize', {'protocolVersion':'2025-11-25'})
tool('ui_open_repository', {'repo':str(repo)})
tool('ui_window', {'width':1440, 'height':900, 'theme':'SourceTree'})
for _ in range(100):
    if not state()['graphLoading']: break
    time.sleep(.1)
before = state()
tool('ui_select_view', {'repo':str(repo), 'view':'search'})
time.sleep(.3); s = state()
check('search opens in the right page with the branch sidebar retained', s['searchActive'] and s['sidebarVisible'])
check('search does not change the sidebar width', s['sidebarWidth'] == before['sidebarWidth'])
check('new search page has no selected commit', s['searchSelected'] is None)
(out/'search-empty.png').write_bytes(tool('ui_screenshot'))
for method, query in [('message','Calibrate'), ('author','TreeGit QA'), ('path','calibration.txt'), ('content','unique_calibration_value'), ('sha',feature)]:
    s = search(query, method)
    check(method+' search finds the unmerged commit', feature in {c['sha'] for c in s['searchResults'] or []})
tool('ui_search', {'repo':str(repo), 'select':feature})
time.sleep(.5); s = state()
check('selecting an off-graph result shows its commit details', s['searchSelected'] == feature and s['detailView'] == 'CommitDetail')
(out/'search-result.png').write_bytes(tool('ui_screenshot'))
check('search selection never checks out a different branch', git('branch', '--show-current') == 'main')
s = search('Calibrate', current=True)
check('current-branch search excludes unmerged results', not s['searchResults'])
s = search('')
check('clearing the search clears results and selection', not s['searchResults'] and s['searchSelected'] is None and not s['searchQuerying'])
search('Calibrate')
tool('ui_select_view', {'repo':str(repo), 'view':'history'}); s = state()
check('history tab exits search and restores the history page', not s['searchActive'] and s['view'] == 0 and s['sidebarVisible'])
tool('ui_select_view', {'repo':str(repo), 'view':'search'})
tool('ui_working_copy', {'repo':str(repo)}); s = state()
check('file-status tab exits search and shows working-copy overview', not s['searchActive'] and s['workingCopySelected'])
tool('ui_select_view', {'repo':str(repo), 'view':'search'})
tool('ui_select_view', {'repo':str(repo), 'view':'working_copy'})
check('full commit page also exits search', not state()['searchActive'])
tool('ui_select_view', {'repo':str(repo), 'view':'search'})
tool('ui_window', {'width':1024, 'height':720})
(out/'search-narrow.png').write_bytes(tool('ui_screenshot'))
tool('ui_select_view', {'repo':str(repo), 'view':'history'})
p.stdin.close(); p.wait(timeout=10)
(out/'search-results.json').write_text(json.dumps({'passed':len(checks), 'checks':checks, 'repo':str(repo), 'exe':str(exe)}, ensure_ascii=False, indent=2), encoding='utf-8')
