"""Exercise real release workers only inside one short, disposable installation."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,sys,tempfile,time,zipfile
from qa_cleanup import new_fixture,remove_fixture,verify_local_runtime

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
verify_local_runtime(exe)
source=Path(__file__).resolve().parents[1]
archive=Path(os.environ['TREEGIT_QA_OLD_ARCHIVE']).resolve();assert archive.name=='TreeGit-2026.23.23-Windows-x64.zip' and archive.is_file()
assert hashlib.sha256(archive.read_bytes()).hexdigest()=='2901e6848a256d772d5e4dbe2f0a0be13e51369b1312e163bc446ed80a6121e1'
probe=source/'tests/bin/Debug/net10.0/UpdaterProbe.exe';assert probe.is_file()
parent=Path(tempfile.gettempdir()).resolve();assert parent.name.startswith('TreeGit audit temporary ')
fixture=new_fixture(parent,'TreeGit update QA ');results=[];failure=None
try:
 old=fixture/'old-source'
 with zipfile.ZipFile(archive) as z:
  for entry in z.infolist():
   assert entry.filename.startswith('TreeGit/') and '..' not in Path(entry.filename).parts and '\\' not in entry.filename and ':' not in entry.filename
   assert (entry.external_attr>>16)&0xf000!=0xa000
  z.extractall(old)
 old=old/'TreeGit';assets=fixture/'assets'
 pack=subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(source/'package-treegit.ps1'),'-Source',str(exe.parent),'-Destination',str(assets)],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW,timeout=120)
 assert pack.returncode==0,(pack.stdout+pack.stderr).decode('utf-8',errors='replace')[-3000:]
 version=json.loads((exe.parent/'build-info.json').read_text('utf-8-sig'))['version']
 new_archive=assets/f'TreeGit-{version}-Windows-x64-BundledGit.zip';assert new_archive.is_file()
 broken=fixture/'startup-failure-fixture.zip'
 with zipfile.ZipFile(new_archive) as original,zipfile.ZipFile(broken,'w') as target:
  for entry in original.infolist():target.writestr(entry,b'' if entry.filename=='TreeGit/av_libglesv2.dll' else original.read(entry))
 env={**os.environ,'DOTNET_BUNDLE_EXTRACT_BASE_DIR':str(fixture/'bundle-cache')}
 # The update test addresses exact owned PIDs itself; its install root is
 # deliberately separate from the marked integration runtime above.
 env.pop('TREEGIT_LOCAL_QA_ROOT',None)
 for name,options,payload in [
  ('current-worker-connected-ai',['--keep-mcp'],new_archive),
  ('previous-release-worker',['--old-worker','--keep-mcp'],new_archive),
  ('startup-failure-rollback',['--expect-rollback'],broken)]:
  folder=new_fixture(fixture,'TreeGit update case ')
  try:
   log=out/(name+'.log')
   with log.open('w',encoding='utf-8') as stream:
    test=subprocess.run([sys.executable,'-u',str(source/'tests/updater_end_to_end.py'),str(old),str(exe.parent),str(payload),str(folder),str(probe),*options],env=env,stdout=stream,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW,timeout=240)
   data=json.loads((folder/'results.json').read_text('utf-8'));expected=6 if '--expect-rollback' in options else 9
   (out/(name+'-actual.json')).write_text(json.dumps(data,indent=2),'utf-8')
   for diagnostic in folder.glob('diagnostic-*'):
    if diagnostic.is_file():shutil.copyfile(diagnostic,out/(name+'-'+diagnostic.name))
   assert test.returncode==0 and data['passed']==expected and not data['failure'],(name,data,log.read_text('utf-8')[-4000:])
   data.update(case=name,oldArchiveSha256=hashlib.sha256(archive.read_bytes()).hexdigest(),normalExecutableSha256=hashlib.sha256(exe.read_bytes()).hexdigest(),fixtureOnly=True)
   (out/(name+'.json')).write_text(json.dumps(data,indent=2),'utf-8');results.append({'case':name,'passed':data['passed']})
  finally:
   for attempt in range(50):
    try:remove_fixture(folder,fixture,'TreeGit update case ');break
    except PermissionError:
     if attempt==49:raise
     time.sleep(.1)
except BaseException:
 import traceback
 failure=traceback.format_exc();raise
finally:
 remove_fixture(fixture,parent,'TreeGit update QA ')
 (out/'normal-update-results.json').write_text(json.dumps({'passed':sum(r['passed'] for r in results),'cases':results,'failure':failure,'fixtureRemoved':not fixture.exists(),'formalInstallUntouched':True},indent=2),'utf-8')
