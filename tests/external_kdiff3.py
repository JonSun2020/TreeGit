"""Focused real KDiff3 close regression, with isolated settings and Git state."""
from pathlib import Path
import json,os,shutil,subprocess,sys,traceback
from qa_cleanup import new_fixture,remove_fixture
from qa_tool_window import close_owned_comparison

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
installed=Path(os.environ['TREEGIT_TEST_KDIFF3_EXECUTABLE']).resolve()
assert os.name=='nt' and installed.name=='kdiff3.exe' and installed.parent.name=='bin'
root=new_fixture(out,'TreeGit external tools ');repo=root/'repo';repo.mkdir()
(root/'treegit-owned-test').write_text('Owned disposable external tool regression\n','utf-8')
tool=root/'kdiff3'
shutil.copytree('\\\\?\\'+str(installed.parent.parent),'\\\\?\\'+str(tool),copy_function=os.link,
                ignore=shutil.ignore_patterns('Qt','QtQuick','QtQml','qml','translations','locale','test','*.obj','*.lib','*.pdb'))
config=root/'kdiff3rc';config.write_text('','utf-8')
temporary=root/'temporary';temporary.mkdir()
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1','TEMP':str(temporary),'TMP':str(temporary),
     'XDG_CONFIG_HOME':str(root/'config'),'XDG_CACHE_HOME':str(root/'cache')}
filename="example O'Brien 中文.txt";checks=[];failure=None;details=None;closed=[]
def git(*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env,stderr=subprocess.DEVNULL).decode('utf-8').strip()
def snapshot():return [git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'),(repo/filename).read_bytes()]
def check(name,condition):
 assert condition,name
 checks.append(name);print('PASS',name,flush=True)
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args)
 (repo/filename).write_text('Original anonymous text\n','utf-8');git('add','.');git('commit','-m','Initial example')
 (repo/filename).write_text('Changed anonymous text\n','utf-8');git('commit','-am','Changed example')
 before=snapshot();plan=root/'plan.json';report=out/'adapter-result.json'
 plan.write_text(json.dumps({'executable':str(tool/'bin/kdiff3.exe'),'arguments':'--config "'+config.as_posix()+'"','useSupportedArguments':True,'kind':'diff','adapter':'kdiff3'}),'utf-8')
 with close_owned_comparison(tool/'bin/kdiff3.exe','KDiff3') as closed:
  result=subprocess.run([str(exe),'--integration-external-tool',str(repo),filename,str(plan),str(report)],env=env,capture_output=True,timeout=35)
 check('KDiff3 comparison returns an actual TreeGit result',result.returncode==0 and report.exists())
 details=json.loads(report.read_text('utf-8-sig'))
 check('the actual comparison window was closed normally',bool(closed))
 check('normal KDiff3 close succeeds without a fatal diff error',details['success'] and not any('fatal:' in text.lower() for text in details['messages']))
 check('closing comparison leaves Git and both revisions unchanged',snapshot()==before)
 check('comparison uses an isolated settings file',config.stat().st_size>0)
 check('comparison temporary empty input is released',not list(temporary.glob('TreeGit-external-diff-*')))
 check('comparison does not create a merge session',not list(temporary.glob('TreeGit-external-merge-*')))
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
finally:
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'adapter':details,'closedWindows':closed},ensure_ascii=False,indent=2),'utf-8')
 remove_fixture(root,out,'TreeGit external tools ')
if failure:raise SystemExit(1)
