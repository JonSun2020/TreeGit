"""The release ZIP must exclude local files and reject integration builds/reused versions."""
import hashlib, json, msvcrt, subprocess, sys, tempfile, zipfile
from pathlib import Path
from qa_cleanup import new_fixture, remove_fixture
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit package audit ');source=root/'publish';source.mkdir();(source/'data').mkdir();(source/'git/cmd').mkdir(parents=True)
files={'TreeGit.exe':b'package-test executable fixture','native.dll':b'package-test native fixture','LICENSE':b'license','THIRD-PARTY-LICENSES.md':b'notices','使用说明.md':b'help','MCP-使用说明.md':b'mcp help','mcp-readonly.json':b'{}','mcp-readwrite.json':b'{}'}
for name,data in files.items():(source/name).write_bytes(data)
info={'product':'TreeGit','version':'0.0.1','integrationTests':False,'bundledGit':True};(source/'build-info.json').write_text(json.dumps(info),encoding='utf-8');files['build-info.json']=b''
for name in ['.env','preference.json','notes.txt','debug.pdb','old.zip']:(source/name).write_bytes(b'local fixture; must not ship')
(source/'data/preference.json').write_bytes(b'private preferences fixture')
script=Path(__file__).resolve().parents[1]/'package-treegit.ps1'
runtime=source/'git'
required=['cmd/git.exe','usr/bin/ssh.exe','usr/bin/ssh-keygen.exe','ucrt64/libexec/git-core/git-lfs.exe','ucrt64/libexec/git-core/git-flow.exe','etc/gitconfig','ucrt64/etc/ssl/certs/ca-bundle.crt','LICENSE.txt','licenses/Git-LFS-LICENSE.md','licenses/Git-Flow-LICENSE','COMPONENTS.md']
for name in required:
 p=runtime/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(('component fixture '+name).encode())
manifest={'schema':1,'inputLockSha256':hashlib.sha256((script.parent/'build/git-runtime.lock.json').read_bytes()).hexdigest(),'files':[{'path':name,'sha256':hashlib.sha256((runtime/name).read_bytes()).hexdigest()} for name in required]}
(runtime/'runtime-manifest.json').write_text(json.dumps(manifest),'utf-8')
def pack(destination):return subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(script),'-Source',str(source),'-Destination',str(destination)],capture_output=True)
checks=[]
def check(name,condition):
    assert condition,name;checks.append(name);print('PASS',name,flush=True)
first=root/'assets';r=pack(first)
if r.returncode!=0:raise RuntimeError(r.stdout.decode('utf-8',errors='replace')+r.stderr.decode('utf-8',errors='replace'))
check('a normal build creates a versioned archive',(first/'TreeGit-0.0.1-Windows-x64.zip').is_file())
archive=first/'TreeGit-0.0.1-Windows-x64.zip'
with zipfile.ZipFile(archive) as z:
    check('program files, verified Git and an empty data directory enter the ZIP',set(z.namelist())=={'TreeGit/'+name for name in files}|{'TreeGit/data/'}|{'TreeGit/git/'+n for n in required+['runtime-manifest.json']})
check('the published SHA256 matches the complete archive',hashlib.sha256(archive.read_bytes()).hexdigest()==(first/'SHA256SUMS.txt').read_text().split()[0])
private=runtime/'private-user-config';private.write_bytes(b'must not publish')
blocked=root/'unexpected';r=pack(blocked);check('extra runtime files are rejected before an archive exists',r.returncode!=0 and not list(blocked.glob('*.zip')));private.unlink()
ssh=runtime/'usr/bin/ssh.exe';saved=ssh.read_bytes();ssh.unlink()
blocked=root/'missing';r=pack(blocked);check('missing SSH is rejected before an archive exists',r.returncode!=0 and not list(blocked.glob('*.zip')));ssh.write_bytes(saved)
keygen=runtime/'usr/bin/ssh-keygen.exe';keygen_saved=keygen.read_bytes();keygen.unlink()
blocked=root/'missing-keygen';r=pack(blocked);check('missing SSH key generator is rejected before an archive exists',r.returncode!=0 and not list(blocked.glob('*.zip')));keygen.write_bytes(keygen_saved)
ssh.write_bytes(b'corrupted');blocked=root/'corrupt';r=pack(blocked);check('changed runtime bytes are rejected before an archive exists',r.returncode!=0 and not list(blocked.glob('*.zip')));ssh.write_bytes(saved)
before=archive.read_bytes();r=pack(first);check('an existing version is rejected without changing its bytes',r.returncode!=0 and archive.read_bytes()==before)
locked=root/'read-failure'
with (source/'native.dll').open('r+b') as handle:
 msvcrt.locking(handle.fileno(),msvcrt.LK_NBLCK,len(files['native.dll']))
 try:r=pack(locked)
 finally:handle.seek(0);msvcrt.locking(handle.fileno(),msvcrt.LK_UNLCK,len(files['native.dll']))
check('a read failure leaves no published or partial archive',r.returncode!=0 and not list(locked.glob('*.zip')) and not list(locked.glob('*.partial-*')))
info['integrationTests']=True;(source/'build-info.json').write_text(json.dumps(info),encoding='utf-8');blocked=root/'blocked';r=pack(blocked)
check('integration-test builds are rejected before any archive is created',r.returncode!=0 and not list(blocked.glob('*.zip')))
(out/'packaging-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'root':str(root)},indent=2),encoding='utf-8')
remove_fixture(root,out,'TreeGit package audit ')
