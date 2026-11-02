"""Apply the presentation-only SourceTree layout to the pinned upstream snapshot."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1] / 'src'
if 'toolbar_label' in (root/'Views/RepositoryToolbar.axaml').read_text(encoding='utf-8-sig'):
    raise SystemExit('The one-time upstream presentation migration has already been applied.')
def edit(name, fn):
    p = root / name
    p.write_text(fn(p.read_text(encoding='utf-8-sig')), encoding='utf-8')

def toolbar(s):
    s = s.replace('<Grid ColumnDefinitions="*,Auto,0,*">', '''<Grid ColumnDefinitions="Auto,*,0,Auto" Margin="8,0">
    <Grid.Styles>
      <Style Selector="Button.icon_button">
        <Setter Property="Height" Value="62"/>
        <Setter Property="CornerRadius" Value="3"/>
        <Setter Property="Padding" Value="6,4"/>
      </Style>
      <Style Selector="Button.icon_button Path">
        <Setter Property="Fill" Value="{DynamicResource Brush.FG2}"/>
      </Style>
      <Style Selector="TextBlock.toolbar_label">
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="HorizontalAlignment" Value="Center"/>
        <Setter Property="Margin" Value="0,5,0,0"/>
      </Style>
    </Grid.Styles>''')
    # Keep every original event, binding, tooltip and hotkey. Only wrap the content.
    labels = ['Text.Repository.OpenWithExternalTools', 'Text.Repository.ViewLogs',
              'Text.Repository.Statistics', 'Text.Fetch', 'Text.Pull', 'Text.Push',
              'Text.Stash', 'Text.Apply.Title', 'Text.Repository.NewBranch',
              'Text.GitFlow', 'Text.GitLFS', 'Text.Bisect',
              'Text.Repository.CustomActions', 'Text.Repository.Clean']
    i = 0
    def button(match):
        nonlocal i
        b = match.group(0)
        if i >= len(labels):
            return b
        label = labels[i]
        i += 1
        b = b.replace('Width="32"', 'Width="64"', 1)
        b = re.sub(r' Margin="(?:16|18|12),0,0,0"', '', b, count=1)
        # Tooltip is a property element; visible content starts after it.
        start = b.find('</ToolTip.Tip>')
        start = start + len('</ToolTip.Tip>') if start >= 0 else b.find('>') + 1
        body = b[start:b.rfind('</Button>')].strip()
        body = re.sub(r'Width="(?:13|14)" Height="(?:13|14)"', 'Width="22" Height="22"', body)
        return b[:start] + '\n        <StackPanel Orientation="Vertical" VerticalAlignment="Center">\n' + body + f'\n          <TextBlock Classes="toolbar_label" Text="{{DynamicResource {label}}}"/>\n        </StackPanel>\n      </Button>'
    s = re.sub(r'<Button Classes="icon_button"[\s\S]*?</Button>', button, s)
    s = s.replace('Margin="16,0,0,0"\n                 VerticalAlignment', 'Margin="6,0"\n                 VerticalAlignment')
    # Main actions stay reachable at small window widths.
    s = s.replace('<StackPanel Grid.Column="1" Orientation="Horizontal">', '<ScrollViewer Grid.Column="1" HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled"><StackPanel Orientation="Horizontal">', 1)
    s = s.replace('    <StackPanel Grid.Column="2"', '    </ScrollViewer>\n    <StackPanel Grid.Column="2"', 1)
    # Close the horizontal action stack before the scroll viewer (the existing close is retained).
    s = s.replace('<Grid Grid.Column="3" HorizontalAlignment="Right"', '<Grid Grid.Column="3" MaxWidth="180" HorizontalAlignment="Right"')
    return s

def repository(s):
    a = s.index('    <!-- Page Switcher -->')
    b = s.index('    <!-- Body -->', a)
    nav = s[a:b]
    nav = nav.replace('<Grid Grid.Column="0" Width="49" RowDefinitions="Auto,*,Auto,12">', '<Grid Grid.Row="0" RowDefinitions="Auto" ColumnDefinitions="*,32" Margin="0,6,0,8">')
    nav = nav.replace('Value="42"', 'Value="32"').replace('Value="4"', 'Value="0"')
    nav = nav.replace('Value="Center"', 'Value="Left"', 1)
    nav = nav.replace('<Setter Property="VerticalAlignment" Value="Center"/>', '<Setter Property="VerticalAlignment" Value="Center"/><Setter Property="Margin" Value="12,0,0,0"/>', 1)
    nav = nav.replace('{DynamicResource Brush.AccentHovered}', '#220052CC')
    nav = nav.replace('<Path Classes="icon" Data="{StaticResource Icons.Dashboard}"/>', '<Grid><Path Classes="icon" Data="{StaticResource Icons.Dashboard}"/><TextBlock Margin="40,0,24,0" Text="{DynamicResource Text.Dashboard}"/></Grid>')
    for icon, label in [('Changes','WorkingCopy'),('Stashes','Stashes')]:
        nav = nav.replace(f'<Path Classes="icon" Data="{{StaticResource Icons.{icon}}}"/>', f'<Path Classes="icon" Data="{{StaticResource Icons.{icon}}}"/><TextBlock Margin="40,0,24,0" Text="{{DynamicResource Text.{label}}}"/>')
    nav = nav.replace('<Button Grid.Row="2"', '<Button Grid.Column="1" VerticalAlignment="Top"')
    nav = nav.replace('Width="40" Height="40"', 'Width="28" Height="28"').replace('Margin="4,0"', 'Margin="0"')
    s = s[:a] + s[b:]
    s = s.replace('<ColumnDefinition Width="49"/>\n      <ColumnDefinition Width="*"/>', '<ColumnDefinition Width="*"/>', 1)
    s = s.replace('<Border Grid.Column="1"\n            Margin="0,0,4,4"', '<Border Grid.Column="0"\n            Margin="0"', 1)
    s = s.replace('CornerRadius="8"', 'CornerRadius="0"')
    marker = '            <!-- Sidebar (Branches/Remotes/Tags/Submodules/Worktrees) -->'
    s = s.replace(marker, '            <Grid Grid.Column="0" RowDefinitions="Auto,*">\n' + nav + marker, 1)
    s = s.replace('<Grid Grid.Column="0" RowDefinitions="Auto,*" IsVisible=', '<Grid Grid.Row="1" RowDefinitions="Auto,*" IsVisible=', 1)
    s = s.replace('<v:CommitSearchView Grid.Column="0"', '<v:CommitSearchView Grid.Row="1"', 1)
    s = s.replace('            <!-- Splitter -->', '            </Grid>\n\n            <!-- Splitter -->', 1)
    s = s.replace('CornerRadius="13"', 'CornerRadius="3"')
    return s

edit('Views/RepositoryToolbar.axaml', toolbar)
edit('Views/Repository.axaml', repository)
edit('Views/LauncherPage.axaml', lambda s: s.replace('RowDefinitions="36,*"', 'RowDefinitions="76,*"').replace('CornerRadius="8"','CornerRadius="0"'))
palette = {'Window':'#FFF4F5F7', 'WindowBorder':'#FFB3BAC5', 'TitleBar':'#FFEBECF0',
           'ToolBar':'#FFFAFBFC', 'PopupBorder':'#FFDFE1E6', 'Border0':'#FFDFE1E6',
           'Border1':'#FFB3BAC5', 'Border2':'#FFDFE1E6', 'FG1':'#FF172B4D',
           'FG2':'#FF5E6C84', 'Link':'#FF0052CC', 'SubPanel':'#FFFAFBFC',
           'Badge':'#FFDEEBFF', 'BadgeFG':'#FF0747A6'}
def themes(s):
    light, rest = s.split('<ResourceDictionary x:Key="Dark">', 1)
    for k,v in palette.items():
        light = re.sub(r'(<Color x:Key="Color\.' + re.escape(k) + r'">)[^<]+', lambda m: m[1]+v, light)
    return light + '<ResourceDictionary x:Key="Dark">' + rest
edit('Resources/Themes.axaml', themes)
edit('ViewModels/Preferences.cs', lambda s: s.replace('_locale = "en_US"','_locale = "zh_CN"').replace('_theme = "Default"','_theme = "Light"'))
edit('ViewModels/LayoutInfo.cs', lambda s: s.replace('= 1280;', '= 1440;').replace('= 720;', '= 900;').replace('= 250;', '= 240;'))
