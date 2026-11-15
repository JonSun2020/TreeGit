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
            App.BindText(this, TitleProperty, "TreeGit.Credentials.Title");
            Width = 520;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            CloseOnESC = true;
            ShowActivated = true;
            Opened += (_, _) => Activate();
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
            panel.Children.Add(new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.Authentication"), FontSize = 20 });
            panel.Children.Add(new TextBlock { Text = repo.FullPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.Description"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var remotes = new ComboBox { ItemsSource = new Commands.QueryRemotes(repo.FullPath).GetResult(), DisplayMemberBinding = new Avalonia.Data.Binding("Name"), HorizontalAlignment = HorizontalAlignment.Stretch };
            var username = new TextBox { Name = "CredentialUsername", Height = 32, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Credentials.UsernameHint") };
            var password = new TextBox { Name = "CredentialPassword", Height = 32, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Credentials.OptionalToken"), PasswordChar = '*' };
            var showPassword = new ToggleButton { Classes = { "toggle_untracked" }, Width = 26, Height = 14 };
            ToolTip.SetTip(showPassword, App.Text("TreeGit.Credentials.TogglePassword"));
            password.InnerRightContent = showPassword;
            showPassword.IsCheckedChanged += (_, _) => password.RevealPassword = showPassword.IsChecked == true;
            var useManager = new CheckBox { Name = "CredentialMode", Content = new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.SystemAuth"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath) };
            var message = new TextBlock { Name = "CredentialStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var save = new Button { Name = "ConfirmCredentials", [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Save"), IsDefault = true, Classes = { "flat", "primary" } };
            var forget = new Button { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Credentials.Forget"), Classes = { "flat" } };
            var close = new Button { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Close"), Classes = { "flat" } };
            Uri SelectedUri()
            {
                if (remotes.SelectedItem is Models.Remote r && Uri.TryCreate(r.URL, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    return uri;
                throw new InvalidOperationException(App.Text("TreeGit.Credentials.SelectHttp"));
            }
            var loadingPassword = false;
            var passwordEdited = false;
            var saving = false;
            void UpdateEnabled()
            {
                save.IsEnabled = forget.IsEnabled = useManager.IsEnabled = remotes.IsEnabled = !saving;
                username.IsEnabled = password.IsEnabled = !saving && useManager.IsChecked != true;
            }
            useManager.IsCheckedChanged += (_, _) => UpdateEnabled();
            password.PropertyChanged += (_, change) =>
            {
                if (!loadingPassword && change.Property == TextBox.TextProperty) passwordEdited = true;
            };
            remotes.SelectionChanged += (_, _) =>
            {
                username.Text = string.Empty;
                loadingPassword = true;
                password.Text = string.Empty;
                loadingPassword = false;
                passwordEdited = false;
                try
                {
                    var existing = Native.RepositoryCredentials.Get(repo.FullPath, SelectedUri());
                    username.Text = existing?.Username ?? string.Empty;
                    message.Text = existing.HasValue ? App.Text("TreeGit.Credentials.Exists") : App.Text("TreeGit.Credentials.None");
                }
                catch (Exception ex) { message.Text = ex.Message; }
            };
            save.Click += async (_, _) =>
            {
                if (saving) return;
                var manager = useManager.IsChecked == true;
                var enteredUser = username.Text?.Trim() ?? string.Empty;
                var enteredPassword = password.Text ?? string.Empty;
                var editedPassword = passwordEdited;
                saving = true; UpdateEnabled();
                try
                {
                    Uri remoteUri = null;
                    (string Username, string Password)? previous = null;
                    if (!manager)
                    {
                        if (string.IsNullOrWhiteSpace(enteredUser))
                            throw new InvalidOperationException(App.Text("TreeGit.Credentials.UsernameRequired"));
                        remoteUri = SelectedUri();
                        previous = Native.RepositoryCredentials.Get(repo.FullPath, remoteUri);
                        if (!editedPassword && previous.HasValue && previous.Value.Username == enteredUser)
                            enteredPassword = previous.Value.Password;
                    }
                    using var watcher = repo.LockWatcher();
                    using var transaction = new Models.RepositoryConfigurationTransaction(repo.FullPath, repo.Settings.StoragePath);
                    if (!await new Commands.Config(repo.FullPath, transaction.GitConfigPath) { RaiseError = false }.SetAsync("sourcegit.credentialmanager", manager ? "true" : "false"))
                        throw new InvalidOperationException(App.Text("TreeGit.Credentials.ModeSaveFailed"));
                    if (!manager) Native.RepositoryCredentials.Save(repo.FullPath, remoteUri, enteredUser, enteredPassword);
                    try { transaction.Commit(); }
                    catch
                    {
                        if (!manager)
                        {
                            if (previous.HasValue) Native.RepositoryCredentials.Save(repo.FullPath, remoteUri, previous.Value.Username, previous.Value.Password);
                            else Native.RepositoryCredentials.Forget(repo.FullPath, remoteUri);
                        }
                        throw;
                    }
                    Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, manager);
                    loadingPassword = true;
                    password.Text = string.Empty;
                    loadingPassword = false;
                    passwordEdited = false;
                    message.Text = manager ? App.Text("TreeGit.Credentials.SystemModeSaved") : App.Text("TreeGit.Credentials.Saved");
                }
                catch (Exception ex) { message.Text = ex.Message; }
                finally { saving = false; UpdateEnabled(); }
            };
            forget.Click += (_, _) =>
            {
                try
                {
                    Native.RepositoryCredentials.Forget(repo.FullPath, SelectedUri());
                    password.Text = string.Empty;
                    message.Text = App.Text("TreeGit.Credentials.Cleared");
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
            RepositoryDialogLayout.Apply(this, panel, buttons, 520);
            UpdateEnabled();
            if (remotes.ItemCount > 0)
                remotes.SelectedIndex = 0;
            Closed += (_, _) => password.Text = string.Empty;
        }
    }
}
