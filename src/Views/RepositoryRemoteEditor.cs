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
            App.BindText(this, TitleProperty, remote == null ? "TreeGit.Remote.Add" : "TreeGit.Remote.Edit");
            Width = 550; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; CanResize = false; CloseOnESC = true;
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
            panel.Children.Add(new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding("Title") { Source = this }, FontSize = 18, FontWeight = FontWeight.Bold });
            var name = new TextBox { Name = "RemoteEditorName", Text = remote?.Name ?? "origin", Height = 28 };
            var url = new RemoteAddressTextBox { Name = "RemoteEditorPath", Text = remote?.URL ?? "", Height = 28, [!TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Remote.UrlHint") };
            var key = new PathTextBox { Name = "RemoteEditorSshKey", Text = remote?.PrivateSSHKey ?? "", Height = 28,
                CornerRadius = new CornerRadius(3), [!TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Remote.KeyHint") };
            var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            keyRow.Children.Add(key);
            var browse = new Button
            {
                Name = "RemoteEditorBrowseSshKey", Classes = { "flat" }, Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 0), Height = 28, VerticalAlignment = VerticalAlignment.Center,
                [!ContentControl.ContentProperty] = App.TextBinding("TreeGit.BrowseEllipsis"),
            };
            browse.Click += async (_, _) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = App.Text("TreeGit.Remote.SelectKey"), AllowMultiple = false });
                if (files.Count > 0) key.Text = files[0].Path.LocalPath;
            };
            Grid.SetColumn(browse, 1);
            keyRow.Children.Add(browse);
            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 16, RowSpacing = 8 };
            void AddField(int row, string caption, Control editor)
            {
                var label = RepositoryDialogLayout.FieldLabel(caption);
                Grid.SetRow(label, row);
                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                fields.Children.Add(label);
                fields.Children.Add(editor);
            }
            AddField(0, "TreeGit.Remote.EditorName", name);
            AddField(1, "TreeGit.Remote.Url", url);
            AddField(2, "TreeGit.Remote.OptionalKey", keyRow);
            panel.Children.Add(fields);
            var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false }; panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(RepositorySettings.Action("TreeGit.Ok", async () =>
            {
                var remoteName = name.Text?.Trim() ?? "";
                var remoteUrl = url.Text?.Trim() ?? "";
                var sshKey = key.Text?.Trim() ?? "";
                string message = null;
                if (!Regex.IsMatch(remoteName, @"^[\w.\-]+$")) message = App.Text("TreeGit.Remote.InvalidName");
                else if (repo.Remotes.Any(r => r.Name == remoteName && r.Name != remote?.Name)) message = App.Text("TreeGit.Remote.NameExists");
                else if (string.IsNullOrWhiteSpace(remoteUrl) || (!Models.Remote.IsValidURL(remoteUrl) && !Directory.Exists(remoteUrl))) message = App.Text("TreeGit.Remote.InvalidUrl");
                else if (!string.IsNullOrEmpty(sshKey) && !File.Exists(sshKey)) message = App.Text("TreeGit.Remote.KeyMissing");
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
                else { error.Text = App.Text("TreeGit.Remote.SaveFailed"); error.IsVisible = true; }
            }));
            buttons.Children.Add(RepositorySettings.Action("TreeGit.Cancel", () => { Close(false); return System.Threading.Tasks.Task.CompletedTask; }));
            RepositoryDialogLayout.Apply(this, panel, buttons, 550);
        }
    }
}
