"""Execute retained command adapters with the immutable bundled Git runtime."""
from pathlib import Path
import json,os,subprocess,sys,tempfile
from qa_cleanup import new_fixture,remove_fixture,verify_local_runtime

exe=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
verify_local_runtime(exe)
bundle=exe.parent/'git/cmd/git.exe';assert bundle.is_file()
parent=Path(tempfile.gettempdir()).resolve();fixture=new_fixture(parent,'TreeGit upstream ')
result=out/'upstream-runtime-results.json';failure=None
try:
 env={**os.environ,'GIT_CONFIG_GLOBAL':str(fixture/'global.gitconfig'),'GIT_CONFIG_NOSYSTEM':'1','GIT_TERMINAL_PROMPT':'0','GIT_ALLOW_PROTOCOL':'file'}
 process=subprocess.run([str(exe),'--integration-probe',str(fixture/'repositories'),str(result)],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW,timeout=180)
 if process.returncode:
  detail=Path(str(result)+'.failure')
  raise AssertionError(detail.read_text('utf-8')[-6000:] if detail.exists() else process.stderr[-6000:])
 report=json.loads(result.read_text('utf-8'));assert report['passed']>=50 and len(report['checks'])==report['passed']
 repo=fixture/'repositories'/'original \u4e2d\u6587'
 def git(*args):
  r=subprocess.run([str(bundle),'-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW,timeout=30)
  assert r.returncode==0,r.stderr
  return r.stdout.strip()
 assert git('symbolic-ref','--short','HEAD')=='develop'
 assert git('show','develop:flow.txt')=='feature'
 assert not git('for-each-ref','--format=%(refname)','refs/heads/feature/integration')
 assert git('status','--porcelain')==''
 report.update(independentGitFlowTreeVerified=True,bundledGit=True,nativeDesktopInput=False)
 result.write_text(json.dumps(report,indent=2),'utf-8')
finally:
 remove_fixture(fixture,parent,'TreeGit upstream ')
 if result.exists():
  report=json.loads(result.read_text('utf-8'));report['fixtureRemoved']=not fixture.exists();result.write_text(json.dumps(report,indent=2),'utf-8')
