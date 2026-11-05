"""Load real preferences from isolated CLI fixtures without showing failure dialogs."""
import json, os, shutil, subprocess, sys
from pathlib import Path

source=Path(sys.argv[1]).resolve(); out=Path(sys.argv[2]).resolve(); out.mkdir(parents=True,exist_ok=True)
app=out/'isolated-app'; app.mkdir(); data=app/'data'; data.mkdir()
for file in source.parent.iterdir():
    if file.is_file():shutil.copy2(file,app/file.name)
exe=app/source.name
cases=[('normal',{},False)]
cases += [(key+'-null',{key:None},True) for key in ['Layout','Workspaces','RepositoryNodes','CustomActions','OpenAIServices']]
cases += [('workspace-null',{'Workspaces':[None]},True),('workspace-repositories-null',{'Workspaces':[{'Repositories':None}]},True),('workspace-path-null',{'Workspaces':[{'Repositories':[None]}]},True),('repository-null',{'RepositoryNodes':[None]},True),('subnodes-null',{'RepositoryNodes':[{'Id':'folder','SubNodes':None}]},True),('nested-repository-null',{'RepositoryNodes':[{'Id':'folder','SubNodes':[None]}]},True)]
records=[]
for name,settings,recovery in cases:
    settings={'Locale':'zh_CN','Check4UpdatesOnStartup':False,**settings}; original=json.dumps(settings,ensure_ascii=False)
    (data/'preference.json').write_text(original,'utf-8'); before=set(data.glob('preference-recovery-*.json'))
    result=subprocess.run([str(exe),'--integration-preferences-load',str(out/name)],capture_output=True,text=True,encoding='utf-8',timeout=25,creationflags=subprocess.CREATE_NO_WINDOW)
    record=json.loads((out/name/'preferences-load.json').read_text('utf-8')); record['case']=name; record['exitCode']=result.returncode
    backups=set(data.glob('preference-recovery-*.json'))-before
    record['backupMatchesOriginal']=len(backups)==1 and next(iter(backups)).read_text('utf-8')==original if recovery else not backups
    record['passed']=result.returncode==0 and record.get('opened') and record.get('recovery')==(1 if recovery else 0) and record['backupMatchesOriginal']
    records.append(record); print(('PASS ' if record['passed'] else 'FAIL ')+name,flush=True)
(out/'preferences-schema-results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),'utf-8')
assert all(record['passed'] for record in records),[record['case'] for record in records if not record['passed']]
