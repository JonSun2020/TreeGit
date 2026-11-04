using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SourceGit.Views
{
    internal sealed class RepositoryCredentials : ChromelessWindow
    {
        public RepositoryCredentials(ViewModels.Repository repo)
        {
            Title = "仓库认证 / Repository credentials";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            Topmost = true;
            ShowActivated = true;
            Opened += (_, _) => Activate();
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = "仓库认证", FontSize = 20 });
            panel.Children.Add(new TextBlock { Text = repo.FullPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "HTTPS 账号只用于当前仓库和所选远程。密码/令牌保存到 Windows 凭据管理器。\nSSH 密钥仍在原有远程配置中设置。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var remotes = new ComboBox { ItemsSource = repo.Remotes, DisplayMemberBinding = new Avalonia.Data.Binding("Name"), HorizontalAlignment = HorizontalAlignment.Stretch };
            var username = new TextBox { Height = 32, Watermark = "用户名 / Username" };
            var password = new TextBox { Height = 32, Watermark = "密码或访问令牌（可留空）", PasswordChar = '*' };
            var showPassword = new ToggleButton { Classes = { "toggle_untracked" }, Width = 26, Height = 14 };
            ToolTip.SetTip(showPassword, "显示 / 隐藏密码");
            password.InnerRightContent = showPassword;
            showPassword.IsCheckedChanged += (_, _) => password.RevealPassword = showPassword.IsChecked == true;
            var useManager = new CheckBox { Content = new TextBlock { Text = "使用原有系统认证 / OAuth（Git Credential Manager）", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath) };
            var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var save = new Button { Content = "保存", Classes = { "flat", "primary" } };
            var forget = new Button { Content = "清除已保存认证", Classes = { "flat" } };
            var close = new Button { Content = "关闭", Classes = { "flat" } };
            Uri SelectedUri()
            {
                if (remotes.SelectedItem is Models.Remote r && Uri.TryCreate(r.URL, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    return uri;
                throw new InvalidOperationException("请选择 HTTP(S) 远程；SSH 远程使用原有的 SSH 密钥配置和自动认证窗口。");
            }
            remotes.SelectionChanged += (_, _) =>
            {
                username.Text = string.Empty;
                password.Text = string.Empty;
                try
                {
                    var existing = Native.RepositoryCredentials.Get(repo.FullPath, SelectedUri());
                    username.Text = existing?.Username ?? string.Empty;
                    message.Text = existing.HasValue ? "已有认证。填写新密码可覆盖；清除后下次访问会重新弹窗。" : "尚未保存认证。也可以在下次访问远程时自动输入。";
                }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            save.Click += async (_, _) =>
            {
                try
                {
                    var manager = useManager.IsChecked == true;
                    if (!await new Commands.Config(repo.FullPath).SetAsync("sourcegit.credentialmanager", manager ? "true" : "false"))
                        throw new InvalidOperationException("无法保存此仓库的认证模式。");
                    Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, manager);
                    if (manager)
                    {
                        password.Text = string.Empty;
                        message.Text = "已切换到此仓库的系统认证 / OAuth 模式。下次联网操作会启动原有认证流程。";
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(username.Text))
                        throw new InvalidOperationException("请输入账号，密码可以留空。");
                    Native.RepositoryCredentials.Save(repo.FullPath, SelectedUri(), username.Text, password.Text ?? string.Empty);
                    password.Text = string.Empty;
                    message.Text = "已保存此仓库的认证。";
                }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            forget.Click += (_, _) =>
            {
                try
                {
                    Native.RepositoryCredentials.Forget(repo.FullPath, SelectedUri());
                    password.Text = string.Empty;
                    message.Text = "已清除。下次访问远程时会自动弹出认证窗口。";
                }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            close.Click += (_, _) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(forget);
            buttons.Children.Add(save);
            buttons.Children.Add(close);
            panel.Children.Add(remotes);
            panel.Children.Add(useManager);
            panel.Children.Add(username);
            panel.Children.Add(password);
            panel.Children.Add(message);
            panel.Children.Add(buttons);
            Content = panel;
            if (repo.Remotes.Count > 0)
                remotes.SelectedIndex = 0;
            Closed += (_, _) => password.Text = string.Empty;
        }
    }
}
