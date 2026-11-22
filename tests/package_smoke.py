"""The release ZIP must exclude local files and reject integration builds/reused versions."""
import hashlib, io, json, msvcrt, subprocess, sys, tempfile, zipfile
from pathlib import Path
from qa_cleanup import new_fixture, remove_fixture
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit package audit ');source=root/'publish';source.mkdir();(source/'data').mkdir();(source/'git/cmd').mkdir(parents=True)
files={'TreeGit.exe':b'package-test executable fixture','native.dll':b'package-test native fixture','LICENSE':b'license','THIRD-PARTY-LICENSES.md':b'notices','使用说明.md':b'help','MCP-使用说明.md':b'mcp help','mcp-readonly.json':b'{}','mcp-readwrite.json':b'{}'}
for name,data in files.items():(source/name).write_bytes(data)
private_mcp=json.dumps({'mcpServers':{'personal':{'command':r'D:\PrivateFixture\TreeGit.exe','args':['--mcp'],'env':{'TOKEN':'TREEGIT_PRIVATE_MCP_CANARY'}}}}).encode()
for name in ['mcp-readonly.json','mcp-readwrite.json']:(source/name).write_bytes(private_mcp)
info={'product':'TreeGit','version':'0.0.1','integrationTests':False,'bundledGit':True};(source/'build-info.json').write_text(json.dumps(info),encoding='utf-8');files['build-info.json']=b''
for name in ['.env','preference.json','notes.txt','debug.pdb','old.zip']:(source/name).write_bytes(b'local fixture; must not ship')
(source/'data/preference.json').write_bytes(b'private preferences fixture')
script=Path(__file__).resolve().parents[1]/'package-treegit.ps1'
runtime=source/'git'
required=['cmd/git.exe','usr/bin/ssh.exe','usr/bin/ssh-keygen.exe','ucrt64/libexec/git-core/git-lfs.exe','ucrt64/libexec/git-core/git-flow.exe','etc/gitconfig','ucrt64/etc/ssl/certs/ca-bundle.crt','LICENSE.txt','licenses/Git-LFS-LICENSE.md','licenses/Git-Flow-LICENSE','COMPONENTS.md']
for name in required:
 p=runtime/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(('component fixture '+name).encode())
manifest={'schema':1,'gitVersion':'2.56.0.windows.4','inputLockSha256':hashlib.sha256((script.parent/'build/git-runtime.lock.json').read_bytes()).hexdigest(),'files':[{'path':name,'sha256':hashlib.sha256((runtime/name).read_bytes()).hexdigest()} for name in required]}
(runtime/'runtime-manifest.json').write_text(json.dumps(manifest),'utf-8')
def pack(destination, app=False):return subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(script),'-Source',str(source),'-Destination',str(destination)]+(['-WithAppUpdate'] if app else []),capture_output=True)
checks=[]
def check(name,condition):
    assert condition,name;checks.append(name);print('PASS',name,flush=True)
first=root/'assets';r=pack(first, app=True)
if r.returncode!=0:raise RuntimeError(r.stdout.decode('utf-8',errors='replace')+r.stderr.decode('utf-8',errors='replace'))
check('a normal build creates a versioned archive',(first/'TreeGit-0.0.1-Windows-x64-BundledGit.zip').is_file())
archive=first/'TreeGit-0.0.1-Windows-x64-BundledGit.zip'
with zipfile.ZipFile(archive) as z:
    wrappers={'cmd/git.exe','runtime.zip','runtime-manifest.json','payload-manifest.json','COMPONENTS.md','LICENSE.txt','licenses/Git-LFS-LICENSE.md','licenses/Git-Flow-LICENSE'}
    check('the portable package keeps Git compressed',set(z.namelist())=={'TreeGit/'+name for name in files}|{'TreeGit/data/'}|{'TreeGit/git/'+n for n in wrappers})
    wrapper=json.loads(z.read('TreeGit/git/runtime-manifest.json'))['files']
    check('the previous updater can verify the compact payload', {x['path'] for x in wrapper}==wrappers-{'runtime-manifest.json'} and all(hashlib.sha256(z.read('TreeGit/git/'+x['path'])).hexdigest()==x['sha256'] for x in wrapper))
    with zipfile.ZipFile(io.BytesIO(z.read('TreeGit/git/runtime.zip'))) as payload:
        check('every verified component is recoverable offline',set(payload.namelist())==set(required) and all(payload.read(n)==(runtime/n).read_bytes() for n in required))
    for name,args in [('mcp-readonly.json',['--mcp','--read-only']),('mcp-readwrite.json',['--mcp'])]:
        check(name+' ships only a generic example without personal commands or secrets',json.loads(z.read('TreeGit/'+name))=={'mcpServers':{'treegit':{'command':r'C:\Tools\TreeGit\TreeGit.exe','args':args}}})
check('packaging leaves both personal MCP configurations unchanged',all((source/name).read_bytes()==private_mcp for name in ['mcp-readonly.json','mcp-readwrite.json']))
check('the published SHA256 matches the complete archive',hashlib.sha256(archive.read_bytes()).hexdigest()==(first/'SHA256SUMS.txt').read_text().split()[0])
entries=sorted((entry['path'],entry['sha256']) for entry in manifest['files'])
identity=hashlib.sha256('\n'.join([manifest['gitVersion']]+[f'{path}\t{sha}' for path,sha in entries]).encode()).hexdigest()
app=first/'TreeGit-0.0.1-Windows-x64-SystemGit.zip'
record=first/'BundledGit-Identity.txt'
check('both variant names omit checksums and the runtime identity is detached',app.is_file() and record.read_text().splitlines()==[manifest["gitVersion"],identity])
check('the detached identity file also has a published SHA256', hashlib.sha256(record.read_bytes()).hexdigest()+'  '+record.name in (first/'SHA256SUMS.txt').read_text())
with zipfile.ZipFile(archive) as full,zipfile.ZipFile(app) as program:
    program_names=[name for name in full.namelist() if not name.startswith('TreeGit/git/')]
    check('the lightweight package has the same program and no Git files',set(program.namelist())==set(program_names) and all(program.read(name)==full.read(name) for name in program_names))
check('both full and lightweight packages have published SHA256 values',hashlib.sha256(app.read_bytes()).hexdigest()+'  '+app.name in (first/'SHA256SUMS.txt').read_text())
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
