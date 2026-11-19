"""Check remote-query cancellation, exact branch identity and useful failure output."""
from pathlib import Path
import http.server,json,os,queue,subprocess,sys,threading,time,urllib.parse
from qa_cleanup import new_fixture,remove_fixture
from qa_process import close_owned_gui

exe,out=(Path(value).resolve() for value in sys.argv[1:3]);out.mkdir(parents=True,exist_ok=True)
assert os.environ.get('CI')=='true'
root=new_fixture(out,'TreeGit remote query ');repo=root/'client';repo.mkdir();server_root=root/'server';server_root.mkdir();bare=server_root/'repo.git'
env={**os.environ,'GIT_CONFIG_GLOBAL':str(root/'gitconfig'),'GIT_CONFIG_NOSYSTEM':'1'}
def git(*args,cwd=repo):
 result=subprocess.run(['git','-C',str(cwd),*args],env=env,capture_output=True,text=True,encoding='utf-8',timeout=20)
 assert result.returncode==0,result.stderr
 return result.stdout.strip()

git('init','-b','main');git('config','user.name','TreeGit Demo');git('config','user.email','demo@example.invalid');git('config','protocol.version','0')
(repo/'note.txt').write_text('Anonymous query fixture.\n','utf-8');git('add','.');git('commit','-m','Anonymous query fixture')
git('clone','--bare',str(repo),str(bare),cwd=root);git('tag','tag-only',cwd=bare);git('branch','nested/refs/heads/missing',cwd=bare);git('config','http.receivepack','true',cwd=bare)
scenario={'mode':'head'};seen=[];backend_errors=[];stop=threading.Event();slow_query=threading.Event();process=None;gui_pid=None
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*_):pass
 def do_GET(self):
  self.handle_git()
 def do_POST(self):
  self.handle_git()
 def handle_git(self):
  mode=scenario['mode'];seen.append(mode)
  parsed=urllib.parse.urlsplit(self.path)
  if mode=='cancel' and stop.wait(5):return
  if mode.startswith('vm-') and 'git-upload-pack' in parsed.query:
   slow_query.set()
   if stop.wait(5):return
  if mode=='failure':
   self.send_error(503,'Anonymous unavailable remote');return
  size=int(self.headers.get('Content-Length','0'));assert size<=1024*1024
  body=self.rfile.read(size) if size else b''
  cgi={**env,'GIT_PROJECT_ROOT':str(server_root),'GIT_HTTP_EXPORT_ALL':'1','REQUEST_METHOD':self.command,'PATH_INFO':urllib.parse.unquote(parsed.path),'QUERY_STRING':parsed.query,'SERVER_PROTOCOL':'HTTP/1.1','SERVER_NAME':'127.0.0.1','SERVER_PORT':str(server.server_port),'REMOTE_ADDR':'127.0.0.1','CONTENT_TYPE':self.headers.get('Content-Type',''),'CONTENT_LENGTH':str(size)}
  if self.headers.get('Git-Protocol'):cgi['HTTP_GIT_PROTOCOL']=self.headers['Git-Protocol']
  result=subprocess.run(['git','http-backend'],input=body,env=cgi,cwd=server_root,capture_output=True,timeout=20)
  header,separator,body=result.stdout.partition(b'\r\n\r\n')
  if not separator:header,separator,body=result.stdout.partition(b'\n\n')
  if result.returncode or not separator:backend_errors.append(result.stderr.decode('utf-8','replace'));self.send_error(500);return
  try:
   self.send_response(200)
   for line in header.decode('ascii').splitlines():
    if ':' in line:
     name,value=line.split(':',1)
     if name.lower() not in ['status','content-length','connection','transfer-encoding']:self.send_header(name,value.strip())
   self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
  except (BrokenPipeError,ConnectionResetError):pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);server.daemon_threads=True
threading.Thread(target=server.serve_forever,daemon=True).start();git('remote','add','origin','http://127.0.0.1:'+str(server.server_port)+'/repo.git');git('update-ref','refs/remotes/origin/main','HEAD')
checks=[];failures=[];records=[]
def check(name,value):
 (checks if value else failures).append(name);print(('PASS ' if value else 'FAIL ')+name,flush=True)
before=[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'),git('show-ref',cwd=bare)]
try:
 for mode,branch in [('cancel','main'),('head','main'),('tag-only','tag-only'),('missing','missing'),('failure','main')]:
  scenario['mode']=mode;(repo/'remote-query.json').write_text(json.dumps({'branch':branch}),'utf-8')
  report=out/('remote-query-'+mode+'.json')
  result=subprocess.run([str(exe),'--integration-command-pipes',str(repo),'remote-query-'+mode,str(report)],env=env,capture_output=True,timeout=25)
  assert report.exists(),result.stderr.decode('utf-8','replace')
  record=json.loads(report.read_text('utf-8-sig'));records.append(record)
  check('remote query '+mode,record['success'] and result.returncode==0)
 (repo/'remote-query.json').unlink()
 if '--operations' in sys.argv:
  env['TREEGIT_TEST_MENU_PROBE']='1';messages=queue.Queue();sequence=0
  process=subprocess.Popen([str(exe),'--mcp'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
  threading.Thread(target=lambda:[messages.put(line) for line in process.stdout],daemon=True).start()
  def rpc(method,params):
   global sequence
   sequence+=1;process.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':params})+'\n');process.stdin.flush();response=json.loads(messages.get(timeout=30));assert response.get('id')==sequence and 'error' not in response,response
   return response['result']
  def tool(name,**args):
   response=rpc('tools/call',{'name':name,'arguments':args});assert not response.get('isError'),response
   return json.loads(response['content'][0]['text'])
  def probe(action,value=0):return tool('ui_layout',action='test_menu',text=action,value=value)
  rpc('initialize',{'protocolVersion':'2025-11-25'});gui_pid=tool('ui_state')['processId'];tool('ui_open_repository',repo=str(repo))
  for _ in range(150):
   state=next((item for item in tool('ui_state')['repositories'] if Path(item['repo'])==repo),None)
   if state and state['workingCopyStatusKnown'] and state['branch']=='main':break
   time.sleep(.05)
  else:raise AssertionError('The isolated remote branch did not load')
  for index in [0,1]:
   scenario['mode']='vm-'+str(index);slow_query.clear();probe('remote_query_start',index)
   assert slow_query.wait(15),'The real remote query did not start'
   probe('remote_query_cancel')
   for _ in range(150):
    state=probe('remote_query_state')
    if not state['inProgress']:break
    time.sleep(.05)
   check('cancelled '+('single' if index==0 else 'multiple')+' branch operation returns to its form',not state['inProgress'] and state['popupPresent'] and not state['panelVisible'])
   check('cancelled '+('single' if index==0 else 'multiple')+' branch operation completes every log',not state['busy'] and state['activeLogs']==0)
   records.append({'operation':scenario['mode'],**state});tool('ui_command',repo=str(repo),command='cancel_popup')
 check('queries preserve commits, index, working files and remote refs',before==[git('rev-parse','HEAD'),git('ls-files','--stage'),git('status','--porcelain'),git('show-ref',cwd=bare)] and not backend_errors)
finally:
 if process:
  process.stdin.close()
  try:process.wait(timeout=5)
  except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
 if gui_pid:close_owned_gui(exe,gui_pid)
 stop.set();server.shutdown();server.server_close();remove_fixture(root,out,'TreeGit remote query ')
 (out/'results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'failures':failures,'records':records,'fixtureRemoved':not root.exists()},indent=2),'utf-8')
assert not failures,failures
