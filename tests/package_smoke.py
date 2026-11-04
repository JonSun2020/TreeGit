"""The release ZIP must exclude local files and reject integration builds/reused versions."""
import hashlib, json, subprocess, sys, tempfile, zipfile
from pathlib import Path
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='TreeGit package audit '));source=root/'publish';source.mkdir();(source/'data').mkdir();(source/'git/cmd').mkdir(parents=True)
files={'TreeGit.exe':b'package-test executable fixture','native.dll':b'package-test native fixture','LICENSE':b'license','THIRD-PARTY-LICENSES.md':b'notices','使用说明.md':b'help','MCP-使用说明.md':b'mcp help','mcp-readonly.json':b'{}','mcp-readwrite.json':b'{}'}
for name,data in files.items():(source/name).write_bytes(data)
info={'product':'TreeGit','version':'0.0.1','integrationTests':False};(source/'build-info.json').write_text(json.dumps(info),encoding='utf-8');files['build-info.json']=b''
for name in ['.env','preference.json','notes.txt','debug.pdb','old.zip']:(source/name).write_bytes(b'local fixture; must not ship')
(source/'data/preference.json').write_bytes(b'private preferences fixture');(source/'git/cmd/git.exe').write_bytes(b'optional git fixture')
script=Path(__file__).resolve().parents[1]/'package-treegit.ps1'
def pack(destination):return subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(script),'-Source',str(source),'-Destination',str(destination)],capture_output=True)
checks=[]
def check(name,condition):
    assert condition,name;checks.append(name);print('PASS',name,flush=True)
first=root/'assets';r=pack(first)
if r.returncode!=0:raise RuntimeError(r.stdout.decode('utf-8',errors='replace')+r.stderr.decode('utf-8',errors='replace'))
check('a normal build creates a versioned archive',(first/'TreeGit-0.0.1-Windows-x64.zip').is_file())
archive=first/'TreeGit-0.0.1-Windows-x64.zip'
with zipfile.ZipFile(archive) as z:
    check('only program files and an empty data directory enter the ZIP',set(z.namelist())=={'TreeGit/'+name for name in files}|{'TreeGit/data/'})
check('the published SHA256 matches the complete archive',hashlib.sha256(archive.read_bytes()).hexdigest()==(first/'SHA256SUMS.txt').read_text().split()[0])
before=archive.read_bytes();r=pack(first);check('an existing version is rejected without changing its bytes',r.returncode!=0 and archive.read_bytes()==before)
info['integrationTests']=True;(source/'build-info.json').write_text(json.dumps(info),encoding='utf-8');blocked=root/'blocked';r=pack(blocked)
check('integration-test builds are rejected before any archive is created',r.returncode!=0 and not list(blocked.glob('*.zip')))
(out/'packaging-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'root':str(root)},indent=2),encoding='utf-8')
