using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SourceGit.Views
{
    internal sealed class McpConnection : ChromelessWindow
    {
        public McpConnection()
        {
            Title = "MCP · AI 连接";
            Width = 650;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
            panel.Children.Add(new TextBlock { Text = "MCP · AI 连接", FontSize = 22 });
            panel.Children.Add(new TextBlock
            {
                Text = "将下面的配置添加到支持 stdio MCP 的 AI 客户端。连接后，AI 可以打开仓库、查看历史和差异、切换界面；默认支持读写，可执行全部 Git 操作，包括暂存、提交、推送、重置和分支管理。操作会同步到本软件的界面和命令日志。",
                TextWrapping = TextWrapping.Wrap,
            });
            var write = new CheckBox { Content = "只读连接（禁止 Git 写入）" };
            var configuration = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 235, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            void Update() => configuration.Text = JsonSerializer.Serialize(new
            {
                mcpServers = new
                {
                    treegit = new { command = Environment.ProcessPath, args = write.IsChecked == true ? new[] { "--mcp", "--read-only" } : ["--mcp"] },
                },
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            write.IsCheckedChanged += (_, _) => Update();
            Update();
            var state = new TextBlock { Text = "本机服务已就绪 · stdio · 当前 Windows 用户 · 无需登录", TextWrapping = TextWrapping.Wrap };
            var copy = new Button { Content = "复制连接配置", Classes = { "flat", "primary" } };
            copy.Click += async (_, _) =>
            {
                if (Clipboard != null)
                {
                    await Clipboard.SetTextAsync(configuration.Text);
                    state.Text = "已复制连接配置。便携目录移动后，请重新复制配置。";
                }
            };
            var close = new Button { Content = "关闭", Classes = { "flat" } };
            close.Click += (_, _) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            panel.Children.Add(write);
            panel.Children.Add(configuration);
            panel.Children.Add(state);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}
