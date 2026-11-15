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
            App.BindText(this, TitleProperty, "TreeGit.Menu.Mcp");
            Width = 650;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
            panel.Children.Add(new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Menu.Mcp"), FontSize = 22 });
            panel.Children.Add(new TextBlock
            {
                [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Mcp.Description"),
                TextWrapping = TextWrapping.Wrap,
            });
            var write = new CheckBox { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Mcp.ReadOnly") };
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
            var state = new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Mcp.Ready"), TextWrapping = TextWrapping.Wrap };
            var copy = new Button { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Mcp.CopyConfig"), Classes = { "flat", "primary" } };
            copy.Click += async (_, _) =>
            {
                if (Clipboard != null)
                {
                    await Clipboard.SetTextAsync(configuration.Text);
                    App.BindText(state, TextBlock.TextProperty, "TreeGit.Mcp.Copied");
                }
            };
            var close = new Button { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Close"), Classes = { "flat" } };
            close.Click += (_, _) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            panel.Children.Add(write);
            panel.Children.Add(configuration);
            panel.Children.Add(state);
            RepositoryDialogLayout.Apply(this, panel, buttons, 650);
        }
    }
}
