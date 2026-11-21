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
profile.write_text(json.dumps({'Locale':'en_US','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False,'ExternalMergeToolType':0}),'utf-8')
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
def select_file(repository,file):
 client.tool('ui_diff',repo=str(repository),action='select',source='history',file=file)
 return wait(lambda: v if (v:=ready('main')) and v['file']==file else None)
def open_pair(repository=repo):
 client.tool('ui_open_repository',repo=str(repository));client.tool('ui_select_commit',repo=str(repository),revision='HEAD')
 wait(lambda:any(Path(r['repo'])==repository and filename in (r['detailChanges'] or []) for r in client.tool('ui_state')['repositories']))
 select_file(repository,filename);client.tool('ui_command',command='external_diff');wait(lambda:ready('standalone'))
def dropdown_geometry(name):
 client.probe('dropdown_geometry',1,name)
 result=wait(lambda:p if (p:=client.probe('dropdown_geometry',2,name))['ready'] and p['left'] is not None else None)
 client.probe('dropdown_geometry',0,name)
 return result
def check_edges(name, result):
 check(name+' expands with both borders aligned',
       abs(result['left'])<=1 and abs(result['right']-result['boxed'])<=1,result)
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args)
 lines=['int value_'+str(i)+' = '+str(i)+';\n' for i in range(120)]
 lines[5]='/* '+('long wrapped comparison text '*24)+'*/\n'
 other_file='second.c'
 for name in (filename,other_file):(repo/name).write_text(''.join(lines),'utf-8')
 git('add','.');git('commit','-m','Initial example')
 lines[5]='// Short replacement\n';lines[60]='int value_60 = 999;\n';lines[90]='/* '+('long replacement on the other side '*22)+'*/\n'
 for name in (filename,other_file):(repo/name).write_text(''.join(lines),'utf-8')
 git('add','.');git('commit','-m','Change example values')
 git('remote','add','origin','https://example.invalid/demo.git')
 git('update-ref','refs/remotes/origin/main','HEAD')
 git('config','branch.main.remote','origin');git('config','branch.main.merge','refs/heads/main')
 before=(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'))
 client=Client();open_pair();main=ready('main');external=ready('standalone')
 check('main defaults to five lines of context',not main['columns'] and main['syntax'] and main['wrap'] and main['context']==5,main)
 check('standalone defaults to columns syntax highlighting wrapping and full file',all(external[k] for k in ['columns','syntax','wrap']) and external['context']==0,external)
 check('the context picker offers exactly four choices in both views',
       all(v['contextChoices']==['5 lines','15 lines','50 lines','Full file'] for v in (main,external)))
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
 initial_main=ready('main')['presenters'][0]['lines']
 whole_file=ready('standalone')['presenters'][0]['lines']
 client.probe('diff_context_main',1)
 expanded_main=wait(lambda: v if (v:=ready('main')) and v['context']==15 and v['presenters'][0]['lines']>initial_main else None)
 client.probe('diff_context_main',2)
 wait(lambda: (v:=ready('main')) and v['context']==50 and v['presenters'][0]['lines']>expanded_main['presenters'][0]['lines'])
 client.probe('diff_context_main',1)
 wait(lambda: (v:=ready('main')) and v['context']==15 and v['presenters'][0]['lines']==expanded_main['presenters'][0]['lines'])
 check('main presets change the rendered amount of context',True)
 check('main context does not change the standalone full file',ready('standalone')['context']==0 and ready('standalone')['presenters'][0]['lines']==whole_file)
 client.probe('diff_context_standalone',0)
 wait(lambda: (v:=ready('standalone')) and v['context']==5 and v['presenters'][0]['lines']<whole_file)
 check('standalone context does not change the main context',ready('main')['context']==15)
 check('main context carries to another changed file in the same repository',select_file(repo,other_file)['context']==15)
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 client.tool('ui_command',command='external_diff')
 check('new standalone windows use their repository context',wait(lambda:ready('standalone'))['context']==5)
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 separate_repo=root/'Context Demo'
 subprocess.run(['git','clone','--quiet','--no-hardlinks',str(repo),str(separate_repo)],env=env,check=True,capture_output=True)
 open_pair(separate_repo)
 check('another repository starts with five lines in main and full file standalone',
       ready('main')['context']==5 and ready('standalone')['context']==0)
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 open_pair()
 saved=json.loads(profile.read_text('utf-8'));check('both profiles save after changing controls',saved['EnableDiffViewWordWrap'] is False and saved['StandaloneDiffSettings']['UseSyntaxHighlighting'] is False and saved['StandaloneDiffSettings']['EnableDiffViewWordWrap'] is True)
 (out/'independent-profiles.png').write_bytes(client.tool('ui_screenshot',window='active'))
 client.close();client=Client();open_pair();main=ready('main');external=ready('standalone')
 check('main user settings survive restarting',not main['columns'] and main['syntax'] and not main['wrap'] and main['context']==15,main)
 check('standalone user settings survive restarting independently',not external['columns'] and not external['syntax'] and external['wrap'] and external['context']==5,external)
 for locale, label, full_file in [('zh_CN','上下文','完整文件'),('zh_TW','上下文','完整檔案'),('en_US','Context','Full file')]:
  client.tool('ui_window',locale=locale)
  unit='lines' if locale=='en_US' else '行'
  choices=[f'{n} {unit}' for n in (5,15,50)]+[full_file]
  for scope, expected in [('main',15),('standalone',5)]:
   client.probe('diff_context_menu_'+scope,1)
   shown=wait(lambda: v if (v:=views().get(scope)) and v['contextLabel']==label and v['contextChoices']==choices else None)
   check(locale+' '+scope+' opens with a localized label and the saved selection',shown['selectedContext']==expected,shown)
   client.probe('diff_context_menu_'+scope,0)
  client.probe('diff_context_menu_main',1)
  compact=wait(lambda: p if (p:=client.probe('diff_context_style'))['text']==f'15 {unit}' and p['longest'] and p['centerOffset'] is not None else None)
  check(locale+' collapses to the longest caption plus the arrow and compact padding',
        36<=compact['width']-compact['longest']<=41 and compact['width']<98,compact)
  check(locale+' selected text is centered within the area left of the arrow',
        abs(compact['centerOffset'])<=1,compact)
  opened=wait(lambda: p if len((p:=client.probe('diff_context_style',1))['dropdown'])==4
              and all(e['centerOffset'] is not None for e in p['dropdown']) else None)
  check(locale+' aligns all dropdown choices with the closed text area center',
        opened['popupAligned'] and all(abs(e['centerOffset'])<=1 for e in opened['dropdown']),opened)
  check(locale+' all dropdown options show their complete captions',
        [e['Text'] for e in opened['dropdown']]==choices and all(e['fits'] for e in opened['dropdown']),opened)
  if locale=='zh_CN':(out/'context-options.png').write_bytes(client.tool('ui_screenshot'))
  client.probe('diff_context_style');client.probe('diff_context_menu_main',0)
  client.probe('diff_context_main',3);client.probe('diff_context_menu_main',1)
  full=wait(lambda:p if (p:=client.probe('diff_context_style'))['text']==full_file and p['centerOffset'] is not None else None)
  check(locale+' longest selected caption fits when the selector is closed',full['fits'] and abs(full['centerOffset'])<=1,full)
  client.probe('diff_context_menu_main',0);client.probe('diff_context_main',1)
 colors=set()
 for theme in ['Light','Dark','Blue']:
  client.tool('ui_window',theme=theme)
  client.probe('diff_context_menu_main',1)
  painted=wait(lambda:p if (p:=client.probe('diff_context_style'))['syntaxBackground'] else None)
  check(theme+' context label matches the checked syntax option',painted['contextLabelBackground']==painted['syntaxBackground'],painted)
  check(theme+' label height matches the syntax surface and the selector is centered on the row',
        abs(painted['contextLabelHeight']-painted['syntaxSurfaceHeight'])<=.5 and abs(painted['verticalOffset'])<=.5,painted)
  check(theme+' closed picker matches the file encoding picker',painted['background']==painted['encodingBackground'],painted)
  colors.add(painted['contextLabelBackground'])
  client.probe('diff_context_menu_main',0)
 check('the context label uses different colors in all three themes',len(colors)==3,colors)
 check('actual presenters restore their own profile flags',not main['presenters'][0]['wrap'] and external['presenters'][0]['wrap'] and not external['presenters'][0]['syntax'])
 check('comparison and configuration changes do not modify Git data',before==(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain')))
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 for zoom in [1,1.25,1.5]:
  client.tool('ui_window',zoom=zoom,locale='zh_CN',width=1180,height=760)
  check_edges(f'{zoom} file encoding',dropdown_geometry('DiffEncodingPicker'))
  encoding=wait(lambda:p if (p:=client.probe('diff_encoding_style'))['centerOffset'] is not None else None)
  check(f'{zoom} file encoding centers the closed text inside its text area',abs(encoding['centerOffset'])<=1,encoding)
  encoding=wait(lambda:p if len((p:=client.probe('diff_encoding_style',1))['dropdown'])==11
                and all(e['centerOffset'] is not None for e in p['dropdown']) else None)
  check(f'{zoom} encodings stay on the closed text center line',
        encoding['popupAligned'] and all(abs(e['centerOffset'])<=1 for e in encoding['dropdown']),encoding)
  client.probe('diff_encoding_style')
  client.probe('diff_context_menu_main',1)
  check_edges(f'{zoom} context',dropdown_geometry('DiffContextPicker'))
  full=wait(lambda:p if len((p:=client.probe('diff_context_style',1))['dropdown'])==4 else None)
  check(f'{zoom} complete file remains visible in the drop-down',
        next(e for e in full['dropdown'] if e['Text']=='完整文件')['fits'],full)
  client.probe('diff_context_style')
  client.probe('diff_context_menu_main',0)
  for page,name in [('diff-merge','ExternalToolSelector'),('terminal','ShellSelector')]:
   client.tool('ui_command',command='preferences',page=page)
   wait(lambda:tool_state if (tool_state:=client.tool('ui_state'))['dialogs'] else None)
   dropdown=dropdown_geometry(name)
   check_edges(f'{zoom} {page}',dropdown)
   if page=='diff-merge':check(f'{zoom} external tool retains left-aligned content',dropdown['alignment']=='Left',dropdown)
   client.tool('ui_command',command='close_dialog');wait(lambda:not client.tool('ui_state')['dialogs'])
  client.tool('ui_command',repo=str(repo),command='pull_dialog')
  wait(lambda:client.tool('ui_state')['activePage']['pendingDialog']=='Pull')
  check_edges(f'{zoom} pull branch',dropdown_geometry('PullBranch'))
  client.tool('ui_command',command='cancel_popup')
 client.tool('ui_window',zoom=1,locale='en_US')
 if '--encodings' in sys.argv[3:]:
  import codecs
  from hashlib import sha256
  check('automatic is the default display encoding',client.probe('diff_decoded_text')['encoding']==0)
  picker=client.probe('diff_encoding',0);choices=picker['labels']
  check('encoding has its own control to the left of the first-difference arrow',picker['beforeFirst'],picker)
  for locale, caption, automatic in [('zh_CN','文件编码','自动适配'),('zh_TW','檔案編碼','自動適配'),('en_US','File encoding','Automatic')]:
   client.tool('ui_window',locale=locale)
   picker=wait(lambda: p if (p:=client.probe('diff_encoding_state'))['caption']==caption and p['labelBeforePicker'] and p['labelVisible'] and p['selectedCaption']==automatic else None)
   check(locale+' displays the file encoding label to the left of its control',bool(picker))
   check(locale+' updates the displayed Automatic option without changing its selection',picker['selected']==0 and picker['labels'][0]==automatic,picker)
   check(locale+' encoding label matches the selected text size',picker['sameFontSize'],picker)
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
   ('patch-buffer-boundary',('温度' * 30000 + '\r\nfinal line').encode(),0,
    ['温度' * 30000, 'final line']),
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
