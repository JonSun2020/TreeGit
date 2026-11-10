using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class Preferences : ChromelessWindow
    {
        public static readonly DirectProperty<Preferences, string> GitVersionProperty =
            AvaloniaProperty.RegisterDirect<Preferences, string>(
                nameof(GitVersion),
                static o => o.GitVersion);

        public string GitVersion
        {
            get => _gitVersion;
            set => SetAndRaise(GitVersionProperty, ref _gitVersion, value);
        }

        public static readonly DirectProperty<Preferences, bool> ShowGitVersionWarningProperty =
            AvaloniaProperty.RegisterDirect<Preferences, bool>(
                nameof(ShowGitVersionWarning),
                static o => o.ShowGitVersionWarning);

        public bool ShowGitVersionWarning
        {
            get => _showGitVersionWarning;
            set => SetAndRaise(ShowGitVersionWarningProperty, ref _showGitVersionWarning, value);
        }

        public string DefaultUser
        {
            get;
            set;
        }

        public string DefaultEmail
        {
            get;
            set;
        }

        public Models.CRLFMode CRLFMode
        {
            get;
            set;
        } = null;

        public bool EnablePruneOnFetch
        {
            get;
            set;
        }

        public bool EnableGPGCommitSigning
        {
            get;
            set;
        }

        public bool EnableGPGTagSigning
        {
            get;
            set;
        }

        public static readonly DirectProperty<Preferences, Models.GPGFormat> GPGFormatProperty =
            AvaloniaProperty.RegisterDirect<Preferences, Models.GPGFormat>(
                nameof(GPGFormat),
                static o => o.GPGFormat,
                static (o, v) => o.GPGFormat = v);

        public Models.GPGFormat GPGFormat
        {
            get => _gpgFormat;
            set => SetAndRaise(GPGFormatProperty, ref _gpgFormat, value);
        }

        public static readonly DirectProperty<Preferences, string> GPGExecutableFileProperty =
            AvaloniaProperty.RegisterDirect<Preferences, string>(
                nameof(GPGExecutableFile),
                static o => o.GPGExecutableFile,
                static (o, v) => o.GPGExecutableFile = v);

        public string GPGExecutableFile
        {
            get => _gpgExecutableFile;
            set => SetAndRaise(GPGExecutableFileProperty, ref _gpgExecutableFile, value);
        }

        public string GPGUserKey
        {
            get;
            set;
        }

        public bool EnableHTTPSSLVerify
        {
            get;
            set;
        } = false;

        public static readonly DirectProperty<Preferences, Models.CustomAction> SelectedCustomActionProperty =
            AvaloniaProperty.RegisterDirect<Preferences, Models.CustomAction>(
                nameof(SelectedCustomAction),
                static o => o.SelectedCustomAction,
                static (o, v) => o.SelectedCustomAction = v);

        public Models.CustomAction SelectedCustomAction
        {
            get => _selectedCustomAction;
            set => SetAndRaise(SelectedCustomActionProperty, ref _selectedCustomAction, value);
        }

        public Preferences()
        {
            var pref = ViewModels.Preferences.Instance;
            DataContext = pref;
            CloseOnESC = true;

            if (pref.IsGitConfigured())
            {
                var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { config = Commands.GlobalConfigEdit.ReadEffective(); _configurationKnown = true; _initialConfig = config; }
                catch { _configurationKnown = false; }

                if (config.TryGetValue("user.name", out var name))
                    DefaultUser = name;
                if (config.TryGetValue("user.email", out var email))
                    DefaultEmail = email;
                if (config.TryGetValue("user.signingkey", out var signingKey))
                    GPGUserKey = signingKey;
                if (config.TryGetValue("core.autocrlf", out var crlf))
                    CRLFMode = Models.CRLFMode.Supported.Find(x => x.Value.Equals(crlf, StringComparison.OrdinalIgnoreCase));
                if (config.TryGetValue("fetch.prune", out var pruneOnFetch))
                    EnablePruneOnFetch = ParseEnabledSetting(pruneOnFetch);
                if (config.TryGetValue("commit.gpgsign", out var gpgCommitSign))
                    EnableGPGCommitSigning = ParseEnabledSetting(gpgCommitSign);
                if (config.TryGetValue("tag.gpgsign", out var gpgTagSign))
                    EnableGPGTagSigning = ParseEnabledSetting(gpgTagSign);
                if (config.TryGetValue("gpg.format", out var gpgFormat))
                    GPGFormat = Models.GPGFormat.Supported.Find(x => x.Value == gpgFormat) ?? Models.GPGFormat.Supported[0];

                if (GPGFormat.Value == "openpgp" && config.TryGetValue("gpg.program", out var openpgp))
                    GPGExecutableFile = openpgp;
                else if (config.TryGetValue($"gpg.{GPGFormat.Value}.program", out var gpgProgram))
                    GPGExecutableFile = gpgProgram;

                if (config.TryGetValue("http.sslverify", out var sslVerify))
                    EnableHTTPSSLVerify = ParseEnabledSetting(sslVerify);
                else
                    EnableHTTPSSLVerify = true;
            }

            _initialGitValues = GetGitValues();
            _initialWindowFrame = pref.UseSystemWindowFrame;
            _editSession = pref.BeginEdit();
            UpdateGitVersion();
            InitializeComponent();
            if (pref.IsGitConfigured() && !_configurationKnown)
            {
                PreferencesSaveError.Text = App.Text("Preferences.Save.ReadFailed");
                PreferencesSaveError.IsVisible = true;
            }
            Opened += (_, _) => FitToScreen();
            ViewModels.Preferences.Instance.PropertyChanged += OnPreferencesChanged;
            Closed += (_, _) =>
            {
                ViewModels.Preferences.Instance.PropertyChanged -= OnPreferencesChanged;
                _editSession?.Dispose();
                _editSession = null;
            };
        }

        public void SelectPage(string page)
        {
            PreferenceTabs.SelectedIndex = page switch
            {
                "general" => 0, "appearance" => 1, "git" => 2, "signing" => 3, "integration" => 4,
                _ => throw new ArgumentException("Unknown preferences page."),
            };
        }

        private void OnPreferencesChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.Preferences.Zoom)) FitToScreen();
        }

        private void FitToScreen()
        {
            if (!IsVisible) return;
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var centerX = Position.X + Bounds.Width * scale / 2;
            var centerY = Position.Y + Bounds.Height * scale / 2;
            MaxWidth = Math.Max(1, area.Width / scale - 32);
            MaxHeight = Math.Max(1, area.Height / scale - 32);
            Width = Math.Min(680 * ViewModels.Preferences.Instance.Zoom, MaxWidth);
            Height = Math.Min(570 * ViewModels.Preferences.Instance.Zoom, MaxHeight);
            Position = new PixelPoint((int)Math.Clamp(centerX - Width * scale / 2, area.X + 8, Math.Max(area.X + 8, area.Right - Width * scale - 8)),
                (int)Math.Clamp(centerY - Height * scale / 2, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - Height * scale - 8)));
        }

        protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == GPGFormatProperty)
            {
                var requestedFormat = GPGFormat.Value;
                var programBeforeRead = GPGExecutableFile;
                Dictionary<string, string> config;
                try { config = await Task.Run(Commands.GlobalConfigEdit.ReadEffective); }
                catch { return; }
                if (requestedFormat != GPGFormat.Value || GPGExecutableFile != programBeforeRead) return;
                if (GPGFormat.Value == "openpgp" && config.TryGetValue("gpg.program", out var openpgp))
                    GPGExecutableFile = openpgp;
                else if (config.TryGetValue($"gpg.{GPGFormat.Value}.program", out var gpgProgram))
                    GPGExecutableFile = gpgProgram;
                else
                    GPGExecutableFile = string.Empty;
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_saving && !_accepted) { e.Cancel = true; return; }
            base.OnClosing(e);
        }

        private static bool ParseEnabledSetting(string value)
        {
            value = value.Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) && number != 0);
        }
        private static string NormalizeSetting(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
        private Dictionary<string, string> GetGitValues()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["user.name"] = NormalizeSetting(DefaultUser),
                ["user.email"] = NormalizeSetting(DefaultEmail),
                ["user.signingkey"] = NormalizeSetting(GPGUserKey),
                ["core.autocrlf"] = CRLFMode?.Value,
                ["fetch.prune"] = EnablePruneOnFetch ? "true" : "false",
                ["commit.gpgsign"] = EnableGPGCommitSigning ? "true" : "false",
                ["tag.gpgsign"] = EnableGPGTagSigning ? "true" : "false",
                ["http.sslverify"] = EnableHTTPSSLVerify ? "true" : "false",
                ["gpg.format"] = GPGFormat.Value,
            };
            values[GPGFormat.Value == "openpgp" ? "gpg.program" : $"gpg.{GPGFormat.Value}.program"] = NormalizeSetting(GPGExecutableFile);
            return values;
        }

        private async void OnSavePreferences(object sender, RoutedEventArgs e)
        {
            await SaveChangesAsync();
            e.Handled = true;
        }

        public string SaveError => PreferencesSaveError.IsVisible ? PreferencesSaveError.Text : null;
        public bool RestartRequired => _initialWindowFrame != ViewModels.Preferences.Instance.UseSystemWindowFrame;

        public async Task<bool> SaveChangesAsync()
        {
            if (_saving) return false;
            _saving = true; PreferenceTabs.IsEnabled = false;
            SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = false;
            PreferencesSaveError.IsVisible = false;
            try
            {
                var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in GetGitValues())
                {
                    _initialGitValues.TryGetValue(key, out var old);
                    if (old != value) changes[key] = value;
                }
                if (changes.Count > 0 && !_configurationKnown)
                    throw new InvalidOperationException(App.Text("Preferences.Save.ReadFailed"));
                using var git = await Commands.GlobalConfigEdit.PrepareAsync(changes, _initialConfig);
                _editSession.SaveCandidate();
                try { git.Commit(); }
                catch
                {
                    // Put original editable preferences back on disk while retaining
                    // the visible draft for retry; Git's file was not replaced.
                    ViewModels.Preferences.Instance.Save(requireSuccess: true);
                    throw;
                }
                _editSession.Accept(); _accepted = true;
                if (RestartRequired) _ = ConfirmRestartAfterSavingAsync();
                else Close();
            }
            catch (Exception error)
            {
                Native.OS.LogException(error);
                PreferencesSaveError.Text = error is InvalidOperationException ? error.Message : App.Text("Preferences.Save.Failed");
                PreferencesSaveError.IsVisible = true;
            }
            finally
            {
                _saving = false; PreferenceTabs.IsEnabled = true;
                SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = true;
            }
            return _accepted;
        }

        private async Task ConfirmRestartAfterSavingAsync()
        {
            // Saving is complete. Release MCP's operation gate before asking the
            // user about restarting so the confirmation can also be dismissed by AI.
            try { await this.ShowDialogAsync(new ConfirmRestart()); }
            catch (Exception error) { Native.OS.LogException(error); }
            finally { Close(); }
        }
        private void OnCancelPreferences(object sender, RoutedEventArgs e)
        {
            if (!_saving) Close();
            e.Handled = true;
        }

        private async void SelectThemeOverrideFile(object _, RoutedEventArgs e)
        {
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType("Theme Overrides File") { Patterns = ["*.json"] }],
                AllowMultiple = false,
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                    ViewModels.Preferences.Instance.ThemeOverrides = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select theme override file: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private void OpenThemeRepository(object _, RoutedEventArgs e)
        {
            Native.OS.OpenBrowser($"https://github.com/sourcegit-scm/sourcegit-theme");
            e.Handled = true;
        }

        private async void SelectGitExecutable(object _, RoutedEventArgs e)
        {
            var pattern = OperatingSystem.IsWindows() ? "git.exe" : "git";
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType("Git Executable") { Patterns = [pattern] }],
                AllowMultiple = false,
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                {
                    ViewModels.Preferences.Instance.GitInstallPath = selected[0].Path.LocalPath;
                    UpdateGitVersion();
                }
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select git executable: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private async void SelectDefaultCloneDir(object _, RoutedEventArgs e)
        {
            var options = new FolderPickerOpenOptions() { AllowMultiple = false };
            try
            {
                var selected = await StorageProvider.OpenFolderPickerAsync(options);
                if (selected.Count == 1)
                {
                    var folder = selected[0];
                    var folderPath = folder is { Path: { IsAbsoluteUri: true } path } ? path.LocalPath : folder?.Path.ToString();
                    ViewModels.Preferences.Instance.GitDefaultCloneDir = folderPath;
                }
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select default clone directory: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private async void SelectGPGExecutable(object _, RoutedEventArgs e)
        {
            var patterns = new List<string>();
            if (OperatingSystem.IsWindows())
                patterns.Add($"{GPGFormat.Program}.exe");
            else
                patterns.Add(GPGFormat.Program);

            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType("GPG Program") { Patterns = patterns }],
                AllowMultiple = false,
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                    GPGExecutableFile = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select gpg program: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private async void SelectShellOrTerminal(object _, RoutedEventArgs e)
        {
            var type = ViewModels.Preferences.Instance.ShellOrTerminalType;
            if (type == -1)
                return;

            var shell = Models.ShellOrTerminal.Supported[type];
            var options = new FilePickerOpenOptions() { AllowMultiple = false };
            if (shell.Type != "custom")
            {
                options = new FilePickerOpenOptions()
                {
                    FileTypeFilter = [new FilePickerFileType(shell.Name)
                    {
                        Patterns = shell.Exec.Split('|',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    }],
                    AllowMultiple = false,
                };
            }

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                    ViewModels.Preferences.Instance.ShellOrTerminalPath = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select shell/terminal: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private async void SelectExternalMergeTool(object _, RoutedEventArgs e)
        {
            var type = ViewModels.Preferences.Instance.ExternalMergeToolType;
            if (type <= 0 || type >= Models.ExternalMerger.Supported.Count)
            {
                ViewModels.Preferences.Instance.ExternalMergeToolType = 0;
                e.Handled = true;
                return;
            }

            var tool = Models.ExternalMerger.Supported[type];
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType(tool.Name) { Patterns = tool.GetPatternsToFindExecFile() }],
                AllowMultiple = false,
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                    ViewModels.Preferences.Instance.ExternalMergeToolPath = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select merge tool: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private void OnUseNativeWindowFrameChanged(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box) ViewModels.Preferences.Instance.UseSystemWindowFrame = box.IsChecked == true;
            e.Handled = true;
        }

        private void OnGitInstallPathChanged(object sender, TextChangedEventArgs e)
        {
            UpdateGitVersion();
        }

        private void OnAddCustomAction(object sender, RoutedEventArgs e)
        {
            var action = new Models.CustomAction() { Name = "Unnamed Action (Global)" };
            ViewModels.Preferences.Instance.CustomActions.Add(action);
            SelectedCustomAction = action;

            e.Handled = true;
        }

        private async void SelectExecutableForCustomAction(object sender, RoutedEventArgs e)
        {
            var options = new FilePickerOpenOptions()
            {
                AllowMultiple = false,
                FileTypeFilter = [new("Executable file(script)") { Patterns = ["*"] }]
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 } && sender is Button { DataContext: Models.CustomAction action })
                    action.Executable = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, $"Failed to select executable for custom action: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private void OnRemoveSelectedCustomAction(object sender, RoutedEventArgs e)
        {
            if (SelectedCustomAction == null)
                return;

            ViewModels.Preferences.Instance.CustomActions.Remove(SelectedCustomAction);
            SelectedCustomAction = null;
            e.Handled = true;
        }

        private void OnMoveSelectedCustomActionUp(object sender, RoutedEventArgs e)
        {
            if (SelectedCustomAction == null)
                return;

            var idx = ViewModels.Preferences.Instance.CustomActions.IndexOf(SelectedCustomAction);
            if (idx > 0)
                ViewModels.Preferences.Instance.CustomActions.Move(idx - 1, idx);

            e.Handled = true;
        }

        private void OnMoveSelectedCustomActionDown(object sender, RoutedEventArgs e)
        {
            if (SelectedCustomAction == null)
                return;

            var idx = ViewModels.Preferences.Instance.CustomActions.IndexOf(SelectedCustomAction);
            if (idx < ViewModels.Preferences.Instance.CustomActions.Count - 1)
                ViewModels.Preferences.Instance.CustomActions.Move(idx + 1, idx);

            e.Handled = true;
        }

        private async void EditCustomActionControls(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: Models.CustomAction act })
                return;

            await this.ShowDialogAsync(new ViewModels.ConfigureCustomActionControls(act.Controls));
            e.Handled = true;
        }

        private void UpdateGitVersion()
        {
            GitVersion = Native.OS.GitVersionString;
            ShowGitVersionWarning = !string.IsNullOrEmpty(GitVersion) && Native.OS.GitVersion < Models.GitVersions.MINIMAL;
        }

        private ViewModels.Preferences.EditSession _editSession;
        private Dictionary<string, string> _initialConfig = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _initialGitValues;
        private bool _configurationKnown;
        private bool _initialWindowFrame;
        private bool _saving;
        private bool _accepted;
        private string _gitVersion = string.Empty;
        private bool _showGitVersionWarning = false;
        private Models.GPGFormat _gpgFormat = Models.GPGFormat.Supported[0];
        private string _gpgExecutableFile = string.Empty;
        private Models.CustomAction _selectedCustomAction = null;
    }
}
