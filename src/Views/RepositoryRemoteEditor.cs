using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    internal sealed class RepositoryRemoteEditor : ChromelessWindow
    {
        public RepositoryRemoteEditor(ViewModels.Repository repo, Models.Remote remote)
        {
            Title = remote == null ? "添加远程仓库" : "编辑远程仓库";
            Width = 550; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; CanResize = false; CloseOnESC = true;
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
            panel.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.Bold });
            var name = new TextBox { Text = remote?.Name ?? "origin", Height = 28 };
            var url = new TextBox { Text = remote?.URL ?? "", Height = 28, Watermark = "HTTPS / SSH 地址或本地仓库路径" };
            var key = new TextBox { Text = remote?.PrivateSSHKey ?? "", Height = 28, Watermark = "留空使用 OpenSSH 默认密钥或代理" };
            panel.Children.Add(new TextBlock { Text = "名称" }); panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = "远程仓库路径" }); panel.Children.Add(url);
            var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            keyRow.Children.Add(key);
            var browse = RepositorySettings.Action("浏览…", async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 SSH 私钥", AllowMultiple = false });
                if (files.Count > 0) key.Text = files[0].Path.LocalPath;
            }); Grid.SetColumn(browse, 1); keyRow.Children.Add(browse);
            var advanced = new StackPanel { Spacing = 6 };
            advanced.Children.Add(new TextBlock { Text = "SSH 私钥（可选）" }); advanced.Children.Add(keyRow);
            panel.Children.Add(new Expander { Header = "认证选项", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = !string.IsNullOrEmpty(remote?.PrivateSSHKey) });
            var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false }; panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(RepositorySettings.Action("确定", async () =>
            {
                var remoteName = name.Text?.Trim() ?? "";
                var remoteUrl = url.Text?.Trim() ?? "";
                var sshKey = key.Text?.Trim() ?? "";
                string message = null;
                if (!Regex.IsMatch(remoteName, @"^[\w.\-]+$")) message = "请输入有效的远程名称（字母、数字、下划线、点或连字符）。";
                else if (repo.Remotes.Any(r => r.Name == remoteName && r.Name != remote?.Name)) message = "同名远程仓库已存在。";
                else if (string.IsNullOrWhiteSpace(remoteUrl) || (!Models.Remote.IsValidURL(remoteUrl) && !Directory.Exists(remoteUrl))) message = "请输入有效的远程地址或本地仓库路径。";
                else if (!string.IsNullOrEmpty(sshKey) && !File.Exists(sshKey)) message = "找不到指定的 SSH 私钥文件。";
                if (message != null) { error.Text = message; error.IsVisible = true; return; }
                var command = new Commands.Remote(repo.FullPath);
                bool success;
                if (remote == null) success = await command.AddAsync(remoteName.Quoted(), remoteUrl.Quoted());
                else
                {
                    success = remote.Name == remoteName || await command.RenameAsync(remote.Name.Quoted(), remoteName.Quoted());
                    if (success) success = await command.SetURLAsync(remoteName.Quoted(), remoteUrl.Quoted(), false, false);
                }
                if (success && !string.IsNullOrEmpty(sshKey))
                    success = await new Commands.Config(repo.FullPath).SetAsync($"remote.{remoteName}.sshkey", sshKey);
                else if (success && !string.IsNullOrEmpty(remote?.PrivateSSHKey))
                    success = await new Commands.Config(repo.FullPath).SetAsync($"remote.{remoteName}.sshkey", null);
                if (success) Close(true);
                else { error.Text = "保存失败，请查看仓库命令日志。"; error.IsVisible = true; }
            }));
            buttons.Children.Add(RepositorySettings.Action("取消", () => { Close(false); return System.Threading.Tasks.Task.CompletedTask; })); panel.Children.Add(buttons); Content = panel;
        }
    }
}
