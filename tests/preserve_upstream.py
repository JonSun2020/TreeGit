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
        # Formatting a path for display preserves the upstream data source and binding mode.
        actual = new.replace(', Converter={x:Static c:PathConverters.Local}', '') if label == 'data bindings' else new
        missing=Counter(re.findall(pattern,old))-Counter(re.findall(pattern,actual))
        if name == 'src/Views/WorkingCopy.axaml':
            # The user removed the file-name filter from this toolbar.
            removed_search = {
                'named controls': ('x:Name="LocalChangesSearchBox"',),
                'actions': ('Command="{Binding ClearFilter}"',),
                'data bindings': ('{Binding Filter, Mode=TwoWay}', '{Binding ClearFilter}',
                                  '{Binding Filter, Converter={x:Static StringConverters.IsNotNullOrEmpty}'),
            }
            for entry in removed_search[label]:
                missing.pop(entry, None)
        if name == 'src/Views/WorkingCopy.axaml' and label == 'actions':
            # Discard moved out of the local changes strip. Keep its main toolbar entry.
            toolbar = (root/'src/Views/RepositoryToolbar.axaml').read_text(encoding='utf-8-sig')
            handler = (root/'src/Views/RepositoryToolbar.axaml.cs').read_text(encoding='utf-8-sig')
            assert 'Click="OpenDiscard"' in toolbar and 'repo.WorkingCopy.DiscardAllChanges();' in handler
            missing.pop('Command="{Binding DiscardAllChanges}"', None)
        if name == 'src/Views/LauncherPage.axaml' and label == 'data bindings':
            operation = (root/'src/ViewModels/Popup.Operation.cs').read_text(encoding='utf-8-sig')
            assert 'Description="{Binding StatusDescription}"' in new and 'IsVisible="{Binding ShowStatus}"' in new
            assert 'UsesOperationPanel ? OperationStage : ProgressDescription' in operation
            assert 'ShowStatus => InProgress ||' in operation
            missing.pop('{Binding ProgressDescription}', None)
            missing.pop('{Binding InProgress}', None)
        if name == 'src/Views/WelcomeToolbar.axaml' and label in ['actions', 'data bindings']:
            # The user replaced the welcome-page terminal with Create. The
            # repository's direct terminal entry must remain functional.
            toolbar = (root/'src/Views/RepositoryToolbar.axaml').read_text(encoding='utf-8-sig')
            handler = (root/'src/Views/RepositoryToolbar.axaml.cs').read_text(encoding='utf-8-sig')
            assert 'Command="{Binding CreateRepository}"' in new
            assert 'Click="OpenTerminalDirect"' in toolbar
            assert 'Native.OS.OpenTerminal(repo.FullPath)' in handler
            missing.pop('Command="{Binding OpenTerminal}"', None)
            missing.pop('{Binding OpenTerminal}', None)
        if name == 'src/Views/RepositoryToolbar.axaml' and label == 'data bindings':
            # The counters avoid expanding the divergence into commit lists.
            for direction in ('Behind', 'Ahead'):
                assert f'{{Binding CurrentBranch?.{direction}Count, Mode=OneWay, Converter={{x:Static c:IntConverters.IsGreaterThanZero}}' in new
                missing.pop(f'{{Binding CurrentBranch?.{direction}, Mode=OneWay, Converter={{x:Static c:ListConverters.IsNotNullOrEmpty}}', None)
        if name == 'src/Views/Repository.axaml' and label == 'data bindings':
            # Search moved from the repository sidebar into the history area.
            moved = {'{Binding Histories.SearchCommitContext, Mode=OneWay}', '{Binding #ThisControl.IsCommitSearchPanelVisible, Mode=OneWay}'}
            history = (root/'src/Views/Histories.axaml').read_text(encoding='utf-8-sig')
            assert '<Border IsVisible="{Binding IsSearchingCommits}"><v:CommitSearchView DataContext="{Binding SearchCommitContext}"/></Border>' in history
            missing = Counter({key: count for key, count in missing.items() if key not in moved})
        if name in ['src/Views/WorkingCopy.axaml','src/Views/CommitChanges.axaml'] and label == 'data bindings':
            # Both panes now share a repository-specific setting instead of the
            # former global preference; the actual persisted width is GUI-tested.
            assert '{Binding Repository.ChangesPanelWidth, Mode=TwoWay}' in new
            former = '{Binding Source={x:Static vm:Preferences.Instance}'
            replaced = 2 if name == 'src/Views/WorkingCopy.axaml' else 1
            if missing[former] == replaced:
                del missing[former]
            if name == 'src/Views/WorkingCopy.axaml':
                # This strip no longer changes the sidebar or duplicates Discard.
                assert 'Path=Layout.IsSidebarCollapsedInLocalChanges' not in new
                missing.pop('{Binding DiscardAllChanges}', None)
                # The confirmed composer removes the options dropdown; these
                # commands remain available in the advanced operation menu.
                menu = (root/'src/Views/Launcher.axaml').read_text(encoding='utf-8-sig')
                handler = (root/'src/Views/RepositoryToolbar.axaml.cs').read_text(encoding='utf-8-sig')
                for binding, action, assignment in [
                    ('{Binding UseAmend, Mode=TwoWay}', 'amend', 'amendRepo.WorkingCopy.UseAmend = true;'),
                    ('{Binding ResetAuthor, Mode=TwoWay}', 'reset_author', 'amendRepo.WorkingCopy.ResetAuthor = action == "reset_author";')]:
                    assert f'x:Name="GitAction_{action}" Click="OnTreeGitExtraAction"' in menu
                    assert f'case "{action}":' in handler and assignment in handler
                    missing.pop(binding, None)
        assert not missing, (name,label,dict(missing))
        checks.append(f'{name}: all original {label} retained')
for p in (root/'src/Resources/Locales').glob('*.axaml'):
    old=subprocess.check_output(['git','show','upstream-c0f40a2:'+p.relative_to(root).as_posix()],cwd=root).decode('utf-8-sig')
    removed = {
        'Text.ScanRepositories.UseCustomDir',
        'Text.WorkingCopy.DiscardAll',
        'Text.WorkingCopy.FilterChanges',
        'Text.Preferences.General.ShowRelativeTimeInGraph',
        'Text.Preferences.General.ShowTagsInGraph',
        'Text.Preferences.General.ShowChangesTabInCommitDetailByDefault',
        *(f'Text.Period.{name}' for name in (
            'DaysAgo', 'HourAgo', 'HoursAgo', 'JustNow', 'LastMonth',
            'LastYear', 'MinutesAgo', 'MonthsAgo', 'YearsAgo', 'Yesterday',
        )),
    }
    current = set(re.findall(r'x:Key="([^"]+)"',p.read_text(encoding='utf-8-sig')))
    assert set(re.findall(r'x:Key="([^"]+)"',old)) - removed <= current
    assert not (current & removed) and 'Text.ScanRepositories.NoRoot' in current
checks.append('upstream localization resource keys retained outside the removed scan and display preferences')
print(json.dumps({'passed':len(checks),'checks':checks},ensure_ascii=False,indent=2))
