using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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

#if false // The custom-action editor is absent from Preferences.
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
#endif

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
            InitializeComponent();
            KeepDropdownAnchor(ShellSelector);
            AutoFetchIntervalInput.Maximum = ViewModels.Preferences.MaximumAutoFetchInterval;
            AutoFetchIntervalInput.AddHandler(TextBox.TextChangedEvent, OnAutoFetchIntervalTextChanged);
            AutoFetchIntervalInput.LostFocus += (_, _) => FillEmptyAutoFetchInterval();
            _initialGitPath = pref.GitInstallPath ?? string.Empty;
            GitPathInput.Text = _initialGitPath;
            UseBundledGitChoice.IsVisible = OperatingSystem.IsWindows();
            UseBundledGitChoice.IsChecked = OperatingSystem.IsWindows() && pref.UseBundledGit;
            UseBundledGitChoice.IsCheckedChanged += async (_, _) =>
            {
                if (!UseBundledGitChoice.IsEnabled) return;
                if (UseBundledGitChoice.IsChecked == false)
                    await DetectGitAsync();
                else if (_noSystemGitFound)
                    ShowGitVersion(string.Empty, null);
            };
            if (pref.IsGitConfigured() && !_configurationKnown)
            {
                PreferencesSaveError.Text = App.Text("Preferences.Save.ReadFailed");
                PreferencesSaveError.IsVisible = true;
            }
            Opened += (_, _) =>
            {
                FitToScreen();
                if (_integrationOnly) _ = LoadGitVersionsAsync();
            };
            ViewModels.Preferences.Instance.PropertyChanged += OnPreferencesChanged;
            Closed += (_, _) =>
            {
                ViewModels.Preferences.Instance.PropertyChanged -= OnPreferencesChanged;
                _editSession?.Dispose();
                _editSession = null;
            };
        }

        private bool _integrationOnly;

        public void SelectPage(string page)
        {
            var wasIntegrationOnly = _integrationOnly;
            _integrationOnly = page is "integration" or "git" or "terminal" or "diff-merge";
            ExternalToolTabs.IsVisible = _integrationOnly;
            PreferenceTabs.IsVisible = !_integrationOnly;
            App.BindText(this, TitleProperty, _integrationOnly ? "Preferences.Integration" : "Preferences");
            if (_integrationOnly)
            {
                ExternalToolTabs.SelectedIndex = page switch { "terminal" => 1, "diff-merge" => 2, _ => 0 };
                if (IsVisible && !wasIntegrationOnly) _ = LoadGitVersionsAsync();
            }
            else PreferenceTabs.SelectedIndex = page switch
            {
                "general" => 0, "appearance" => 1, "signing" => 2,
                _ => throw new ArgumentException("Unknown preferences page."),
            };
            if (_integrationOnly != wasIntegrationOnly) FitToScreen();
        }

        private void OnPreferencesChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.Preferences.Zoom)) FitToScreen();
            if (!_saving && e.PropertyName == nameof(ViewModels.Preferences.UseBundledGit))
                UseBundledGitChoice.IsChecked = UseBundledGitChoice.IsEnabled && ViewModels.Preferences.Instance.UseBundledGit;
        }

        private void FitToScreen()
        {
            var (width, height) = _integrationOnly ? (680, 464) : (620, 370);
            if (!IsVisible)
            {
                Width = width;
                Height = height;
                return;
            }
            var screen = Screens.ScreenFromWindow(this);
            if (screen == null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var centerX = Position.X + Bounds.Width * scale / 2;
            var centerY = Position.Y + Bounds.Height * scale / 2;
            MaxWidth = Math.Max(1, area.Width / scale - 32);
            MaxHeight = Math.Max(1, area.Height / scale - 32);
            Width = Math.Min(width * ViewModels.Preferences.Instance.Zoom, MaxWidth);
            Height = Math.Min(height * ViewModels.Preferences.Instance.Zoom, MaxHeight);
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
            if (_saving || _detectingGit) return false;
            if (_integrationOnly) FillEmptyAutoFetchInterval();
            _saving = true; SettingsBody.IsEnabled = false;
            SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = false;
            PreferencesSaveError.IsVisible = false;
            try
            {
                var pref = ViewModels.Preferences.Instance;
                var input = GitPathInput.Text?.Trim().Trim('"') ?? string.Empty;
                var forceBundled = OperatingSystem.IsWindows() && UseBundledGitChoice.IsChecked == true;
                string selected = null;
                if (_integrationOnly && (input != _initialGitPath || forceBundled != pref.UseBundledGit))
                {
                    selected = await Task.Run(() =>
                    {
                        if (!forceBundled && Native.GitExecutableLocator.IsUsable(input)) return input;
                        return OperatingSystem.IsWindows() ? Native.GitExecutableLocator.FindVerifiedBundledGit() : Native.OS.FindGitExecutable();
                    });
                    if (string.IsNullOrEmpty(selected))
                    {
                        if (forceBundled)
                        {
                            ApplyBundledGitAvailability(null);
                            return false;
                        }
                        throw new InvalidOperationException(App.Text(OperatingSystem.IsWindows() ? "TreeGit.Operation.GitUnavailable" : "Preferences.Git.Invalid"));
                    }
                }
                var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in GetGitValues())
                {
                    var gitPage = key is "user.name" or "user.email" or "core.autocrlf" or "fetch.prune" or "http.sslverify";
                    if (gitPage != _integrationOnly) continue;
                    _initialGitValues.TryGetValue(key, out var old);
                    if (old != value) changes[key] = value;
                }
                if (changes.Count > 0 && !_configurationKnown)
                    throw new InvalidOperationException(App.Text("Preferences.Save.ReadFailed"));
                using var git = await Commands.GlobalConfigEdit.PrepareAsync(changes, _initialConfig);
                var oldPath = pref.GitInstallPath;
                var oldBundled = pref.UseBundledGit;
                try
                {
                    if (selected != null)
                    {
                        pref.GitInstallPath = input;
                        pref.UseBundledGit = OperatingSystem.IsWindows() && Native.GitExecutableLocator.IsBundled(selected);
                    }
                    _editSession.SaveCandidate();
                    git.Commit();
                }
                catch
                {
                    pref.GitInstallPath = oldPath;
                    pref.UseBundledGit = oldBundled;
                    pref.Save(requireSuccess: true);
                    throw;
                }
                if (selected != null) Native.OS.GitExecutable = selected;
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
                _saving = false; SettingsBody.IsEnabled = true;
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

#if false // The JSON theme picker is absent from Preferences; saved themes still load.
        private async void SelectThemeOverrideFile(object _, RoutedEventArgs e)
        {
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType(App.Text("TreeGit.Message.ThemeFile")) { Patterns = ["*.json"] }],
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
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.ThemeFileSelect", ex.Message), true);
            }

            e.Handled = true;
        }

        private void OpenThemeRepository(object _, RoutedEventArgs e)
        {
            Native.OS.OpenBrowser($"https://github.com/sourcegit-scm/sourcegit-theme");
            e.Handled = true;
        }
