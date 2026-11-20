"""Actual views, independent toggle bindings, full-file mode and restart persistence."""
import base64,json,os,queue,subprocess,sys,threading,time,traceback
from pathlib import Path
from qa_cleanup import new_fixture,remove_fixture,verify_process
from qa_process import close_owned_gui
from qa_profile import IsolatedProfile

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
root=new_fixture(out,'TreeGit comparison profiles ');repo=root/'Demo';repo.mkdir()
isolated=IsolatedProfile(exe,out)
profile=isolated.data/'preference.json'
profile.write_text(json.dumps({'Locale':'en_US','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False}),'utf-8')
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
checks=[];failure=None;client=None;filename='example.c'
def git(*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env,stderr=subprocess.DEVNULL).decode('utf-8').strip()
class Client:
 def __init__(self):
  self.p=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
  self.q=queue.Queue();self.seq=0;self.pid=None
  threading.Thread(target=lambda:[self.q.put(line) for line in self.p.stdout],daemon=True).start();self.rpc('initialize',{'protocolVersion':'2025-11-25'})
  self.pid=self.tool('ui_state')['processId'];verify_process(self.pid,exe)
 def rpc(self,method,params):
  self.seq+=1;self.p.stdin.write(json.dumps({'jsonrpc':'2.0','id':self.seq,'method':method,'params':params})+'\n');self.p.stdin.flush()
  result=json.loads(self.q.get(timeout=45));assert 'error' not in result,result;return result['result']
 def tool(self,name,**args):
  result=self.rpc('tools/call',{'name':name,'arguments':args});assert not result.get('isError'),result
  return base64.b64decode(result['content'][0]['data']) if name=='ui_screenshot' else json.loads(result['content'][0]['text'])
 def probe(self,action='diff_inspect',index=0,value=''):
  return self.tool('ui_layout',action='test_menu',text=action,value=index,button=value)
 def close(self):
  if self.pid:close_owned_gui(exe,self.pid);self.pid=None
  self.p.stdin.close()
  try:self.p.wait(timeout=10)
  except subprocess.TimeoutExpired:self.p.terminate();self.p.wait(timeout=10)
def wait(callback):
 for _ in range(160):
  value=callback()
  if value:return value
  time.sleep(.08)
 raise AssertionError('Expected comparison state did not appear')
def check(name,value,data=None):
 assert value,(name,data)
 checks.append(name);print('PASS',name,flush=True)
def views():return {view['scope']:view for view in client.probe()['views']}
def ready(scope):
 view=views().get(scope)
 return view if view and len(view['presenters'])==(2 if view['columns'] else 1) and all((p['lines'] or 0)>1 for p in view['presenters']) else None
def open_pair():
 client.tool('ui_open_repository',repo=str(repo));client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
 wait(lambda:ready('main'));client.probe('diff_open',value=filename);wait(lambda:ready('standalone'))
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args)
 lines=['int value_'+str(i)+' = '+str(i)+';\n' for i in range(120)]
 lines[5]='/* '+('long wrapped comparison text '*24)+'*/\n'
 (repo/filename).write_text(''.join(lines),'utf-8');git('add','.');git('commit','-m','Initial example')
 lines[5]='// Short replacement\n';lines[60]='int value_60 = 999;\n';lines[90]='/* '+('long replacement on the other side '*22)+'*/\n';(repo/filename).write_text(''.join(lines),'utf-8');git('add','.');git('commit','-m','Change one value')
 before=(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'))
 client=Client();open_pair();main=ready('main');external=ready('standalone')
 check('main defaults to stacked syntax highlighted wrapped changes',not main['columns'] and main['syntax'] and main['wrap'] and not main['full'],main)
 check('standalone defaults to columns syntax highlighting wrapping and full file',all(external[k] for k in ['columns','syntax','wrap','full']),external)
 check('both standalone text presenters receive wrapping and syntax settings',all(p['wrap'] and p['syntax'] for p in external['presenters']),external)
 check('standalone actually loads the complete file',all(p['lines']>=120 for p in external['presenters']),external)
 def aligned(require_wrap=True):
  value=ready('standalone')
  if not value or len(value['presenters'])!=2:return False
  a,b=({r['index']:r for r in p['rows']} for p in value['presenters']);common=set(a)&set(b)
  return len(common)>3 and (not require_wrap or any(r['segments']>1 for r in list(a.values())+list(b.values()))) and all(abs(a[i]['top']-b[i]['top'])<.75 and abs(a[i]['height']-b[i]['height'])<.5 for i in common) and abs(value['presenters'][0]['offset']-value['presenters'][1]['offset'])<.75
 wait(aligned);check('asymmetrically wrapped rows remain vertically aligned',aligned())
 client.tool('ui_layout',action='resize',window='active',width=850,height=650);wait(aligned);check('wrapped alignment follows standalone window resizing',aligned())
 (out/'wrapped-side-by-side.png').write_bytes(client.tool('ui_screenshot',window='active'))
 for row in [85,20,100,1]:
  client.probe('diff_scroll_standalone',row);wait(lambda:aligned(False));check('paired rows and scroll offsets stay aligned at line '+str(row),aligned(False))
 client.probe('diff_select_standalone');check('selecting all text excludes visual alignment padding',True)
 check('main keeps the shorter changes-only content',main['presenters'][0]['lines']<100,main)
 client.probe('diff_toggle_standalone',1,'false');wait(lambda:not views()['standalone']['syntax'])
 check('standalone syntax toggle leaves main unchanged',views()['main']['syntax'])
 client.probe('diff_toggle_main',2,'false');wait(lambda:not views()['main']['wrap'])
 check('main wrapping toggle leaves standalone unchanged',views()['standalone']['wrap'])
 client.probe('diff_toggle_standalone',0,'false');wait(lambda:ready('standalone') and not ready('standalone')['columns'])
 check('standalone layout changes independently',not views()['main']['columns'])
 saved=json.loads(profile.read_text('utf-8'));check('both profiles save after changing controls',saved['EnableDiffViewWordWrap'] is False and saved['StandaloneDiffSettings']['UseSyntaxHighlighting'] is False and saved['StandaloneDiffSettings']['EnableDiffViewWordWrap'] is True)
 (out/'independent-profiles.png').write_bytes(client.tool('ui_screenshot',window='active'))
 client.close();client=Client();open_pair();main=ready('main');external=ready('standalone')
 check('main user settings survive restarting',not main['columns'] and main['syntax'] and not main['wrap'] and not main['full'],main)
 check('standalone user settings survive restarting independently',not external['columns'] and not external['syntax'] and external['wrap'] and external['full'],external)
 check('actual presenters restore their own profile flags',not main['presenters'][0]['wrap'] and external['presenters'][0]['wrap'] and not external['presenters'][0]['syntax'])
 check('comparison and configuration changes do not modify Git data',before==(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')))
 if '--encodings' in sys.argv[3:]:
  import codecs
  from hashlib import sha256
  check('automatic is the default display encoding',client.probe('diff_decoded_text')['encoding']==0)
  picker=client.probe('diff_encoding',0);choices=picker['labels']
  check('encoding has its own control to the left of the first-difference arrow',picker['beforeFirst'],picker)
  for locale, caption in [('zh_CN','文件编码'),('zh_TW','檔案編碼'),('en_US','File encoding')]:
   client.tool('ui_window',locale=locale)
   picker=wait(lambda: p if (p:=client.probe('diff_encoding',0))['caption']==caption and p['labelBeforePicker'] and p['labelVisible'] else None)
   check(locale+' displays the file encoding label to the left of its control',bool(picker))
  check('encoding choices retain the Unicode and Chinese families',choices==['Automatic','UTF-8','UTF-16 LE','UTF-16 BE','UTF-32 LE','UTF-32 BE','GB2312','GBK','GB18030','Big5','Windows-1252'],choices)
  corpora=[
   ('mixed-lines','UTF 温度𠮷\r\n'.encode()+'GB 使用传感器\n'.encode('gb2312'),0,['UTF 温度𠮷','GB 使用传感器']),
   ('no-western-guess','Prix: 20 €'.encode('cp1252'),0,'Prix: 20 \ufffd'),
   ('manual-western','Prix: 22 €'.encode('cp1252'),10,'Prix: 22 €'),
   ('gb18030','采样𠀀结束'.encode('gb18030'),8,'采样𠀀结束'),
   ('big5','傳統字庫'.encode('big5'),9,'傳統字庫'),
   ('gb2312','新测试'.encode('gb2312'),6,'新测试'),
   ('utf16le',codecs.BOM_UTF16_LE+'状态\nline'.encode('utf-16-le'),0,'状态'),
   ('utf16be',codecs.BOM_UTF16_BE+'检查\nline'.encode('utf-16-be'),3,'检查'),
   ('utf32le',codecs.BOM_UTF32_LE+'中文\nline'.encode('utf-32-le'),4,'中文'),
   ('utf32be',codecs.BOM_UTF32_BE+'记录\nline'.encode('utf-32-be'),5,'记录'),
  ]
  client.close();client=None
  filename='encoding-sample.c'
  revisions=[]
  for label,raw,index,expected_text in corpora:
   (repo/filename).write_bytes(raw);git('add','--',filename);git('commit','-m','Anonymous encoding '+label)
   revisions.append(git('rev-parse','HEAD'))
  checksum=(sha256((repo/filename).read_bytes()).digest(),git('write-tree'),git('rev-parse','HEAD'))
  client=Client();client.tool('ui_open_repository',repo=str(repo))
  wait(lambda:any(r['graphTotal']>=len(revisions) and not r['graphLoading'] for r in client.tool('ui_state')['repositories']))
  for (label,raw,index,expected_text),wanted in zip(corpora,revisions):
   client.tool('ui_select_commit',repo=str(repo),revision=wanted)
   wait(lambda:any(r['detailCommit']==wanted and filename in (r['detailChanges'] or []) for r in client.tool('ui_state')['repositories']))
   wait(lambda:views().get('main') and client.probe('diff_decoded_text')['kind'])
   client.probe('diff_encoding',index)
   expected_rows=expected_text if isinstance(expected_text,list) else [expected_text]
   try:state=wait(lambda:s if (s:=client.probe('diff_decoded_text'))['encoding']==index and all(any(row.rstrip('\r')==text for row in (s['rows'] or [])) for text in expected_rows) else None)
   except AssertionError as error:raise AssertionError((label,client.probe('diff_decoded_text'))) from error
   check(label+' renders expected text without changing file, index, or commit',checksum==(sha256((repo/filename).read_bytes()).digest(),git('write-tree'),git('rev-parse','HEAD')),state)
  client.probe('diff_encoding',6)
  client.tool('ui_select_commit',repo=str(repo),revision=before[0])
  wait(lambda:views().get('main') and (s:=client.probe('diff_decoded_text'))['file']=='example.c' and s['kind'])
  check('a different file of the same type in this repository uses the manual encoding',client.probe('diff_decoded_text')['encoding']==6)
  (repo/'other-format.txt').write_text('A different type\n','utf-8')
  wait(lambda:any('other-format.txt' in (r.get('workingCopyState') or {}).get('unstaged',[]) for r in client.tool('ui_state')['repositories']))
  client.tool('ui_diff',repo=str(repo),action='select',file='other-format.txt',source='unstaged')
  wait(lambda:views().get('main') and (s:=client.probe('diff_decoded_text'))['file']=='other-format.txt' and s['kind'])
  check('a different file type stays automatic',client.probe('diff_decoded_text')['encoding']==0)
  other_repo=root/'Other'
  other_repo.mkdir()
  subprocess.run(['git','-C',str(other_repo),'init','-b','main'],env=env,check=True,capture_output=True)
  (other_repo/'other.c').write_text('A different repository\n','utf-8')
  client.tool('ui_open_repository',repo=str(other_repo))
  wait(lambda:any('other.c' in (r.get('workingCopyState') or {}).get('unstaged',[]) for r in client.tool('ui_state')['repositories']))
  client.tool('ui_diff',repo=str(other_repo),action='select',file='other.c',source='unstaged')
  wait(lambda:views().get('main') and (s:=client.probe('diff_decoded_text'))['file']=='other.c' and s['kind'])
  check('the same type in a different repository has no manual override',client.probe('diff_decoded_text')['encoding']==0)
  client.close();client=Client()
  client.tool('ui_open_repository',repo=str(repo));client.tool('ui_select_commit',repo=str(repo),revision=before[0])
  wait(lambda:views().get('main') and (s:=client.probe('diff_decoded_text'))['file']=='example.c' and s['kind'])
  check('the repository and type encoding is remembered after restart',client.probe('diff_decoded_text')['encoding']==6)
  client.probe('diff_encoding',0)
  check('selecting Automatic clears that type override',client.probe('diff_decoded_text')['encoding']==0)
  (repo/'sample.bin').write_bytes(b'\x00 ASCII~\x7f\x80\xff')
  wait(lambda:any('sample.bin' in (r.get('workingCopyState') or {}).get('unstaged',[]) for r in client.tool('ui_state')['repositories']))
  client.tool('ui_diff',repo=str(repo),action='select',file='sample.bin',source='unstaged')
  binary=wait(lambda: s if (s:=client.probe('diff_decoded_text'))['kind']=='BinaryDiff' else None)
  check('binary comparisons hide both the file encoding label and its selector',not binary['encodingVisible'] and not binary['labelVisible'],binary)
except BaseException:
 failure=traceback.format_exc();traceback.print_exc()
 if client:
  try:(out/'failure.png').write_bytes(client.tool('ui_screenshot',window='active'));(out/'failure-state.json').write_text(json.dumps(client.probe(),indent=2),'utf-8')
  except Exception:pass
finally:
 if client:client.close()
 if profile.exists():profile.unlink()
 isolated.close()
 remove_fixture(root,out,'TreeGit comparison profiles ')
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure},indent=2),'utf-8')
if failure:raise SystemExit(1)
