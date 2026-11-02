"""One-time presentation migration; normal builds do not run these migration scripts."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]/'src'
p=root/'Views/WelcomeToolbar.axaml'
s=p.read_text(encoding='utf-8')
if 'Text.Toolbar.Clone' not in s:
    labels=['Clone','Open','Terminal','Groups','Scan']
    i=0
    def wrap(m):
        global i
        b=m[0]
        b=b.replace('Width="32"','Width="72" Height="62"',1)
        start=b.find('</ToolTip.Tip>')
        start=start+len('</ToolTip.Tip>') if start>=0 else b.find('>')+1
        content=b[start:b.rfind('</Button>')].strip()
        content=re.sub(r'Width="(?:13|14|16)" Height="(?:13|14|16)"','Width="22" Height="22"',content)
        label=labels[i];i+=1
        return b[:start]+'<StackPanel Orientation="Vertical">'+content+f'<TextBlock Margin="0,6,0,0" HorizontalAlignment="Center" FontSize="12" Text="{{DynamicResource Text.Toolbar.{label}}}"/></StackPanel></Button>'
    s=re.sub(r'<Button Classes="icon_button"[\s\S]*?</Button>',wrap,s)
    p.write_text(s,encoding='utf-8')
p=root/'Views/Repository.axaml'
s=p.read_text(encoding='utf-8')
s=s.replace('<TextBlock Margin="40,0,24,0" Text="{DynamicResource Text.Dashboard}"/>','<TextBlock Margin="40,0,24,0" Text="{DynamicResource Text.Toolbar.History}"/>')
s=s.replace('<TextBlock Margin="40,0,24,0" Text="{DynamicResource Text.WorkingCopy}"/>','<TextBlock Margin="40,0,24,0" Text="{DynamicResource Text.Toolbar.Files}"/>')
p.write_text(s,encoding='utf-8')
values=[('Clone','Clone','克隆'),('Open','Open','打开'),('Terminal','Terminal','终端'),('Groups','New group','新建分组'),('Scan','Scan','扫描仓库'),('History','History','历史'),('Files','File status','文件状态')]
for p in (root/'Resources/Locales').glob('*.axaml'):
    s=p.read_text(encoding='utf-8-sig')
    text=''.join(f'  <x:String x:Key="Text.Toolbar.{key}">{zh if p.stem in ("zh_CN","zh_TW") else en}</x:String>\n' for key,en,zh in values if f'Text.Toolbar.{key}"' not in s)
    if 'Text.Clone.CredentialManager"' not in s:
        text += '  <x:String x:Key="Text.Clone.CredentialManager">'+('使用系统认证 / OAuth' if p.stem in ('zh_CN','zh_TW') else 'Use system authentication / OAuth')+'</x:String>\n'
    p.write_text(s.replace('</ResourceDictionary>',text+'</ResourceDictionary>'),encoding='utf-8')
