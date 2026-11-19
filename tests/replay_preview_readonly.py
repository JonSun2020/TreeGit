"""Exercise the actual replay command with bundled Git, isolated from real repositories."""
from pathlib import Path
import json,os,runpy,subprocess,sys,traceback
import qa_cleanup
out=Path(sys.argv[2]);out.mkdir(parents=True,exist_ok=True);failure=None;passed=0
expected_hash='sha256' if '--sha256' in sys.argv else 'sha1'
expected_refs='reftable' if '--reftable' in sys.argv else 'files'
long_path='--long-path' in sys.argv
caseout=out/('long directory '+'x'*12) if long_path else out
caseout.mkdir(parents=True,exist_ok=True)
if long_path:os.environ.update(GIT_CONFIG_COUNT='1',GIT_CONFIG_KEY_0='core.longpaths',GIT_CONFIG_VALUE_0='true')
os.environ['GIT_DEFAULT_HASH']=expected_hash;os.environ['GIT_DEFAULT_REF_FORMAT']=expected_refs
exe=Path(sys.argv[1]).resolve();bundled=exe.parent/'git/cmd/git.exe'
git=str(bundled) if bundled.is_file() else os.environ.get('TREEGIT_TEST_BUNDLED_GIT','git')
cleanup=qa_cleanup.remove_fixture;formats=[]
def inspect_then_cleanup(path,parent,prefix):
 if prefix.startswith('stream-replay-') and Path(path).is_dir():
  actual=[]
  for option in ['--show-object-format','--show-ref-format']:
   result=subprocess.run([git,'-C',str(path),'rev-parse',option],capture_output=True,text=True,creationflags=subprocess.CREATE_NO_WINDOW)
   assert result.returncode==0,result.stderr
   actual.append(result.stdout.strip())
  assert actual==[expected_hash,expected_refs],actual
  formats.append(actual)
 return cleanup(path,parent,prefix)
qa_cleanup.remove_fixture=inspect_then_cleanup
try:
 sys.argv=[str(Path(__file__).with_name('command_pipes.py')),str(exe),str(caseout),'--replays-extended']
 runpy.run_path(sys.argv[0],run_name='__main__')
 records=json.loads((caseout/'command-pipes-results.json').read_text('utf-8'))
 assert len(records)==len(formats)==7 and all(row['result']['success'] for row in records),records
 passed=len(records)
except BaseException:failure=traceback.format_exc();raise
finally:
 qa_cleanup.remove_fixture=cleanup
 (out/'replay-preview-results.json').write_text(json.dumps({'passed':passed,'failure':failure,'objectFormat':expected_hash,'refFormat':expected_refs,'formatsVerified':len(formats),'longPath':long_path},indent=2),'utf-8')
