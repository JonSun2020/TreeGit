import json
from pathlib import Path
import subprocess
import sys

exe=Path(sys.argv[1]).resolve()
p=subprocess.Popen([str(exe),'--mcp'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
checks=[]
def exchange(value):
    p.stdin.write((value if isinstance(value,str) else json.dumps(value))+'\n')
    p.stdin.flush()
    return json.loads(p.stdout.readline())
def request(method,params={},id=1):return {'jsonrpc':'2.0','id':id,'method':method,'params':params}
def check(name,value):
    assert value,name
    checks.append(name)
    print('PASS',name,flush=True)
check('parse error',exchange('{')['error']['code']==-32700)
check('invalid request id',exchange(request('ping',id=[]))['error']['code']==-32600)
check('ping before initialization',exchange(request('ping'))['result']=={})
check('initialization required',exchange(request('tools/list'))['error']['code']==-32601)
for version in ['2024-11-05','2025-03-26','2025-06-18','2025-11-25']:
    check('negotiate '+version,exchange(request('initialize',{'protocolVersion':version}))['result']['protocolVersion']==version)
check('unsupported version selects server version',exchange(request('initialize',{'protocolVersion':'1900-01-01'}))['result']['protocolVersion']=='2025-11-25')
check('unknown method',exchange(request('missing'))['error']['code']==-32601)
check('unknown tool',exchange(request('tools/call',{'name':'missing'}))['error']['code']==-32602)
check('write tool unavailable',exchange(request('tools/call',{'name':'git_action','arguments':{'repo':'C:/','action':'push'}}))['error']['code']==-32602)
check('missing argument',exchange(request('tools/call',{'name':'git_status','arguments':{}}))['error']['code']==-32602)
check('wrong argument type',exchange(request('tools/call',{'name':'git_log','arguments':{'repo':'C:/','count':'many'}}))['error']['code']==-32602)
check('unknown argument',exchange(request('tools/call',{'name':'ui_state','arguments':{'execute':'calc'}}))['error']['code']==-32602)
check('invalid enum',exchange(request('tools/call',{'name':'ui_select_view','arguments':{'repo':'C:/','view':'unknown'}}))['error']['code']==-32602)
p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n')
p.stdin.flush()
check('notification emits no response',exchange(request('ping',id='after-notification'))['id']=='after-notification')
p.stdin.close()
p.wait(timeout=10)
check('clean EOF and no non-protocol output',p.returncode==0 and p.stdout.read()=='' and p.stderr.read()=='')
out=Path(sys.argv[2]);out.parent.mkdir(parents=True,exist_ok=True)
out.write_text(json.dumps({'passed':len(checks),'checks':checks},indent=2),encoding='utf-8')