#endif

        private async void SelectGitExecutable(object _, RoutedEventArgs e)
        {
            var pattern = OperatingSystem.IsWindows() ? "git.exe" : "git";
            var options = new FilePickerOpenOptions()
            {
                FileTypeFilter = [new FilePickerFileType(App.Text("TreeGit.Message.GitExecutable")) { Patterns = [pattern] }],
                AllowMultiple = false,
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 })
                {
                    GitPathInput.Text = selected[0].Path.LocalPath;
                }
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.GitSelect", ex.Message), true);
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
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.CloneFolderSelect", ex.Message), true);
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
                FileTypeFilter = [new FilePickerFileType(App.Text("TreeGit.Message.GPGProgram")) { Patterns = patterns }],
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
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.GpgSelect", ex.Message), true);
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
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.ShellSelect", ex.Message), true);
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
            if (tool.IsGitConfiguration) { e.Handled = true; return; }
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
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.MergeSelect", ex.Message), true);
            }

            e.Handled = true;
        }

        private void OnUseNativeWindowFrameChanged(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box) ViewModels.Preferences.Instance.UseSystemWindowFrame = box.IsChecked == true;
            e.Handled = true;
        }

        private void OnAutoFetchIntervalTogglePressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint((Control)sender).Properties.IsLeftButtonPressed) return;

            var enable = AutoFetchCheckBox.IsChecked != true;
            AutoFetchCheckBox.SetCurrentValue(ToggleButton.IsCheckedProperty, enable);
            if (enable)
                Dispatcher.UIThread.Post(() => AutoFetchIntervalInput.Focus(), DispatcherPriority.Input);
            e.Handled = true;
        }

        private void OnAutoFetchIntervalTextChanged(object sender, TextChangedEventArgs e)
        {
            if (e.Source is not TextBox input || string.IsNullOrEmpty(input.Text)) return;

            var text = input.Text;
            var number = 0;
            var digitsAfterCaret = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var digit = CharUnicodeInfo.GetDecimalDigitValue(text, index);
                if (digit < 0) continue;
                number = Math.Min(ViewModels.Preferences.MaximumAutoFetchInterval, number * 10 + digit);
                if (index >= input.CaretIndex) digitsAfterCaret++;
                if (char.IsSurrogatePair(text, index)) index++;
            }

            var normalized = Math.Max(1, number).ToString(CultureInfo.InvariantCulture);
            if (normalized == text) return;

            input.SetCurrentValue(TextBox.TextProperty, normalized);
            input.CaretIndex = Math.Max(0, normalized.Length - digitsAfterCaret);
        }

        private void FillEmptyAutoFetchInterval()
        {
            if (string.IsNullOrEmpty(AutoFetchIntervalInput.Text))
                AutoFetchIntervalInput.SetCurrentValue(NumericUpDown.TextProperty, "1");
        }

        private async Task LoadGitVersionsAsync()
        {
            var local = RefreshGitVersionAsync();
            if (OperatingSystem.IsWindows())
            {
                var bundled = await Task.Run(() => Native.GitExecutableLocator.ReadBundledVersion(AppContext.BaseDirectory));
                if (IsVisible) ApplyBundledGitAvailability(bundled);
            }
            await local;
        }

        private void ApplyBundledGitAvailability(string version)
        {
            var available = !string.IsNullOrEmpty(version);
            UseBundledGitChoice.IsEnabled = false;
            UseBundledGitChoice.IsChecked = available && ViewModels.Preferences.Instance.UseBundledGit;
            BundledGitVersionLabel.Text = available ? $"- {version}" : string.Empty;
            BundledGitVersionLabel.IsVisible = available;
            MissingBundledGitHint.IsVisible = !available;
            UseBundledGitChoice.IsEnabled = available;
        }

        private void OnDownloadBundledGit(object sender, RoutedEventArgs e) =>
            Native.OS.OpenBrowser(Native.TreeGitUpdater.DownloadPage);

        private async void OnGitInstallPathChanged(object sender, TextChangedEventArgs e)
        {
            if (!_integrationOnly || !IsVisible || _detectingGit || GitPathInput.Text == _versionPath)
                return;
            await RefreshGitVersionAsync(debounce: true);
        }

        private async Task RefreshGitVersionAsync(bool debounce = false)
        {
            var path = GitPathInput.Text;
            var revision = ++_gitVersionRevision;
            ShowGitVersion(string.Empty, null);
            if (debounce) await Task.Delay(250);
            if (!IsVisible || revision != _gitVersionRevision) return;

            var (version, number) = await Task.Run(() => ReadGitVersion(path));
            if (!IsVisible || revision != _gitVersionRevision || GitPathInput.Text != path) return;
            _versionPath = path;
            ShowGitVersion(version, number);
        }

        private async void OnDetectGit(object sender, RoutedEventArgs e) => await DetectGitAsync();

        private async Task DetectGitAsync()
        {
            if (!_integrationOnly || !IsVisible || _detectingGit || _saving || UseBundledGitChoice.IsChecked == true)
                return;

            _detectingGit = true;
            _gitVersionRevision++;
            var currentPath = GitPathInput.Text ?? string.Empty;
            GitPathInput.IsEnabled = DetectGitButton.IsEnabled = SavePreferencesButton.IsEnabled = false;
            try
            {
                var detected = await Task.Run(() =>
                {
                    var path = currentPath.Trim().Trim('"');
                    var (version, number) = ReadGitVersion(path);
                    if (number >= Models.GitVersions.MINIMAL &&
                        (!OperatingSystem.IsWindows() || !Native.GitExecutableLocator.IsManagedBundle(path)))
                        return (Path: path, Version: version, Number: number);

                    path = OperatingSystem.IsWindows() ? Native.GitExecutableLocator.FindInstalledWindowsGit() : Native.OS.FindGitExecutable();
                    (version, number) = ReadGitVersion(path);
                    return (Path: path ?? string.Empty, Version: version, Number: number);
                });
                if (!IsVisible) return;

                if (detected.Number >= Models.GitVersions.MINIMAL)
                {
                    _versionPath = detected.Path;
                    GitPathInput.Text = detected.Path;
                    ShowGitVersion(detected.Version, detected.Number);
                }
                else
                {
                    var (version, number) = await Task.Run(() => ReadGitVersion(currentPath));
                    if (!IsVisible) return;
                    _versionPath = currentPath;
                    ShowGitVersion(version, number);
                    if (string.IsNullOrEmpty(version) && UseBundledGitChoice.IsChecked != true)
                    {
                        _noSystemGitFound = true;
                        GitVersion = App.Text(OperatingSystem.IsWindows() ? "TreeGit.Git.UseBundled" : "Preferences.Git.Invalid");
                    }
                }
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                ShowGitVersion(string.Empty, null);
            }
            finally
            {
                _detectingGit = false;
                GitPathInput.IsEnabled = DetectGitButton.IsEnabled = SavePreferencesButton.IsEnabled = true;
            }
        }

        private static (string Text, Version Number) ReadGitVersion(string path)
        {
            Native.GitExecutableLocator.TryReadVersion(path?.Trim().Trim('"'), out var text, out var number);
            return (text ?? string.Empty, number);
        }

        private void ShowGitVersion(string version, Version number)
        {
            _noSystemGitFound = false;
            GitVersion = version;
            ShowGitVersionWarning = number != null && number < Models.GitVersions.MINIMAL;
        }

