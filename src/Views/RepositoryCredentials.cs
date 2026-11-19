using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace SourceGit.Views
{
    internal sealed class RepositoryCredentials : ChromelessWindow
    {
        private sealed record Target(string RemoteName, string Url, string Direction)
        {
            public string DisplayName => Direction == null ? RemoteName : $"{RemoteName} ({App.Text(Direction)})";
        }

        public RepositoryCredentials(ViewModels.Repository repo, string remoteName = null)
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
            var query = new Commands.QueryRemotes(repo.FullPath);
            var remoteList = query.GetResult();
            var targets = new List<Target>();
            foreach (var remote in remoteList)
            {
                var separatePush = !string.IsNullOrEmpty(remote.PushURL) && remote.PushURL != remote.URL;
                targets.Add(new Target(remote.Name, remote.URL, separatePush ? "TreeGit.Menu.Pull" : null));
                if (separatePush) targets.Add(new Target(remote.Name, remote.PushURL, "TreeGit.Menu.Push"));
            }
            var remotes = new ComboBox { Name = "CredentialRemote", ItemsSource = targets, DisplayMemberBinding = new Avalonia.Data.Binding("DisplayName"), HorizontalAlignment = HorizontalAlignment.Stretch };
            var username = new TextBox { Name = "CredentialUsername", Height = 32, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Credentials.UsernameHint") };
            var password = new PasswordTextBox { Name = "CredentialPassword", Height = 32, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Credentials.OptionalToken"), PasswordChar = '*' };
            var showPassword = new ToggleButton { Classes = { "toggle_untracked" }, Width = 26, Height = 14 };
            ToolTip.SetTip(showPassword, App.Text("TreeGit.Credentials.TogglePassword"));
            password.InnerRightContent = showPassword;
            showPassword.IsCheckedChanged += (_, _) => password.RevealPassword = showPassword.IsChecked == true;
            var useManager = new CheckBox { Name = "CredentialMode", Content = new TextBlock { [!Avalonia.Controls.TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.SystemAuth"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, IsChecked = Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath) };
            var message = new TextBlock { Name = "CredentialStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            void ErrorMessage(Exception error, string fallback)
            {
                Native.OS.LogException(error);
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                message.Text = error is InvalidOperationException ? error.Message : App.Text(fallback);
            }
            var save = new Button { Name = "ConfirmCredentials", [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Ok"), IsDefault = true, Classes = { "flat", "primary", "dialog_action" } };
            var forget = new Button { Name = "ForgetCredentials", [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Credentials.Forget"), Classes = { "flat" } };
            var close = new Button { Name = "CancelCredentials", [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Cancel"), Classes = { "flat", "dialog_action" } };
            var lifetime = new CancellationTokenSource();
            var resolvedTargets = new Dictionary<Target, IReadOnlyList<Uri>>();
            IReadOnlyList<Uri> credentialUris = [];
            var selectionVersion = 0;
            var loading = false;
            var changingMode = false;
            var confirmedClose = false;
            var sanitizedTargets = new HashSet<Target>();
            Uri SelectedUri()
            {
                if (remotes.SelectedItem is Target r && Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    return sanitizedTargets.Contains(r) ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri : uri;
                throw new InvalidOperationException(App.Text("TreeGit.Credentials.SelectHttp"));
            }
            (string Username, string Password)? EmbeddedAccount(Uri uri)
            {
                if (string.IsNullOrEmpty(uri.UserInfo)) return null;
                var parts = uri.UserInfo.Split(':', 2);
                return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
            }
            var loadingPassword = false;
            var passwordEdited = false;
            var saving = false;
            var clearPending = false;
            void UpdateEnabled()
            {
                var isHttp = remotes.SelectedItem is Target selected && Uri.TryCreate(selected.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
                remotes.IsEnabled = !saving && query.Succeeded;
                save.IsEnabled = useManager.IsEnabled = !saving && !loading && query.Succeeded && isHttp;
                username.IsEnabled = password.IsEnabled = save.IsEnabled && useManager.IsChecked != true;
                forget.IsEnabled = save.IsEnabled;
                close.IsEnabled = !saving;
            }
            password.PropertyChanged += (_, change) =>
            {
                if (!loadingPassword && change.Property == TextBox.TextProperty) passwordEdited = true;
            };
            async void LoadSelected()
            {
                var version = ++selectionVersion;
                loading = true;
                credentialUris = [];
                username.Text = string.Empty;
                loadingPassword = true;
                password.Text = string.Empty;
                loadingPassword = false;
                passwordEdited = false;
                clearPending = false;
                message.ClearValue(TextBlock.ForegroundProperty);
                UpdateEnabled();
                if (!query.Succeeded) { loading = false; UpdateEnabled(); message.Text = App.Text("TreeGit.Config.ReadFailed"); return; }
                try
                {
                    var uri = SelectedUri();
                    message.Text = App.Text("TreeGit.Credentials.Loading");
                    var target = (Target)remotes.SelectedItem;
                    if (!resolvedTargets.TryGetValue(target, out var uris))
                    {
                        uris = await Native.RepositoryCredentials.ResolveCredentialUrisAsync(uri, lifetime.Token);
                        resolvedTargets[target] = uris;
                    }
                    if (version != selectionVersion || lifetime.IsCancellationRequested) return;
                    credentialUris = uris;
                    var existing = uris.Reverse().Select(candidate => Native.RepositoryCredentials.Get(repo.FullPath, candidate)).FirstOrDefault(account => account.HasValue) ?? EmbeddedAccount(uri);
                    username.Text = existing?.Username ?? string.Empty;
                    message.Text = useManager.IsChecked == true ? App.Text("TreeGit.Credentials.SystemManaged") :
                        existing.HasValue ? App.Text("TreeGit.Credentials.Exists") : App.Text("TreeGit.Credentials.None");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (version == selectionVersion) ErrorMessage(ex, "TreeGit.Credentials.ReadFailed"); }
                finally { if (version == selectionVersion && !lifetime.IsCancellationRequested) { loading = false; UpdateEnabled(); } }
            }
            useManager.IsCheckedChanged += (_, _) => { if (!saving && !changingMode) LoadSelected(); };
            remotes.SelectionChanged += (_, _) => LoadSelected();
            save.Click += async (_, _) =>
            {
                if (saving || loading) return;
                var enteredUser = username.Text?.Trim() ?? string.Empty;
                var enteredPassword = password.Text ?? string.Empty;
                var manager = useManager.IsChecked == true;
                var clearing = !manager && string.IsNullOrWhiteSpace(enteredUser) && string.IsNullOrEmpty(enteredPassword);
                var editedPassword = passwordEdited;
                saving = true; UpdateEnabled();
                Target sanitizedTarget = null;
                try
                {
                    Uri remoteUri = null;
                    var previous = new Dictionary<Uri, (string Username, string Password)?>();
                    if (!manager)
                    {
                        if (!clearing && string.IsNullOrWhiteSpace(enteredUser))
                            throw new InvalidOperationException(App.Text("TreeGit.Credentials.UsernameRequired"));
                        remoteUri = SelectedUri();
                        foreach (var candidate in credentialUris) previous[candidate] = Native.RepositoryCredentials.Get(repo.FullPath, candidate);
                        if (previous.Count == 0) previous[remoteUri] = Native.RepositoryCredentials.Get(repo.FullPath, remoteUri);
                        var oldAccount = previous.Values.Reverse().FirstOrDefault(account => account.HasValue) ?? EmbeddedAccount(remoteUri);
                        if (!clearPending && !editedPassword && oldAccount.HasValue && oldAccount.Value.Username == enteredUser)
                            enteredPassword = oldAccount.Value.Password;
                        if (enteredUser.IndexOfAny(['\r', '\n', '\0']) >= 0 || enteredPassword.IndexOfAny(['\r', '\n', '\0']) >= 0)
                            throw new InvalidOperationException(App.Text("TreeGit.Credentials.InvalidCharacters"));
                        if (Encoding.Unicode.GetByteCount(enteredPassword) > 5120)
                            throw new InvalidOperationException(App.Text("TreeGit.Credentials.TooLong"));
                    }
                    using var watcher = repo.LockWatcher();
                    using var transaction = new Models.RepositoryConfigurationTransaction(repo.FullPath, repo.Settings.StoragePath);
                    if (!await new Commands.Config(repo.FullPath, transaction.GitConfigPath) { RaiseError = false }.SetAsync("sourcegit.credentialmanager", manager ? "true" : "false"))
                        throw new InvalidOperationException(App.Text("TreeGit.Credentials.ModeSaveFailed"));
                    if (!manager && !string.IsNullOrEmpty(remoteUri.UserInfo) && remotes.SelectedItem is Target selected)
                    {
                        // A credential embedded in the URL would otherwise keep
                        // bypassing the saved account after clearing or switching.
                        var clean = new UriBuilder(remoteUri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
                        var key = "remote." + selected.RemoteName + (selected.Direction == "TreeGit.Menu.Push" ? ".pushurl" : ".url");
                        if (!await new Commands.Config(repo.FullPath, transaction.GitConfigPath) { RaiseError = false }.SetAsync(key, clean))
                            throw new InvalidOperationException(App.Text("TreeGit.Credentials.ModeSaveFailed"));
                        sanitizedTarget = selected;
                    }
                    try
                    {
                        if (!manager)
                            foreach (var candidate in previous.Keys)
                                if (clearing) Native.RepositoryCredentials.Forget(repo.FullPath, candidate);
                                else Native.RepositoryCredentials.Save(repo.FullPath, candidate, enteredUser, enteredPassword);
                        transaction.Commit();
                    }
                    catch
                    {
                        if (!manager)
                        {
                            foreach (var account in previous)
                                if (account.Value.HasValue) Native.RepositoryCredentials.Save(repo.FullPath, account.Key, account.Value.Value.Username, account.Value.Value.Password);
                                else Native.RepositoryCredentials.Forget(repo.FullPath, account.Key);
                        }
                        throw;
                    }
                    Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, manager);
                    if (sanitizedTarget != null) { sanitizedTargets.Add(sanitizedTarget); repo.RefreshAll(); }
                    useManager.IsChecked = manager;
                    loadingPassword = true;
                    password.Text = string.Empty;
                    loadingPassword = false;
                    passwordEdited = false;
                    message.Text = manager ? App.Text("TreeGit.Credentials.SystemModeSaved") :
                        clearing ? App.Text("TreeGit.Credentials.Cleared") : App.Text("TreeGit.Credentials.Saved");
                    clearPending = false;
                    confirmedClose = true;
                    Close();
                }
                catch (Exception ex) { ErrorMessage(ex, "TreeGit.Credentials.SaveFailed"); }
                finally { saving = false; UpdateEnabled(); }
            };
            forget.Click += (_, _) =>
            {
                try
                {
                    _ = SelectedUri();
                    changingMode = true;
                    useManager.IsChecked = false;
                    changingMode = false;
                    clearPending = true;
                    message.ClearValue(TextBlock.ForegroundProperty);
                    username.Text = string.Empty;
                    loadingPassword = true;
                    password.Text = string.Empty;
                    loadingPassword = false;
                    passwordEdited = false;
                    message.Text = App.Text("TreeGit.Credentials.ClearPending");
                    UpdateEnabled();
                }
                catch (Exception ex) { ErrorMessage(ex, "TreeGit.Credentials.ReadFailed"); }
            };
            close.Click += (_, _) => Close();
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            forget.Margin = new Thickness(0, 0, 8, 8);
            var confirmation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            buttons.Children.Add(forget);
            confirmation.Children.Add(save);
            confirmation.Children.Add(close);
            buttons.Children.Add(confirmation);
            panel.Children.Add(remotes);
            panel.Children.Add(useManager);
            panel.Children.Add(new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.UsernameLabel") });
            panel.Children.Add(username);
            panel.Children.Add(new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.PasswordLabel") });
            panel.Children.Add(password);
            panel.Children.Add(message);
            panel.Children.Add(new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.IdentityHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 });
            RepositoryDialogLayout.Apply(this, panel, buttons, 520);
            UpdateEnabled();
            if (remotes.ItemCount > 0)
                remotes.SelectedItem = remotes.Items.OfType<Target>().FirstOrDefault(r => r.RemoteName == remoteName) ?? remotes.Items[0];
            else LoadSelected();
            Closing += (_, e) => { if (saving && !confirmedClose) e.Cancel = true; };
            Closed += (_, _) => { lifetime.Cancel(); password.Text = string.Empty; };
        }
    }
}
