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
profile.write_text(json.dumps({'Locale':'en_US','Theme':'Light','Check4UpdatesOnStartup':False,'EnableAutoFetch':False,'ExternalMergeToolType':0,
 'ShowTagsInGraph':False,'DisplayTimeAsPeriodInHistories':True,'ShowChangesInCommitDetailByDefault':False}),'utf-8')
env={**os.environ,'TREEGIT_TEST_MENU_PROBE':'1','GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
checks=[];failure=None;client=None;filename='example.c';performance=None
def git(*args):return subprocess.check_output(['git','-C',str(repo),*args],env=env,stderr=subprocess.DEVNULL).decode('utf-8').strip()
class Client:
 def __init__(self,read_only=False):
  args=[str(exe),'--mcp']+(['--read-only'] if read_only else [])
  self.p=subprocess.Popen(args,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
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
def dropdown_geometry(name, labels=False):
 client.probe('dropdown_geometry',1,name)
 result=wait(lambda:p if (p:=client.probe('dropdown_geometry',2,name))['ready'] and p['left'] is not None and
             (not labels or p['labelOffsets'] and all(item['offset'] is not None for item in p['labelOffsets'])) else None)
 client.probe('dropdown_geometry',0,name)
 return result
def check_label_alignment(name, result, details=False):
 check(name+' options and the selected caption have the same left edge',
       all(abs(item['offset'])<=1 for item in result['labelOffsets']),result)
 if details:
  check(name+' keeps both parts of the selected option in place',
        len(result['selectedOffsets'])==2 and all(item is not None and abs(item)<=1 for item in result['selectedOffsets']),result)
def check_edges(name, result):
 check(name+' expands with both borders aligned',
       abs(result['left'])<=1 and abs(result['right']-result['boxed'])<=1,result)
def diff_calls(trace, file):
 if not trace.is_file():return 0
 count=0
 for line in trace.read_text(encoding='utf-8').splitlines():
  try: event=json.loads(line)
  except ValueError: continue
  args=event.get('argv',[])
  if event.get('event')=='start' and 'diff' in args and '--full-index' in args and file in args:count+=1
 return count
try:
 for args in [('init','-b','main'),('config','user.name','TreeGit Demo'),('config','user.email','demo@example.invalid')]:git(*args)
 lines=[f'int value_{i} = {i}; /* \u4e2d\u6587 English */\n' for i in range(120)]
 lines[5]='/* '+('long wrapped comparison text '*24)+'*/\n'
 other_file='second.c'
 for name in (filename,other_file):(repo/name).write_text(''.join(lines),'utf-8')
 if '--tab-lifetime-only' in sys.argv[3:]:
  binary_path=repo/'zz-example.bin'
  binary_path.write_bytes(bytes(32768))
 git('add','.');git('commit','-m','Initial example')
 tagged_commit=git('rev-parse','HEAD')
 git('tag','sidebar-demo')
 git('tag','-a','releases/sidebar-demo','-m','Anonymous annotated tag')
 lines[5]='// Short replacement\n';lines[60]='int value_60 = 999; /* \u4e2d\u6587 English */\n';lines[90]='/* '+('long replacement on the other side '*22)+'*/\n'
 for name in (filename,other_file):(repo/name).write_text(''.join(lines),'utf-8')
 if '--tab-lifetime-only' in sys.argv[3:]:
  binary_path.write_bytes(b'AB'+bytes(32766))
 git('add','.');git('commit','-m','Change example values')
 side_commit=git('commit-tree',git('rev-parse','HEAD^{tree}'),'-p',tagged_commit,'-m','Tagged side revision')
 git('branch','tagged-side',side_commit);git('tag','side-only',side_commit)
 git('remote','add','origin','https://example.invalid/demo.git')
 git('update-ref','refs/remotes/origin/main','HEAD')
 git('config','branch.main.remote','origin');git('config','branch.main.merge','refs/heads/main')
 before=(git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'))
 if '--tab-lifetime-only' in sys.argv[3:]:
  trace=root/'git-performance.jsonl'
  env['GIT_TRACE2_EVENT']=str(trace)
 client=Client()
 client.tool('ui_open_repository',repo=str(repo))
 if '--tab-lifetime-only' in sys.argv[3:]:
  client.tool('ui_window',width=1024,height=720,state='normal',zoom=1)
  client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
  first_tab=wait(lambda: t if (t:=client.probe('diff_commit_tab'))['selectedTab'] is not None else None)
  check('a new commit detail opens on Changes even with the legacy preference set to false',first_tab['selectedTab']==1,first_tab)
  select_file(repo,filename)
  calls_before=diff_calls(trace,other_file)
  started=time.perf_counter()
  select_file(repo,other_file)
  file_ready_ms=round((time.perf_counter()-started)*1000,1)
  selected_file_calls=diff_calls(trace,other_file)-calls_before
  check('selecting a fresh file starts one Git patch query',selected_file_calls==1,selected_file_calls)
  reuse=wait(lambda: value if (value:=client.probe('diff_read_reuse'))['ready'] else None)
  check('four settings checks reuse the current pending query',reuse['repeatedChecks']==4 and reuse['readRequests']==1,reuse)
  check('changed options and explicit refresh still start a new query',
        reuse['optionChange'] and reuse['optionRevert'] and reuse['explicitRefresh'],reuse)
  performance={'fileReadyMs':file_ready_ms,'gitPatchProcesses':selected_file_calls,**reuse}
  client.tool('ui_diff',repo=str(repo),action='select',source='history',file=binary_path.name)
  def binary_state():return client.tool('ui_diff',repo=str(repo),action='state')
  wait(lambda: (s:=binary_state())['status']=='ready' and s['selectedOffset']==0)
  client.tool('ui_screenshot')
  initial=binary_state()
  client.tool('ui_diff',repo=str(repo),action='next')
  client.tool('ui_screenshot')
  same_view=binary_state()
  check('binary selection redraw shares the visible snapshot without reading it again',
        initial['visiblePairReads']>0 and same_view['selectedOffset']==1 and same_view['topOffset']==initial['topOffset'] and
        same_view['visiblePairReads']==initial['visiblePairReads'],(initial,same_view))
  client.tool('ui_diff',repo=str(repo),action='goto',offset=128)
  client.tool('ui_screenshot')
  scrolled=binary_state()
  check('both binary panes share one snapshot read when the viewport moves',
        scrolled['topOffset']>initial['topOffset'] and scrolled['visiblePairReads']==initial['visiblePairReads']+1 and
        set(scrolled['newHex'])=={'0'},scrolled)
  performance['binarySelectionReads']=same_view['visiblePairReads']-initial['visiblePairReads']
  performance['binaryViewportReads']=scrolled['visiblePairReads']-initial['visiblePairReads']
  print('PERFORMANCE',json.dumps(performance),flush=True)
  select_file(repo,filename)
  retired=wait(lambda: value if (value:=client.probe('diff_retired_line'))['ready'] else None)
  check('a queued draw for a removed document line is safe',retired['retired'] and retired['deleted'],retired)
  check('outdated selection and missing navigation survive a document reset',
        retired['missingNavigationSafe'] and retired['staleBounds']<=1,retired)
  check('a stale patch range is rejected safely',retired['rejectedInvalidPatch'],retired)
  second_repo=root/'Another Demo'
  subprocess.run(['git','clone','--quiet','--no-hardlinks',str(repo),str(second_repo)],env=env,check=True,capture_output=True)
  client.tool('ui_open_repository',repo=str(second_repo))
  client.tool('ui_select_commit',repo=str(second_repo),revision='HEAD')
  wait(lambda:any(Path(r['repo'])==second_repo and other_file in (r['detailChanges'] or []) for r in client.tool('ui_state')['repositories']))
  select_file(second_repo,other_file)
  indexes={Path(tab['repo']).resolve():tab['index'] for tab in client.tool('ui_tabs',action='list')['tabs'] if tab['repo']}
  for turn in range(5):
   for path,file in ((repo,filename),(second_repo,other_file)):
    client.tool('ui_tabs',action='select',index=indexes[path.resolve()])
    wait(lambda: (v:=ready('main')) and v['file']==file)
    if turn==2:client.tool('ui_refresh',repo=str(path))
  check('switching two tabs with loaded text comparisons preserves each displayed file',True)
  client.tool('ui_tabs',action='close',index=indexes[second_repo.resolve()])
  check('closing a tab with a text comparison restores the other tab',
        wait(lambda: (v:=ready('main')) and v['file']==filename))
  for locale in ('zh_CN','zh_TW','en_US'):
   client.tool('ui_window',locale=locale)
   for tab in ('general','appearance','signing'):
    client.tool('ui_command',command='preferences',page=tab)
    size=wait(lambda: result if (result:=client.probe('diff_preferences_bounds'))['ready'] else None)
    check(f'{locale} {tab} fits inside the compact preferences window',
          size['width']==620 and size['height']==370 and size['contentHeight']<=size['visibleHeight']+1,size)
    if tab=='general':check(f'{locale} general preferences omit the default Changes checkbox',size['checkboxes']==4,size)
    client.tool('ui_command',command='close_dialog')
    wait(lambda:not client.tool('ui_state')['dialogs'])
  raise SystemExit(0)
 def tag_sidebar(action=0,name='sidebar-demo'):
  return client.probe('tag_sidebar',action,name)
 def tag_row(state,name):
  return next((row for row in state['rows'] if row['name']==name),None)
 def tag_painted(state,name,shade):
  row=tag_row(state,name)
  return row and row['color']==state[shade] and row['opacity']==1
 wait(lambda:any(r['graphTotal']>=2 and not r['graphLoading'] for r in client.tool('ui_state')['repositories']))
 if tag_sidebar()['expanded']:tag_sidebar(1)
 for theme in ('Light','Dark','Blue','Default'):
  client.tool('ui_window',theme=theme)
  client.tool('ui_select_commit',repo=str(repo),revision=tagged_commit)
  wait(lambda:(s:=tag_sidebar()) and s['groupColor']==s['secondary'])
  check(theme+' collapsed Tags header matches the secondary branch color for a tagged commit',not tag_sidebar()['expanded'])
  tag_sidebar(1)
  wait(lambda:(s:=tag_sidebar()) and s['groupColor']=='Transparent' and any(r['name']=='sidebar-demo' for r in s['rows']))
  for tree in (False,True):
   if tag_sidebar()['tree']!=tree:tag_sidebar(2)
   wait(lambda:(s:=tag_sidebar()) and s['tree']==tree and tag_row(s,'sidebar-demo'))
   client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
   wait(lambda:(row:=tag_row(tag_sidebar(),'sidebar-demo')) and row['opacity']==0)
   tag_sidebar(3)
   s=tag_sidebar()
   check(theme+(' tree' if tree else ' flat')+' manual tag selection matches the branch color and text',
         tag_painted(s,'sidebar-demo','primary') and tag_row(s,'sidebar-demo')['selected'] and tag_row(s,'sidebar-demo')['text']==s['selectionText'],s)
   tag_sidebar(5)
   client.tool('ui_select_commit',repo=str(repo),revision=tagged_commit)
   s=wait(lambda:s if tag_painted(s:=tag_sidebar(),'sidebar-demo','primary') else None)
   check(theme+(' tree' if tree else ' flat')+' selecting a tagged commit paints the tag without selecting a tag action',
         s['groupColor']=='Transparent' and not tag_row(s,'sidebar-demo')['selected'] and tag_row(s,'sidebar-demo')['text']==s['selectionText'],s)
   if tree:
    if tag_row(s,'releases/sidebar-demo'):tag_sidebar(4,'releases')
    check(theme+' collapsed tag folder uses the secondary branch color',wait(lambda:tag_painted(tag_sidebar(),'releases','secondary')))
    tag_sidebar(4,'releases')
    check(theme+' expanding the tag folder moves the highlight to its matching tag',
          wait(lambda:tag_painted(s:=tag_sidebar(),'releases/sidebar-demo','primary') and not tag_painted(s,'releases','secondary')))
    tag_sidebar(4,'releases')
   else:
    check(theme+' both lightweight and annotated tags on the commit are highlighted',tag_painted(s,'releases/sidebar-demo','primary'),s)
  tag_sidebar(1)
  check(theme+' closing Tags moves the highlight back to the group header',
        wait(lambda:(s:=tag_sidebar()) and not s['expanded'] and s['groupColor']==s['secondary'] and not s['rows']))
  client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
  check(theme+' collapsed header clears for an untagged commit',wait(lambda:tag_sidebar()['groupColor']=='Transparent'))
 client.tool('ui_search',repo=str(repo),method='sha',query=tagged_commit)
 wait(lambda: (s:=client.tool('ui_search',repo=str(repo))) and not s['querying'])
 client.tool('ui_search',repo=str(repo),select=tagged_commit)
 check('collapsed Tags header follows a tagged search selection',wait(lambda:(s:=tag_sidebar()) and s['groupColor']==s['secondary']))
 tag_sidebar(1)
 check('expanded Tags rows follow a tagged search selection',wait(lambda:tag_painted(tag_sidebar(),'sidebar-demo','primary')))
 client.tool('ui_select_view',repo=str(repo),view='history')
 check('leaving search clears the tag highlight for the restored untagged commit',
       wait(lambda:(s:=tag_sidebar()) and s['groupColor']=='Transparent' and not tag_painted(s,'sidebar-demo','primary')))
 tag_sidebar(1)
 client.tool('ui_window',theme='Light')
 if tag_sidebar()['tree']:tag_sidebar(2)
 def repository_state():
  return next(r for r in client.tool('ui_state')['repositories'] if Path(r['repo'])==repo)
 def highlighted_commit(sha):
  state=repository_state()
  scope=state['commitBranchHighlights']
  return state if scope and scope['sha']==sha and state['detailCommit']==sha and not state['detailBranchesLoading'] else None
 client.tool('ui_select_commit',repo=str(repo),revision=tagged_commit)
 baseline=wait(lambda:highlighted_commit(tagged_commit))
 client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
 tag_sidebar(1)
 wait(lambda:tag_row(tag_sidebar(),'sidebar-demo'))
 check('the tag list has no eye-filter buttons',tag_sidebar()['filterButtons']==0)
 for tree in (False,True):
  if tag_sidebar()['tree']!=tree:tag_sidebar(2)
  wait(lambda:(s:=tag_sidebar()) and s['tree']==tree and tag_row(s,'sidebar-demo'))
  if tree:
   check('the tag tree has no eye-filter buttons',tag_sidebar()['filterButtons']==0)
   client.tool('ui_search',repo=str(repo),method='sha',query=tagged_commit)
   wait(lambda:not client.tool('ui_search',repo=str(repo))['querying'])
  tag_sidebar(6)
  actual=wait(lambda:highlighted_commit(tagged_commit))
  keys=('commitBranchHighlights','sidebarSelectedBranch','localBranchSelection','remoteBranchSelection')
  check(('tree' if tree else 'flat')+' tag click matches direct history selection and returns from search',
        not actual['searchActive'] and all(actual[k]==baseline[k] for k in keys),(baseline,actual))
  check(('tree' if tree else 'flat')+' click retains the tag action selection',tag_row(tag_sidebar(),'sidebar-demo')['selected'])
  client.tool('ui_select_commit',repo=str(repo),revision='HEAD')
  wait(lambda:repository_state()['detailCommit']==before[0])
 client.tool('ui_graph',repo=str(repo),mode='current',showRemotes=False)
 wait(lambda:(s:=repository_state()) and not s['graphLoading'] and all(c['sha']!=side_commit for c in s['graphCommits']))
 wait(lambda:tag_row(tag_sidebar(),'side-only'))
 tag_sidebar(6,'side-only')
 outside=wait(lambda:highlighted_commit(side_commit))
 check('tag outside the graph still highlights its branch without changing the graph filter',
       outside['graphMode']==1 and not outside['graphShowRemotes'] and outside['sidebarSelectedBranch']=='refs/heads/tagged-side',outside)
 client.tool('ui_graph',repo=str(repo),mode='all',showRemotes=True)
 wait(lambda:not repository_state()['graphLoading'])
 client.tool('ui_select_commit',repo=str(repo),revision=tagged_commit)
 probe=wait(lambda:p if (p:=client.tool('ui_state')['layoutProbe'])['taggedRefs'] and p['historyDates'] else None)
 check('legacy hidden-tag preference does not hide rendered commit tags',any('sidebar-demo' in r['refs'] for r in probe['taggedRefs']),probe['taggedRefs'])
 check('legacy relative-time preference still renders actual history dates',all(t['actual']==t['formatted'] for t in probe['historyDates']),probe['historyDates'])
 for locale,removed in (
     ('en_US',('Show tags in commit graph','Show relative time in commit graph')),
     ('zh_CN',('在提交图谱中显示标签','在提交图谱中显示相对时间')),
     ('zh_TW',('在圖譜中顯示標籤','在提交圖譜中顯示相對時間'))):
  client.tool('ui_window',locale=locale)
  client.tool('ui_command',command='preferences',page='general')
  dialog=wait(lambda:next((d for d in client.tool('ui_state')['layoutProbe']['dialogs'] if d['type']=='Preferences' and d['checkboxes']),None))
  check(locale+' general preferences omit both graph display switches',not set(removed)&set(dialog['checkboxes']),dialog)
  client.tool('ui_command',command='close_dialog')
  wait(lambda:not client.tool('ui_state')['dialogs'])
 client.tool('ui_window',locale='en_US')
 tag_sidebar(1)
 if tag_sidebar()['tree']:tag_sidebar(2)
 client.tool('ui_select_commit', repo=str(repo), revision='HEAD')
 wait(lambda: filename in (repository_state()['detailChanges'] or []))
 select_file(repo, filename)
 for theme in ('Light', 'Dark', 'Blue', 'Default'):
  client.tool('ui_window', theme=theme)
  selection=wait(lambda: state if (state:=client.probe('content_selection'))['ready'] else None)
  check(theme+' text fields, metadata and file diff use the same readable primary branch palette',
        selection['backgrounds']==[selection['primary']]*3 and
        selection['foregrounds']==[selection['primaryForeground']]*3 and not selection['diffBorder'], selection)
  check(theme+' dragging metadata preserves glyph positions and copied text',
        selection['stable'] and selection['selectedText']==selection['expectedText'], selection)
 check('Windows local paths use backslashes and URLs retain their syntax',
       selection['local']==r'C:\demo\sample\file.txt' and
       selection['remote']==[r'C:\demo\repo.git', r'\\server\share\repo.git', 'https://example.invalid/a/b.git',
                              'git@example.invalid:a/b.git', '../other/demo.git'], selection)
 for theme in ('Light','Dark','Blue'):
  client.tool('ui_window',theme=theme)
  for page in ('commit_info','commit_files','commit_changes'):
   client.tool('ui_command',command=page,repo=str(repo))
   painted=wait(lambda: p if (p:=client.probe('commit_file_selection'))['ready'] else None)
   check(theme+' '+page+' uses the same readable file selection colors',
         painted['background']==painted['expectedBackground'] and painted['foreground']==painted['expectedForeground'] and
         painted['labels'] and all(label==painted['expectedForeground'] for label in painted['labels']),painted)
 def commit_divider():
  return next((s for s in client.tool('ui_layout')['splitters'] if s['name']=='CommitInfoFilesSplitter' and s['visible']),None)
 divider=wait(commit_divider)
 client.tool('ui_layout',action='drag',splitter=divider['index'],dy=35)
 moved=wait(lambda: p if (p:=commit_divider()) and p['actualRows'][0]-divider['actualRows'][0]>15 else None)
 check('dragging the commit divider down trades file area height for commit information',
       moved['actualRows'][2]<divider['actualRows'][2] and abs(sum(moved['actualRows'])-sum(divider['actualRows']))<2,moved)
 client.tool('ui_command',command='commit_files',repo=str(repo))
 client.tool('ui_command',command='commit_changes',repo=str(repo))
 restored=wait(commit_divider)
 check('reopening the changes panel restores the repository divider position',
       abs(restored['actualRows'][0]-moved['actualRows'][0])<2,restored)
 client.tool('ui_layout',action='drag',splitter=restored['index'],dy=-35)
 check('dragging the commit divider up increases file list height',
       wait(lambda: p if (p:=commit_divider()) and p['actualRows'][2]-restored['actualRows'][2]>15 else None))
 client.tool('ui_window',theme='Dark')
 open_pair();main=ready('main');external=ready('standalone')
 for scope, count in (('main', 1), ('standalone', 2)):
  selection=wait(lambda: result if (result:=client.probe('diff_selection_colors',value=scope))['ready'] else None)
  check('Dark syntax highlighting preserves readable selected text in '+scope,
        len(selection['panes'])==count and all(p['readable'] and p['unchangedSyntax'] and p['copiedText']=='int'
            for p in selection['panes']),selection)
  check('Selecting the space between Chinese and English keeps glyph positions and wrapping fixed in '+scope,
        all(p['fixedLayout'] and p['spaceText']==' ' for p in selection['panes']),selection)
 client.tool('ui_window', theme='Light')
 check('main defaults to five lines of context',not main['columns'] and main['syntax'] and main['wrap'] and main['context']==5,main)
 check('standalone defaults to columns with syntax highlighting, no wrapping and full file',
       external['columns'] and external['syntax'] and not external['wrap'] and external['context']==0,external)
 check('the context picker offers exactly four choices in both views',
       all(v['contextChoices']==['5 lines','15 lines','50 lines','Full file'] for v in (main,external)))
 check('both standalone text presenters receive unwrapped syntax-highlighted defaults',all(not p['wrap'] and p['syntax'] for p in external['presenters']),external)
 check('standalone actually loads the complete file',all(p['lines']>=120 for p in external['presenters']),external)
 client.probe('diff_toggle_standalone',2,'true')
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
 for locale,zoom,branches,orders in (
      ('zh_CN',1,('所有分支','当前分支'),('按日期排序','按拓扑排序')),
      ('zh_TW',1.25,('全部分支','目前分支'),('依日期排序','依拓撲排序')),
      ('en_US',1.5,('All branches','Current branch'),('Sort by date','Sort topologically'))):
  client.tool('ui_window',locale=locale,zoom=zoom,width=1180,height=760)
  for picker,choices in [('HistoryBranchPicker',branches),('HistoryOrderPicker',orders)]:
   def geometry(action=2):return client.probe('dropdown_geometry',action,picker)
   width=None
   for index,caption in enumerate(choices):
    geometry(3+index)
    closed=wait(lambda:p if (p:=geometry())['text']==caption and p['centerOffset'] is not None else None)
    check(f'{locale} {zoom} {caption} fits and centers in a compact selector',
          34<=closed['boxed']-closed['longest']<=42 and closed['fits'] and abs(closed['centerOffset'])<=1,closed)
    if width is None:width=closed['boxed']
    else:check(f'{locale} {picker} keeps its width when switching options',abs(width-closed['boxed'])<=.5,closed)
   geometry(1)
   opened=wait(lambda:p if len((p:=geometry())['items'])==2 and p['left'] is not None and
                  all(item['centerOffset'] is not None for item in p['items']) else None)
   check_edges(f'{locale} {picker}',opened)
   check(f'{locale} {picker} options use the same text center as the closed box',
         [item['text'] for item in opened['items']]==list(choices) and
         all(item['fits'] and abs(item['centerOffset'])<=1 for item in opened['items']),opened)
   geometry(0)
   geometry(3)
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
  encoding=wait(lambda:p if (p:=client.probe('diff_encoding_style',2))['staticScrollbar'] and
                p['dropdown'] and all(e['centerOffset'] is not None for e in p['dropdown']) else None)
  check(f'{zoom} centered encoding options keep their center line with a permanent scrollbar',
        all(abs(e['centerOffset'])<=1 for e in encoding['dropdown']),encoding)
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
   dropdown=dropdown_geometry(name,labels=True)
   check_edges(f'{zoom} {page}',dropdown)
   check_label_alignment(f'{zoom} {page}',dropdown)
   if page=='diff-merge':check(f'{zoom} external tool retains left-aligned content',dropdown['alignment']=='Left',dropdown)
   client.tool('ui_command',command='close_dialog');wait(lambda:not client.tool('ui_state')['dialogs'])
  client.tool('ui_command',repo=str(repo),command='pull_dialog')
  wait(lambda:client.tool('ui_state')['activePage']['pendingDialog']=='Pull')
  branch_dropdown=dropdown_geometry('PullBranch',labels=True)
  check_edges(f'{zoom} pull branch',branch_dropdown)
  check_label_alignment(f'{zoom} pull branch',branch_dropdown)
  client.tool('ui_command',command='cancel_popup')
  client.tool('ui_command',repo=str(repo),command='repository_settings')
  wait(lambda:client.tool('ui_state')['dialogs'])
  client.tool('ui_layout',window='active',action='test_click',button='RepositorySettingsCredentials')
  wait(lambda:len(client.tool('ui_state')['dialogs'])==2)
  remote_dropdown=dropdown_geometry('CredentialRemote',labels=True)
  check_label_alignment(f'{zoom} credential remote name',remote_dropdown)
  client.tool('ui_command',command='close_dialog');client.tool('ui_command',command='close_dialog')
  wait(lambda:not client.tool('ui_state')['dialogs'])
 for locale,theme in [('zh_CN','Light'),('zh_TW','Dark'),('en_US','Blue')]:
  client.tool('ui_window',zoom=1,locale=locale,theme=theme)
  for page,names in [('general',('DateFormatPicker',)),('appearance',('ThemePicker','InterfaceFontPicker','CodeFontPicker')),
                     ('signing',('SigningFormatBox',)),('git',('LineEndingPicker',))]:
   client.tool('ui_command',command='preferences',page=page)
   wait(lambda:client.tool('ui_state')['dialogs'])
   for name in names:
    dropdown=dropdown_geometry(name,labels=True)
    check_edges(f'{locale} {name}',dropdown)
    check_label_alignment(f'{locale} {name}',dropdown,details=name in ('DateFormatPicker','SigningFormatBox','LineEndingPicker'))
   client.tool('ui_command',command='close_dialog')
   wait(lambda:not client.tool('ui_state')['dialogs'])
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
 # Exercise adjacent changes separated by one unchanged row, including unequal
 # side-by-side lengths and the real Stage action. All writes use this fixture.
 hover_repo=root/'Hover and MCP Demo'
 hover_repo.mkdir()
 def hover_git(*args):
  return subprocess.check_output(['git','-C',str(hover_repo),*args],env=env,stderr=subprocess.PIPE).decode('utf-8').strip()
 hover_git('init','-b','main');hover_git('config','user.name','TreeGit Hover Demo')
 hover_git('config','user.email','treegit-hover@example.invalid')
 file_name='hover.c'
 first_old='int first = 1;'
 first_new='int first = 100; // '+('wrapped sample '*24).rstrip()
 extra='int extra = 8;'
 middle='// unchanged between blocks'
 last_old='int second = 2;'
 last_new='int second = 200;'
 (hover_repo/file_name).write_text('\n'.join(['// preceding context',first_old,middle,last_old,'// trailing context'])+'\n','utf-8',newline='\n')
 standalone_file='standalone.c'
 (hover_repo/standalone_file).write_bytes((hover_repo/file_name).read_bytes())
 (hover_repo/'mcp.txt').write_text('before\n','utf-8');hover_git('add','.');hover_git('commit','-m','Hover fixture')
 parent_commit=hover_git('rev-parse','HEAD')
 (hover_repo/file_name).write_text('\n'.join(['// preceding context',first_new,extra,middle,last_new,'// trailing context'])+'\n','utf-8',newline='\n')
 (hover_repo/'mcp.txt').write_text('after\n','utf-8')
 (hover_repo/'untracked.txt').write_text('keep me\n','utf-8')
 client.tool('ui_open_repository',repo=str(hover_repo))
 wait(lambda:any(Path(s['repo'])==hover_repo and file_name in (s.get('workingCopyState') or {}).get('unstaged',[]) for s in client.tool('ui_state')['repositories']))
 client.tool('ui_diff',repo=str(hover_repo),action='select',file=file_name,source='unstaged')
 wait(lambda:views().get('main') and client.probe('diff_decoded_text')['file']==file_name)
 def hover(text,side=0):
  return wait(lambda:s if (s:=client.probe('diff_working_hover',side,text))['ready'] else None)
 first_block=[first_old,first_new.rstrip(),extra]
 for columns,wrap in ((False,False),(False,True),(True,True),(True,False)):
  client.probe('diff_toggle_main',0,str(columns).lower())
  client.probe('diff_toggle_main',2,str(wrap).lower())
  wait(lambda:(v:=ready('main')) and v['columns']==columns and v['wrap']==wrap)
  state=hover(first_old)
  check(f'working copy hover selects only the first changed block with columns={columns} wrap={wrap}',
        state['wrap']==wrap and [r['Content'] for r in state['changed']]==first_block and
        set(state['visualRows'])<= {'Added','Deleted','None'},state)
  if columns:
   aligned=hover('$alignment')
   check(f'hovering alignment padding selects the same changed block with wrap={wrap}',
         aligned['changed']==state['changed'] and 'None' in aligned['visualRows'],aligned)
  check(f'an unchanged row has no hover action with columns={columns} wrap={wrap}',not hover(middle)['selected'])
  check(f'hover selects the independent neighboring change with columns={columns} wrap={wrap}',
        [r['Content'] for r in hover(last_old)['changed']]==[last_old,last_new])
  check(f'the hunk heading has no hover action with columns={columns} wrap={wrap}',not hover('$hunk')['selected'])
 hover(first_old)
 client.probe('diff_working_hover_stage')
 cached=wait(lambda:patch if (patch:=hover_git('diff','--cached','--',file_name)) else None)
 check('the Stage button applies just the visibly hovered change',first_new.rstrip() in cached and last_new not in cached,cached)
 staged=client.tool('git_action',repo=str(hover_repo),action='stage',paths=['mcp.txt'])
 check('TreeGit MCP stages only the requested path',staged['exitCode']==0 and set(hover_git('diff','--cached','--name-only').splitlines())=={file_name,'mcp.txt'})
 no_write=client.tool('git_status',repo=str(hover_repo))
 check('TreeGit MCP sees both staged and remaining unstaged changes',
       no_write['isSuccess'] and '1 MM ' in no_write['stdout'] and '? untracked.txt' in no_write['stdout'],no_write)
 message='TreeGit MCP fixture commit'
 committed=client.tool('git_action',repo=str(hover_repo),action='commit',message=message)
 head=hover_git('rev-parse','HEAD')
 result=client.tool('git_show',repo=str(hover_repo),revision=head)
 check('TreeGit MCP creates one commit with the supplied message and configured identity',
       committed['exitCode']==0 and hover_git('rev-parse','HEAD^')==parent_commit and hover_git('log','-1','--format=%s')==message
       and hover_git('log','-1','--format=%an <%ae>%n%cn <%ce>')==('TreeGit Hover Demo <treegit-hover@example.invalid>\n'*2).strip()
       and result['isSuccess'] and head in result['stdout'],(committed,result))
 check('MCP commits staged bytes and preserves unstaged and untracked files',
       first_new.rstrip() in hover_git('show','HEAD:'+file_name) and last_old in hover_git('show','HEAD:'+file_name) and
       last_new in hover_git('diff','--',file_name) and hover_git('show','HEAD:mcp.txt')=='after' and
       not hover_git('diff','--cached','--name-only') and (hover_repo/'untracked.txt').read_text('utf-8')=='keep me\n')
 # Use the actual external-compare entry and the original inline buttons in
 # the separate window. Keep this separate from the preceding commit fixture.
 (hover_repo/standalone_file).write_bytes((hover_repo/file_name).read_bytes())
 def history_working_state():
  return next(s['workingCopyState'] for s in client.tool('ui_state')['repositories'] if Path(s['repo'])==hover_repo)
 def select_standalone_change(source):
  client.tool('ui_diff',repo=str(hover_repo),action='select',file=standalone_file,source=source)
  wait(lambda:views().get('main') and client.probe('diff_decoded_text')['file']==standalone_file)
  client.tool('ui_command',command='external_diff')
  return wait(lambda:s if (v:=views().get('standalone')) and v['file']==standalone_file and
              (s:=client.probe('diff_standalone_state'))['ready'] and s['IsTextDiff'] else None)
 def standalone_hover(text,side=0):
  return wait(lambda:s if (s:=client.probe('diff_standalone_hover',side,text))['ready'] and s['selected'] else None)
 def standalone_patch(button):
  return client.probe('diff_standalone_chunk',0,button)
 wait(lambda:standalone_file in history_working_state()['unstaged'])
 window=select_standalone_change('unstaged')
 check('TreeGit external comparison opens an editable worktree diff',window['canEdit'])
 check('the standalone Stage action targets the hovered lines',standalone_hover(first_old)['changed']==state['changed'])
 standalone_patch('BtnStageChunk')
 staged_patch=wait(lambda:patch if (patch:=hover_git('diff','--cached','--',standalone_file)) else None)
 check('standalone Stage updates the index and the main staged-file list',
       first_new in staged_patch and last_new not in staged_patch and wait(lambda:standalone_file in history_working_state()['staged']))
 check('standalone reloads the unstaged diff after Stage',wait(lambda:(s:=client.probe('diff_standalone_state'))['ready'] and
       s['lines'] and not any(l['Content']==first_old and l['type']=='Deleted' for l in s['lines']) and
       any(l['Content']==last_new and l['type']=='Added' for l in s['lines'])))
 standalone_hover(last_old)
 standalone_patch('BtnDiscardChunk')
 wait(lambda:last_old in (hover_repo/standalone_file).read_text('utf-8'))
 check('standalone Discard updates the worktree without changing the staged block',
       first_new in (hover_repo/standalone_file).read_text('utf-8') and last_new not in (hover_repo/standalone_file).read_text('utf-8') and
       staged_patch==hover_git('diff','--cached','--',standalone_file))
 check('both windows show the effect of discarding the last unstaged block',
       wait(lambda:(s:=client.probe('diff_standalone_state'))['ready'] and not s['IsTextDiff'] and
            standalone_file not in history_working_state()['unstaged']))
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 select_standalone_change('staged')
 standalone_hover(first_old)
 standalone_patch('BtnUnstageChunk')
 check('standalone Unstage clears the index and preserves the worktree edit',
       wait(lambda: not hover_git('diff','--cached','--',standalone_file) and
            standalone_file in history_working_state()['unstaged']) and first_new in hover_git('diff','--',standalone_file))
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 client.close();client=Client(read_only=True)
 client.tool('ui_open_repository',repo=str(hover_repo))
 wait(lambda:standalone_file in (history_working_state() or {}).get('unstaged',[]))
 check('read-only MCP still opens an inspector without patch actions',
       not select_standalone_change('unstaged')['canEdit'])
 # Thousands of refs are unnecessary to exercise virtualization: these
 # lists are several viewports tall even with the largest default window.
 client.tool('ui_command',command='close_dialog');wait(lambda:not views().get('standalone'))
 scroll_repo=root/'Sidebar Scroll Demo'
 scroll_repo.mkdir()
 def scroll_git(*args,input_text=None,date=None):
  fixture_env=env if date is None else {**env,'GIT_AUTHOR_DATE':date,'GIT_COMMITTER_DATE':date}
  result=subprocess.run(['git','-C',str(scroll_repo),*args],env=fixture_env,input=input_text.encode('utf-8') if input_text else None,capture_output=True)
  if result.returncode:raise RuntimeError(result.stderr.decode('utf-8',errors='replace').strip())
  return result.stdout.decode('utf-8').strip()
 scroll_git('init','-b','main');scroll_git('config','user.name','TreeGit Sidebar Demo')
 scroll_git('config','user.email','sidebar@example.invalid')
 scroll_git('remote','add','origin','https://example.invalid/sidebar.git')
 scroll_git('commit','--allow-empty','-m','Before sidebar anchor',date='2024-01-02T00:00:00+00:00')
 base=scroll_git('rev-parse','HEAD')
 # The matching tag must start off screen with the default creator-date sort.
 target=scroll_git('commit-tree',scroll_git('rev-parse','HEAD^{tree}'),'-p',base,'-m','Sidebar anchor',date='2024-01-01T00:00:00+00:00')
 local='refs/heads/group/z-primary'
 remote='refs/remotes/origin/group/z-primary'
 tag='group/z-position'
 refs=[(f'{prefix}/a{i:02}',base) for prefix in ('refs/heads/group','refs/remotes/origin/group','refs/tags/group') for i in range(44)]
 refs.extend(((local,target),(remote,target),('refs/tags/'+tag,target)))
 scroll_git('update-ref','--stdin',input_text=''.join(f'update {ref} {sha}\n' for ref,sha in refs))
 scroll_git('config','branch.group/z-primary.remote','origin')
 scroll_git('config','branch.group/z-primary.merge','refs/heads/group/z-primary')
 def scroll_state():
  return next(s for s in client.tool('ui_state')['repositories'] if Path(s['repo'])==scroll_repo)
 def sidebar(group,action=0,folder=None):
  return client.probe('sidebar_reveal',action,group+('|' + folder if folder else ''))
 def visible_in(group,item):
  try:
   return wait(lambda:s if (s:=sidebar(group))['open'] and s['offset']>0 and item in s['visible'] else None)
  except AssertionError:
   raise AssertionError((group, item, sidebar(group))) from None
 def select_sidebar_commit(sha):
  client.tool('ui_select_commit',repo=str(scroll_repo),revision=sha)
  return wait(lambda:s if (s:=scroll_state())['detailCommit']==sha and
              (s['commitBranchHighlights'] or {}).get('sha')==sha and not s['detailBranchesLoading'] else None)
 client.tool('ui_window',zoom=1,width=1180,height=720)
 client.tool('ui_open_repository',repo=str(scroll_repo))
 wait(lambda:not scroll_state()['graphLoading'] and len(scroll_state()['loadedBranches'])>=90)
 if tag_sidebar()['tree']:tag_sidebar(2)
 groups=('local','remote','tags')
 for section in groups: sidebar(section,1)
 selection=select_sidebar_commit(target)
 check('the target uses the tracked local primary branch in the large-sidebars fixture',
       selection['commitBranchHighlights']['primary']==local,selection)
 check('selecting the commit keeps all intentionally collapsed sections closed',
       all(not sidebar(section)['open'] for section in groups))
 sidebar('local',2)
 wait(lambda:'refs/heads/group' in sidebar('local')['rows'])
 check('opening the branch section leaves its nested folder closed',local not in sidebar('local')['rows'])
 sidebar('local',3,'refs/heads/group')
 check('expanding the local branch folder scrolls to its highlighted leaf',visible_in('local',local)['offset']>0)
 sidebar('remote',2)
 wait(lambda:'refs/remotes/origin' in sidebar('remote')['rows'])
 if 'refs/remotes/origin/group' not in sidebar('remote')['rows']:sidebar('remote',3,'refs/remotes/origin')
 sidebar('remote',3,'refs/remotes/origin/group')
 check('expanding the remote-tracking folder scrolls to its highlighted leaf',visible_in('remote',remote)['offset']>0)
 sidebar('tags',2)
 check('opening the flat Tags section scrolls to the matching tag',visible_in('tags',tag)['offset']>0)
 tag_sidebar(2)
 wait(lambda:'group' in sidebar('tags')['rows'] and tag not in sidebar('tags')['rows'])
 sidebar('tags',3,'group')
 check('expanding the tag folder scrolls to its matching leaf',visible_in('tags',tag)['offset']>0)
 for section,item in zip(groups,(local,remote,tag)):
  sidebar(section,1);sidebar(section,2)
  check(section+' section restores its highlighted row when reopened',visible_in(section,item)['offset']>0)
 select_sidebar_commit(base)
 for section in groups:sidebar(section,4)
 check('the primary branch and tag start off-screen before selecting the second commit',
       all(item not in sidebar(section)['visible'] for section,item in zip(groups,(local,remote,tag))))
 select_sidebar_commit(target)
 for section,item in zip(groups,(local,remote,tag)):
  check('selecting a commit scrolls its '+section+' list to the matching row',visible_in(section,item)['offset']>0)
except SystemExit:
 raise
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
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failure':failure,'performance':performance},indent=2),'utf-8')
if failure:raise SystemExit(1)
