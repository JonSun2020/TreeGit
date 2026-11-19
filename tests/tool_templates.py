"""Exercise every Windows tool's shipped argument template against real Git files."""
from pathlib import Path
import json,os,shutil,subprocess,sys,traceback
from qa_cleanup import new_fixture,remove_fixture

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
assert os.name=='nt' and os.environ.get('CI')=='true'
helper=Path(os.environ['TREEGIT_TEST_TOOL_HELPER']).resolve();assert helper.is_file()
root=new_fixture(out,'TreeGit external templates ');repo=root/'repository 中文';repo.mkdir();temporary=root/'temporary';temporary.mkdir()
(root/'treegit-owned-test').write_text('Owned disposable template validation\n','utf-8')
program=root/"native tool O'Brien 中文";program.mkdir()
for name in ['CancellationHelper.exe','CancellationHelper.dll','CancellationHelper.deps.json','CancellationHelper.runtimeconfig.json']:
 os.link(helper.parent/name,program/name)
capture=root/'arguments.json'
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1','TEMP':str(temporary),'TMP':str(temporary),'TMPDIR':str(temporary),'TREEGIT_TEST_TOOL_CAPTURE':str(capture)}
filename="-example O'Brien 中文.txt";checks=[];failure=None;records=[]
adapters=['vscode','vscode_insiders','vs','tortoise_merge','kdiff3','beyond_compare','win_merge','codium','p4merge','plastic_merge','meld','cursor']
def git(*args,expected=0):
 result=subprocess.run(['git','-C',str(repo),*args],env=env,capture_output=True,text=True,encoding='utf-8')
 assert result.returncode==expected,(args,result.stderr)
 return result.stdout.strip()
def check(name,value):
 assert value,name
 checks.append(name);print('PASS',name,flush=True)
def invoke(adapter,kind):
 capture.unlink(missing_ok=True)
 plan=root/'plan.json';report=out/(adapter+'-'+kind+'.json')
 plan.write_text(json.dumps({'executable':str(program/helper.name),'arguments':'','useSupportedArguments':True,'adapter':adapter,'kind':kind}),'utf-8')
 mode={**env,'TREEGIT_TEST_TOOL_MERGE':'true' if kind=='merge' else 'false'}
 result=subprocess.run([str(exe),'--integration-external-tool',str(repo),filename,str(plan),str(report)],env=mode,capture_output=True,timeout=25)
 check(adapter+' '+kind+' produces an actual result',result.returncode==0 and report.exists() and capture.exists())
 details=json.loads(report.read_text('utf-8-sig'));arguments=json.loads(capture.read_text('utf-8-sig'))
 check(adapter+' '+kind+' default command succeeds',details['success'])
 check(adapter+' '+kind+' resolves every revision placeholder',not any('$'+key in value for value in arguments['arguments'] for key in ['BASE','LOCAL','REMOTE','MERGED']))
 check(adapter+' '+kind+' releases temporary input and merge files',not list(temporary.glob('TreeGit-external-*')))
 records.append({'adapter':adapter,'kind':kind,'arguments':arguments['arguments'],'inputContents':[f['text'] for f in arguments['files']]})
 return arguments
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid'),('config','core.autocrlf','false')]:git(*args)
 (repo/filename).write_bytes(b'Original\n');git('add','.');git('commit','-m','Initial example')
 git('checkout','-b','feature/demo');(repo/filename).write_bytes(b'Remote change\n');git('commit','-am','Remote example')
 git('checkout','main');(repo/filename).write_bytes(b'Local change\n');git('commit','-am','Local example')
 before=[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')]
 for adapter in adapters:
  arguments=invoke(adapter,'diff')
  check(adapter+' receives the exact old and new file bytes',[f['text'] for f in arguments['files']]==['Original\n','Local change\n'])
  check(adapter+' comparison preserves Git state',before==[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')])
 for adapter in adapters:
  git('reset','--hard','main');git('merge','--no-edit','feature/demo',expected=1)
  check(adapter+' starts with a real conflict',bool(git('ls-files','--unmerged')))
  arguments=invoke(adapter,'merge');contents=[f['text'] for f in arguments['files']]
  check(adapter+' merge receives its declared source versions',adapter=='win_merge' and len(contents)==1 or adapter!='win_merge' and set(['Original\n','Local change\n','Remote change\n']).issubset(contents))
  check(adapter+' merge command passes one editable conflict result',sum('<<<<<<<' in value for value in contents)==1)
  check(adapter+' stages only the saved merge result',not git('ls-files','--unmerged') and git('show',':'+filename)=='Resolved anonymous example' and (repo/filename).read_text('utf-8')=='Resolved anonymous example\n')
  check(adapter+' merge does not commit on behalf of the user',git('rev-parse','HEAD')==before[0])
  if adapter=='vs':check('Visual Studio slash switch survives the native command line','/m' in arguments['arguments'])
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
finally:
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'records':records,'failure':failure},ensure_ascii=False,indent=2),'utf-8')
 remove_fixture(root,out,'TreeGit external templates ')
if failure:raise SystemExit(1)
