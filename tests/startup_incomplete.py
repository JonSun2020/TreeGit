"""A copied/incompletely extracted package must return useful guidance through MCP."""
import json, queue, shutil, subprocess, sys, threading, time
from pathlib import Path

source = Path(sys.argv[1]).resolve().parent
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
root = out/'incomplete-app'; root.mkdir()
assert (source/'libSkiaSharp.dll').is_file(), 'The input must be a complete normal package'
for file in source.iterdir():
    if file.is_file() and file.name != 'libSkiaSharp.dll': shutil.copy2(file, root/file.name)
(root/'data').mkdir(); (root/'data/preference.json').write_text(json.dumps({'Locale':'zh_CN'}), 'utf-8')
(root/'data/sentinel.txt').write_text('keep personal configuration', 'utf-8')
p = subprocess.Popen([str(root/'TreeGit.exe'), '--mcp', '--read-only'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
replies = queue.Queue(); threading.Thread(target=lambda:[replies.put(s) for s in p.stdout], daemon=True).start()
def rpc(i, method, params):
    p.stdin.write(json.dumps({'jsonrpc':'2.0','id':i,'method':method,'params':params})+'\n'); p.stdin.flush()
    result = json.loads(replies.get(timeout=8)); assert 'error' not in result, result
    return result['result']
try:
    rpc(1, 'initialize', {'protocolVersion':'2025-11-25'})
    started = time.monotonic()
    result = rpc(2, 'tools/call', {'name':'ui_state','arguments':{}})
    elapsed = time.monotonic()-started
    text = result['content'][0]['text']
    assert result.get('isError') and '完整解压' in text and '.dll' not in text and elapsed < 6, result
    assert (root/'data/sentinel.txt').read_text('utf-8') == 'keep personal configuration'
    (out/'startup-incomplete-results.json').write_text(json.dumps({'passed':True,'seconds':round(elapsed,3),'message':text,'settingsPreserved':True},ensure_ascii=False,indent=2),'utf-8')
    print('PASS incomplete package gives prompt, actionable MCP guidance and preserves settings', flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill(); p.wait()
    literal = str(root/'TreeGit.exe').replace("'", "''")
    script = "$ErrorActionPreference='Stop';Get-Process -Name TreeGit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq '"+literal+"'} | ForEach-Object {$null=$_.CloseMainWindow();if(-not $_.WaitForExit(5000)){$_.Kill();$_.WaitForExit()}};exit 0"
    subprocess.run(['powershell','-NoProfile','-Command',script],check=True,capture_output=True)
