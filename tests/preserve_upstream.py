"""Check that the presentation changes retain original named controls, actions and bindings."""
from collections import Counter
from pathlib import Path
import json
import re
import subprocess
root=Path(__file__).resolve().parents[1]
files=['src/Views/Repository.axaml','src/Views/RepositoryToolbar.axaml','src/Views/LauncherPage.axaml','src/Views/Launcher.axaml','src/Views/WelcomeToolbar.axaml','src/Views/Clone.axaml','src/Views/WorkingCopy.axaml','src/Views/CommitChanges.axaml','src/Views/LauncherTabBar.axaml']
checks=[]
for name in files:
    old=subprocess.check_output(['git','show','upstream-c0f40a2:'+name],cwd=root).decode('utf-8-sig')
    new=(root/name).read_text(encoding='utf-8-sig')
    for label,pattern in [('named controls',r'x:Name="[^"]+"'),('actions',r'(?:Click|Tapped|DoubleTapped|Command|HotKey|ContextRequested|SelectionChanged|PointerPressed|SizeChanged|RowsChanged|SearchRequested)="[^"]+"'),('data bindings',r'\{Binding [^}]+\}')]:
        missing=Counter(re.findall(pattern,old))-Counter(re.findall(pattern,new))
        assert not missing, (name,label,dict(missing))
        checks.append(f'{name}: all original {label} retained')
for p in (root/'src/Resources/Locales').glob('*.axaml'):
    old=subprocess.check_output(['git','show','upstream-c0f40a2:'+p.relative_to(root).as_posix()],cwd=root).decode('utf-8-sig')
    assert set(re.findall(r'x:Key="([^"]+)"',old)) <= set(re.findall(r'x:Key="([^"]+)"',p.read_text(encoding='utf-8-sig')))
checks.append('all upstream localization resource keys retained')
print(json.dumps({'passed':len(checks),'checks':checks},ensure_ascii=False,indent=2))
