from pathlib import Path
root = Path(__file__).resolve().parents[1] / 'src'
p = root/'Views/RepositoryToolbar.axaml'
s = p.read_text(encoding='utf-8')
labels = [('Repository.OpenWithExternalTools','Tools','外部工具'),('Repository.ViewLogs','Log','命令日志'),
          ('Repository.Statistics','Statistics','统计'),('Fetch','Fetch','获取'),('Pull','Pull','拉取'),
          ('Push','Push','推送'),('Stash','Stash','贮藏'),('Apply.Title','Patch','应用补丁'),
          ('Repository.NewBranch','Branch','分支'),('GitFlow','Git Flow','Git Flow'),('GitLFS','Git LFS','Git LFS'),
          ('Bisect','Bisect','二分定位'),('Repository.CustomActions','Actions','自定义'),('Repository.Clean','Clean','清理')]
for old, en, zh in labels:
    s = s.replace(f'Classes="toolbar_label" Text="{{DynamicResource Text.{old}}}"', f'Classes="toolbar_label" Text="{{DynamicResource Text.Toolbar.{en.replace(" ", "")}}}"')
s = s.replace('<Setter Property="FontSize" Value="11"/>', '<Setter Property="FontSize" Value="12"/><Setter Property="MaxWidth" Value="60"/><Setter Property="TextTrimming" Value="CharacterEllipsis"/>')
start = s.index('    <StackPanel Grid.Column="0"')
end = s.index('    <ScrollViewer Grid.Column="1"', start)
external = s[start:end].replace('Grid.Column="0"', 'Grid.Column="1"', 1)
s = s[:start] + s[end:]
s = s.replace('ColumnDefinitions="Auto,*,0,Auto"', 'ColumnDefinitions="*,Auto,0,Auto"', 1)
s = s.replace('<ScrollViewer Grid.Column="1"', '<ScrollViewer Grid.Column="0"', 1)
s = s.replace('    <StackPanel Grid.Column="2"', external + '    <StackPanel Grid.Column="2"', 1)
needle = 'VerticalScrollBarVisibility="Disabled"><StackPanel Orientation="Horizontal">'
commit = '''
      <Button Classes="icon_button" Width="64" Click="ShowWorkingCopy" IsVisible="{Binding !IsBare}" ToolTip.Tip="{DynamicResource Text.WorkingCopy}">
        <StackPanel Orientation="Vertical"><Path Width="22" Height="22" Data="{StaticResource Icons.Commit}" Fill="{DynamicResource Brush.Accent}"/><TextBlock Classes="toolbar_label" Text="{DynamicResource Text.Toolbar.Commit}"/></StackPanel>
      </Button>'''
s = s.replace(needle, needle + commit, 1)
p.write_text(s, encoding='utf-8')
for p in (root/'Resources/Locales').glob('*.axaml'):
    s = p.read_text(encoding='utf-8-sig')
    chinese = p.stem in ('zh_CN','zh_TW')
    content = ''.join(f'  <x:String x:Key="Text.Toolbar.{en.replace(" ", "")}">{zh if chinese else en}</x:String>\n' for _, en, zh in labels)
    content += f'  <x:String x:Key="Text.Toolbar.Commit">{"提交" if chinese else "Commit"}</x:String>\n'
    s = s.replace('</ResourceDictionary>', content + '</ResourceDictionary>')
    p.write_text(s, encoding='utf-8')