#if false // Handlers for the removed custom-action editor.
        private void OnAddCustomAction(object sender, RoutedEventArgs e)
        {
            var action = new Models.CustomAction() { Name = App.Text("TreeGit.Message.UnnamedAction") };
            ViewModels.Preferences.Instance.CustomActions.Add(action);
            SelectedCustomAction = action;

            e.Handled = true;
        }

        private async void SelectExecutableForCustomAction(object sender, RoutedEventArgs e)
        {
            var options = new FilePickerOpenOptions()
            {
                AllowMultiple = false,
                FileTypeFilter = [new(App.Text("TreeGit.Message.Executable")) { Patterns = ["*"] }]
            };

            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(options);
                if (selected is { Count: 1 } && sender is Button { DataContext: Models.CustomAction action })
                    action.Executable = selected[0].Path.LocalPath;
            }
            catch (Exception ex)
            {
                await new Alert().ShowAsync(this, App.Text("TreeGit.Message.ActionSelect", ex.Message), true);
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
        private Models.CustomAction _selectedCustomAction = null;
#endif

        private ViewModels.Preferences.EditSession _editSession;
        private Dictionary<string, string> _initialConfig = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _initialGitValues;
        private bool _configurationKnown;
        private bool _initialWindowFrame;
        private bool _saving;
        private bool _detectingGit;
        private readonly string _initialGitPath;
        private string _versionPath;
        private int _gitVersionRevision;
        private bool _noSystemGitFound;
        private bool _accepted;
        private string _gitVersion = string.Empty;
        private bool _showGitVersionWarning = false;
        private Models.GPGFormat _gpgFormat = Models.GPGFormat.Supported[0];
        private string _gpgExecutableFile = string.Empty;
    }
}
