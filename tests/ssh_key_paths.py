"""The real OpenSSH process must parse repository key paths containing spaces/apostrophes."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile

exe=Path(sys.argv[1]).resolve()
out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix="TreeGit SSH O'Brien "));repo=root/'repo';repo.mkdir()
env=os.environ.copy();env.pop('GIT_SSH_COMMAND',None)
env.update(GIT_CONFIG_GLOBAL=str(root/'global.gitconfig'),GIT_CONFIG_NOSYSTEM='1',GIT_TERMINAL_PROMPT='0')
subprocess.run(['git','init',str(repo)],capture_output=True,check=True,env=env)
with socket.socket() as listener:
    listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
url=f'ssh://git@127.0.0.1:{port}/owned-audit.git'
subprocess.run(['git','-C',str(repo),'remote','add','origin',url],capture_output=True,check=True,env=env)
checks=[]
for i,name in enumerate(['identity with spaces',"identity O'Brien 中文"]):
    key=root/name;key.write_text('test fixture; connection is refused before authentication\n',encoding='utf-8')
    report=out/f'ssh-key-path-{i}.log'
    result=subprocess.run([str(exe),'--integration-auth-fetch',str(repo),url,str(key),str(report)],env=env,capture_output=True,timeout=30)
    text=report.read_text(encoding='utf-8')
    assert result.returncode==1,result.stderr
    assert 'connection refused' in text.lower(),text
    assert 'not accessible' not in text.lower() and 'unterminated' not in text.lower() and 'unexpected eof' not in text.lower(),text
    checks.append('native fetch reaches OpenSSH with exact key path '+repr(name))
    print('PASS',checks[-1],flush=True)
(out/'ssh-key-path-results.json').write_text(json.dumps({'passed':len(checks),'checks':checks,'root':str(root)},ensure_ascii=False,indent=2),encoding='utf-8')
