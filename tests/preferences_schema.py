"""Load real preferences from isolated CLI fixtures without showing failure dialogs."""
import json, os, subprocess, sys
from pathlib import Path
from qa_profile import IsolatedProfile

source=Path(sys.argv[1]).resolve(); out=Path(sys.argv[2]).resolve(); out.mkdir(parents=True,exist_ok=True)
profile=IsolatedProfile(source,out);exe=source
cases=[('normal',{},False)]
cases += [(key+'-null',{key:None},True) for key in ['Layout','Workspaces','RepositoryNodes','CustomActions','OpenAIServices']]
cases += [('workspace-null',{'Workspaces':[None]},True),('workspace-repositories-null',{'Workspaces':[{'Repositories':None}]},True),('workspace-path-null',{'Workspaces':[{'Repositories':[None]}]},True),('repository-null',{'RepositoryNodes':[None]},True),('subnodes-null',{'RepositoryNodes':[{'Id':'folder','SubNodes':None}]},True),('nested-repository-null',{'RepositoryNodes':[{'Id':'folder','SubNodes':[None]}]},True)]
records=[]
correct_p4='-tw 4 "$BASE" "$REMOTE" "$LOCAL" "$MERGED"'
former_p4='-tw 4 "$BASE" "$LOCAL" "$REMOTE" "$MERGED"'
custom_p4='"$BASE" "$LOCAL" "$REMOTE" "$MERGED" --custom-marker'
try:
    for name,settings,recovery in cases:
        data=profile.reset()/'data'
        settings={'Locale':'zh_CN','Check4UpdatesOnStartup':False,**settings}; original=json.dumps(settings,ensure_ascii=False)
        (data/'preference.json').write_text(original,'utf-8'); before=set(data.glob('preference-recovery-*.json'))
        result=subprocess.run([str(exe),'--integration-preferences-load',str(out/name)],capture_output=True,text=True,encoding='utf-8',timeout=25,creationflags=subprocess.CREATE_NO_WINDOW)
        record=json.loads((out/name/'preferences-load.json').read_text('utf-8')); record['case']=name; record['exitCode']=result.returncode
        backups=set(data.glob('preference-recovery-*.json'))-before
        record['backupMatchesOriginal']=len(backups)==1 and next(iter(backups)).read_text('utf-8')==original if recovery else not backups
        record['passed']=result.returncode==0 and record.get('opened') and record.get('recovery')==(1 if recovery else 0) and record['backupMatchesOriginal']
        if name=='normal':
            p4=next(i for i,v in enumerate(record['externalPresets']) if v['icon']=='p4merge')
            record['passed']=record['passed'] and record['externalPresets'][p4]['merge']==correct_p4
            cases += [('p4-empty',{'ExternalMergeToolType':p4,'ExternalMergeToolMergeArgs':''},False),
                      ('p4-former-default',{'ExternalMergeToolType':p4,'ExternalMergeToolMergeArgs':former_p4},False),
                      ('p4-custom',{'ExternalMergeToolType':p4,'ExternalMergeToolMergeArgs':custom_p4},False),
                      ('other-custom',{'ExternalMergeToolType':1,'ExternalMergeToolMergeArgs':former_p4},False)]
        if name.startswith('p4-'):
            record['passed']=record['passed'] and record['externalMergeArgs']==(custom_p4 if name=='p4-custom' else correct_p4)
        elif name=='other-custom':record['passed']=record['passed'] and record['externalMergeArgs']==former_p4
        records.append(record); print(('PASS ' if record['passed'] else 'FAIL ')+name,flush=True)
finally:
    (out/'preferences-schema-results.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),'utf-8')
    profile.close()
assert all(record['passed'] for record in records),[record['case'] for record in records if not record['passed']]
