using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    internal static class GuiBridge
    {
        private static readonly SemaphoreSlim _operations = new(1, 1);
        private static CancellationTokenSource _lifetime;

        public static bool TryBeginUpdate() => _operations.Wait(0);
        public static void EndUpdate() => _operations.Release();

        public static void Start()
        {
            _lifetime = new CancellationTokenSource();
            _ = ListenAsync(_lifetime.Token);
        }

        public static void Stop() => _lifetime?.Cancel();

        private static async Task ListenAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(McpServer.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    _ = HandleClientAsync(pipe, token);
                }
                catch (OperationCanceledException) { pipe?.Dispose(); break; }
                catch (Exception ex)
                {
                    pipe?.Dispose();
                    Native.OS.LogException(ex);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }

        private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
            await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (line == null || line.Length > 2 * 1024 * 1024)
                        return;
                    using var document = JsonDocument.Parse(line);
                    await _operations.WaitAsync(token).ConfigureAwait(false);
                    object result;
                    try
                    {
                        result = await DispatchAsync(document.RootElement, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { result = new { error = Redact(ex.Message) }; }
                    finally { _operations.Release(); }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (Exception ex) { Native.OS.LogException(ex); }
            }
        }

        private static async Task<object> DispatchAsync(JsonElement request, CancellationToken token)
        {
            var name = request.GetProperty("name").GetString();
            if (name == "ui_update_ping")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var window = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    if (window?.IsVisible != true) throw new InvalidOperationException("Startup is still in progress.");
                    return (object)new { version = Models.TreeGitRelease.Version, processId = Environment.ProcessId };
                });
            if (Native.TreeGitUpdateInstaller.IsApplying && name is not ("ui_state" or "ui_screenshot"))
                throw new InvalidOperationException("TreeGit is completing an update. Retry after it restarts.");
            var args = request.GetProperty("arguments");
            if (args.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("arguments must be an object.");
            if (name.StartsWith("git_", StringComparison.Ordinal) && !ViewModels.Preferences.Instance.IsGitConfigured())
                throw new InvalidOperationException(App.Text("NotConfigured"));
            if (name == "ui_state")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var launcher = App.GetLauncher();
                    var window = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    var tabs = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(b => b.Name == "LauncherTabsList");
                    return (object)new
                    {
                        application = "TreeGit",
                        processId = Environment.ProcessId,
                        git = new { path = Native.OS.GitExecutable, version = Native.OS.GitVersionString,
                            bundled = OperatingSystem.IsWindows() && Native.GitExecutableLocator.IsBundled(Native.OS.GitExecutable) },
                        terminal = new { path = Native.OS.ShellOrTerminal, arguments = Native.OS.ShellOrTerminalArgs },
#if INTEGRATION_TESTS
                        layoutProbe = UiLayoutProbe.Inspect(((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow),
#endif
                        mcp = "server",
                        defaultReadWrite = true,
                        tabDropdownOpen = window.GetVisualDescendants().OfType<Views.LauncherTabBar>().FirstOrDefault()?.TabListMenu?.IsOpen == true,
                        tabs = tabs?.GetVisualDescendants().OfType<ListBoxItem>().Select(item =>
                        {
                            var close = item.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("treegit_tab_close"));
                            var syncBadge = item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("treegit_tab_sync_badge"));
                            var a = close?.TranslatePoint(new Point(0, 0), item);
                            var b = close?.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), item);
                            return new { selected = item.IsSelected, text = item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray(),
                                syncBadgeVisible = syncBadge?.IsEffectivelyVisible == true, syncStatus = (syncBadge?.Child as TextBlock)?.Text,
                                closeVisible = close?.IsEffectivelyVisible == true && a.HasValue && b.HasValue && a.Value.X >= 0 && b.Value.X <= item.Bounds.Width + 1 };
                        }).ToArray(),
                        readOnlyFlag = "--read-only",
                        activePage = new { kind = launcher.ActivePage.Data.GetType().Name, pendingDialog = launcher.ActivePage.Popup?.GetType().Name, notifications = launcher.ActivePage.Notifications.Count },
                        dialogs = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows
                            .Where(w => w != ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow && w.IsVisible)
                            .Select(w =>
                            {
                                var area = w.Screens.ScreenFromWindow(w)?.WorkingArea;
                                var confirm = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ConfirmRepositorySettings");
                                var top = confirm?.TranslatePoint(new Point(0, 0), w);
                                var bottom = confirm?.TranslatePoint(new Point(confirm.Bounds.Width, confirm.Bounds.Height), w);
                                return new { title = w.Title, type = w.GetType().Name, topmost = w.Topmost, message = w is Views.Alert ? w.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "Message")?.Text : null, width = w.Bounds.Width, height = w.Bounds.Height, x = w.Position.X, y = w.Position.Y, scaling = w.RenderScaling,
                                    workArea = new { x = area?.X, y = area?.Y, width = area?.Width, height = area?.Height },
                                    confirmVisible = top.HasValue && bottom.HasValue && top.Value.Y >= 0 && bottom.Value.Y <= w.Bounds.Height && bottom.Value.X <= w.Bounds.Width };
                            }).ToArray(),
                        repositories = launcher.Pages.Where(p => p.Data is ViewModels.Repository).Select(p =>
                        {
                            var r = (ViewModels.Repository)p.Data;
                            var mainWindow = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                            var repositoryView = mainWindow.GetVisualDescendants().OfType<Views.Repository>().FirstOrDefault(v => v.DataContext == r);
                            var historyList = repositoryView?.GetVisualDescendants().OfType<Views.HistoriesCommitList>().FirstOrDefault();
                            return new
                            {
                                repo = r.FullPath,
                                active = p == launcher.ActivePage,
                                view = r.SelectedViewIndex,
                                sidebarVisible = repositoryView?.IsNormalSidebarVisible,
                                sidebarWidth = repositoryView?.SidebarWidth.Value,
                                branch = r.CurrentBranch?.Name,
                                changes = r.LocalChangesCount,
                                workingCopyState = r.WorkingCopy == null ? null : new { staging = r.WorkingCopy.IsStaging, unstaging = r.WorkingCopy.IsUnstaging,
                                    committing = r.WorkingCopy.IsCommitting, canChangeIndex = r.WorkingCopy.CanChangeIndex,
                                    staged = r.WorkingCopy.Staged.Take(256).Select(c => c.Path).ToArray(), unstaged = r.WorkingCopy.Unstaged.Take(256).Select(c => c.Path).ToArray(),
                                    selectedStaged = r.WorkingCopy.SelectedStaged?.Changes.Take(256).Select(c => c.Path).ToArray(), selectedUnstaged = r.WorkingCopy.SelectedUnstaged?.Changes.Take(256).Select(c => c.Path).ToArray() },
                                ahead = r.CurrentBranch?.Ahead.Count ?? 0,
                                behind = r.CurrentBranch?.Behind.Count ?? 0,
                                workingCopySelected = r.Histories?.IsWorkingCopySelected,
                                searchActive = r.Histories?.IsSearchingCommits,
                                searchQuerying = r.Histories?.SearchCommitContext.IsQuerying,
                                searchSelected = r.Histories?.SearchCommitContext.Selected?.SHA,
                                searchResults = r.Histories?.SearchCommitContext.Results?.Select(c => new { sha = c.SHA, subject = c.Subject, branch = c.SearchBranchName }).ToArray(),
                                detailView = r.Histories?.DetailContext?.GetType().Name,
                                detailChanges = r.Histories?.DetailContext is ViewModels.CommitDetail detail ? detail.Changes?.Take(256).Select(c => c.Path).ToArray()
                                    : r.Histories?.DetailContext is ViewModels.RevisionCompare comparison ? comparison.VisibleChanges?.Take(256).Select(c => c.Path).ToArray() : null,
                                stashes = new { count = r.StashesCount, expanded = r.IsStashGroupExpanded, selected = r.StashesPage.SelectedStash?.SHA,
                                    changes = r.StashesPage.Changes?.Select(c => c.Path).ToArray(), diff = r.StashesPage.DiffContext?.Title },
                                selectedCommits = r.Histories?.SelectedCommits.Select(c => c.SHA).ToArray(),
                                historySelectedCommits = historyList?.SelectedItems.OfType<Models.Commit>().Select(c => c.SHA).ToArray(),
                                historyVisible = historyList?.IsEffectivelyVisible,
                                detailCommit = r.GetSelectedCommitInHistory()?.SHA,
                                loadedBranches = r.Branches.Select(b => new { reference = b.FullName, head = b.Head, upstream = b.Upstream }).ToArray(),
                                localBranchTreeVisible = repositoryView?.LocalBranchTree.IsEffectivelyVisible,
                                remoteBranchTreeVisible = repositoryView?.RemoteBranchTree.IsEffectivelyVisible,
                                localBranchSelection = repositoryView?.LocalBranchTree.Rows.Where(n => n.IsSelected && n.Backend is Models.Branch).Select(n => ((Models.Branch)n.Backend).FullName).ToArray(),
                                remoteBranchSelection = repositoryView?.RemoteBranchTree.Rows.Where(n => n.IsSelected && n.Backend is Models.Branch).Select(n => ((Models.Branch)n.Backend).FullName).ToArray(),
                                graphBranch = r.Histories?.GraphBranchName,
                                graphMode = r.Histories?.BranchDisplayMode,
                                graphShowRemotes = r.Histories?.ShowRemoteBranches,
                                graphLoading = r.Histories?.IsLoading,
                                graphTotal = r.Histories?.Commits.Count,
                                graphCommits = r.Histories?.Commits.Take(256).Select((c, index) => new { sha = c.SHA, subject = c.Subject, localHead = c.IsLocalHead, tracking = c.Decorators.Where(d => !string.IsNullOrEmpty(d.TrackStatusDescription)).Select(d => new { branch = d.Name, status = d.TrackStatusDescription }).ToArray(), x = r.Histories.Graph?.Dots[index].Center.X, subjectLeft = c.LeftMargin, refs = c.Decorators.Select(d => d.Name).ToArray() }).ToArray(),
                                pendingDialog = p.Popup?.GetType().Name
                            };
                        }).ToArray(),
                    };
                });
            if (name == "ui_screenshot")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var lifetime = (IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                    var window = lifetime.MainWindow;
                    if (args.TryGetProperty("window", out var target) && target.GetString() == "active")
                        window = lifetime.Windows.LastOrDefault(w => w.IsVisible && w != lifetime.MainWindow) ?? window;
                    Visual surface = window;
                    if (args.TryGetProperty("window", out target) && target.GetString() == "active" &&
                        lifetime.MainWindow.GetVisualDescendants().OfType<Views.LauncherTabBar>().FirstOrDefault()?.TabListMenu is { IsOpen: true } tabMenu)
                        surface = tabMenu;
                    var width = Math.Max(1, (int)Math.Ceiling(surface.Bounds.Width));
                    var height = Math.Max(1, (int)Math.Ceiling(surface.Bounds.Height));
                    using var image = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                    image.Render(surface);
                    using var stream = new MemoryStream();
                    image.Save(stream);
                    return (object)new { png = Convert.ToBase64String(stream.ToArray()), width, height };
                });

            if (name == "ui_layout")
                return await Dispatcher.UIThread.InvokeAsync(() => UiLayoutController.Execute(args));

            if (name == "ui_tabs")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var launcher = App.GetLauncher();
                    var window = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    var tabBar = window.GetVisualDescendants().OfType<Views.LauncherTabBar>().FirstOrDefault() ?? throw new InvalidOperationException("Tab bar is not ready.");
                    var action = Text(args, "action");
                    if (action == "dropdown") tabBar.OpenTabList();
                    else if (action is "select" or "close" or "move")
                    {
                        if (!args.TryGetProperty("index", out var index) || index.GetInt32() < 0 || index.GetInt32() >= launcher.Pages.Count)
                            throw new ArgumentException("Tab index is out of range.");
                        var page = launcher.Pages[index.GetInt32()];
                        if (action == "select") tabBar.ActivateTab(page);
                        else if (action == "close") launcher.CloseTab(page);
                        else
                        {
                            if (!args.TryGetProperty("targetIndex", out var target) || target.GetInt32() < 0 || target.GetInt32() >= launcher.Pages.Count)
                                throw new ArgumentException("Destination index is out of range.");
                            launcher.MoveTab(page, launcher.Pages[target.GetInt32()]);
                        }
                    }
                    else if (action != "list") throw new ArgumentException("Unknown tab action.");
                    return (object)new { tabs = launcher.Pages.Select((page, index) => new { index, title = page.Node.IsRepository ? page.Node.Name : "TreeGit", repo = page.Node.IsRepository ? page.Node.Id : null, loaded = page.Data is ViewModels.Repository, error = page.Data is ViewModels.RepositoryLoading { Failed: true } pending ? pending.Message : null, selected = page == launcher.ActivePage }).ToArray() };
                });

            if (name == "ui_command")
            {
                var uiCommand = Text(args, "command");
                var globalResult = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var lifetime = (IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                    var dialog = lifetime.Windows.LastOrDefault(w => w.IsVisible && w != lifetime.MainWindow);
                    if (uiCommand == "close_dialog")
                    {
                        dialog?.Close();
                        return (object)new { closed = dialog != null };
                    }
                    if (uiCommand == "clear_notifications")
                    {
                        foreach (var page in App.GetLauncher().Pages)
                            page.ClearNotifications();
                        return (object)new { cleared = true };
                    }
                    if (uiCommand == "cancel_popup" && !args.TryGetProperty("repo", out _))
                    {
                        App.GetLauncher().ActivePage.CancelPopup();
                        return (object)new { cancelled = App.GetLauncher().ActivePage.Popup == null };
                    }
                    if (uiCommand is "repository_settings_advanced" or "repository_settings_remotes" && dialog is Views.RepositorySettings settings)
                    {
                        settings.SelectPage(uiCommand == "repository_settings_advanced");
                        return (object)new { selected = uiCommand };
                    }
                    if (uiCommand == "preferences" && dialog is Views.Preferences preferences && args.TryGetProperty("page", out var existingPage))
                    {
                        preferences.SelectPage(existingPage.GetString());
                        return (object)new { selected = existingPage.GetString() };
                    }
                    if (dialog != null)
                        throw new InvalidOperationException("Close the current dialog before opening another one.");
                    if (uiCommand == "new_tab")
                    {
                        App.GetLauncher().AddNewTab();
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "close_tab")
                    {
                        var launcher = App.GetLauncher();
                        var page = launcher.ActivePage;
                        var repository = page.Node.IsRepository;
                        launcher.CloseTab(page);
                        return (object)new { closed = !launcher.Pages.Contains(page) || repository && !page.Node.IsRepository };
                    }
                    if (uiCommand == "mcp_connection")
                    {
                        App.OpenMcpConnectionCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "preferences")
                    {
                        App.OpenPreferencesCommand.Execute(args.TryGetProperty("page", out var preferencePage) ? preferencePage.GetString() : null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "about")
                    {
                        App.OpenAboutCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "ssh_keys")
                    {
                        App.OpenSSHKeyHelperCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "check_updates")
                    {
                        App.CheckForUpdateCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "external_diff")
                    {
                        var diff = lifetime.MainWindow.GetVisualDescendants().OfType<Views.DiffView>()
                            .FirstOrDefault(v => v.IsEffectivelyVisible)?.DataContext as ViewModels.DiffContext;
                        if (diff == null) throw new InvalidOperationException("Select a changed file first.");
                        diff.OpenExternalMergeTool();
                        return (object)new { opened = uiCommand };
                    }
                    return null;
                });
                if (globalResult != null)
                    return globalResult;
            }

            if (name == "git_clone")
            {
                if (!request.TryGetProperty("allowWrite", out var writePermission) || !writePermission.GetBoolean())
                    throw new InvalidOperationException("Writes are disabled.");
                var url = Text(args, "url");
                if (!Models.Remote.IsValidURL(url) || url.StartsWith('-') || url.Any(char.IsControl))
                    throw new ArgumentException("Invalid repository URL.");
                if (Uri.TryCreate(url, UriKind.Absolute, out var remoteUri) && remoteUri.UserInfo.Contains(':'))
                    throw new ArgumentException("Use the authentication dialog instead of embedding a password in the URL.");
                var destination = Text(args, "destination");
                if (!Path.IsPathFullyQualified(destination))
                    throw new ArgumentException("destination must be an absolute path.");
                destination = Path.GetFullPath(destination).TrimEnd('\\', '/');
                var parent = Path.GetDirectoryName(destination);
                if (!Directory.Exists(parent) || Directory.Exists(destination) || File.Exists(destination))
                    throw new ArgumentException("Destination must not exist and its parent directory must already exist.");
                var key = args.TryGetProperty("sshKey", out var k) ? k.GetString() : string.Empty;
                if (!string.IsNullOrEmpty(key) && (!Path.IsPathFullyQualified(key) || !File.Exists(key)))
                    throw new ArgumentException("sshKey must name an existing private key file.");
                var cloneTask = await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var launcher = App.GetLauncher();
                    var lifetime = (IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                    if (launcher.ActivePage.Popup != null || lifetime.Windows.Any(w => w != lifetime.MainWindow && w.IsVisible))
                        throw new InvalidOperationException("Finish the current dialog before cloning.");
                    launcher.AddNewTab();
                    var page = launcher.ActivePage;
                    var clone = new ViewModels.Clone(page.Node.Id)
                    {
                        Remote = url,
                        ParentFolder = parent,
                        Local = Path.GetFileName(destination),
                        SSHKey = key,
                        UseCredentialManager = Flag(args, "credentialManager"),
                        InitAndUpdateSubmodules = !args.TryGetProperty("recursive", out var recursive) || recursive.GetBoolean(),
                    };
                    if (!clone.Check())
                        throw new ArgumentException("TreeGit rejected the clone configuration.");
                    page.Popup = clone;
                    await page.ProcessPopupAsync();
                    return (object)new { isSuccess = page.Popup == null && Directory.Exists(destination), repo = destination };
                });
                return cloneTask;
            }

            if (name == "ui_window")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var window = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    int? width = args.TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    int? height = args.TryGetProperty("height", out var h) ? h.GetInt32() : null;
                    var theme = args.TryGetProperty("theme", out var t) ? t.GetString() : null;
                    if (args.TryGetProperty("zoom", out var z))
                    {
                        var zoom = z.GetDouble();
                        if (!double.IsFinite(zoom) || zoom < 0.75 || zoom > 2.5) throw new ArgumentException("Zoom is out of range.");
                        ViewModels.Preferences.Instance.Zoom = zoom;
                    }
                    if (width.HasValue && (width < 1024 || width > 3840) || height.HasValue && (height < 640 || height > 2160))
                        throw new ArgumentException("Window size is out of range.");
                    if (theme != null && theme is not ("Light" or "Dark" or "Default" or "SourceTree" or "Blue"))
                        throw new ArgumentException("Unknown theme.");
                    if (width.HasValue)
                        window.Width = width.Value;
                    if (height.HasValue)
                        window.Height = height.Value;
                    if (theme != null)
                        ViewModels.Preferences.Instance.Theme = theme;
                    if (args.TryGetProperty("state", out var windowState))
                    {
                        if (windowState.GetString() == "toggle" && window is Views.Launcher launcher) launcher.ToggleMaximizeRestore();
                        else window.WindowState = windowState.GetString() switch { "normal" => WindowState.Normal, "maximized" => WindowState.Maximized, "minimized" => WindowState.Minimized, _ => throw new ArgumentException("Unknown window state.") };
                    }

                    if (args.TryGetProperty("collapseBranches", out var collapse))
                        ViewModels.Preferences.Instance.Layout.IsSidebarCollapsedInLocalChanges = collapse.GetBoolean();
                    return (object)new { width = window.Width, height = window.Height, theme = ViewModels.Preferences.Instance.Theme, effectiveTheme = window.ActualThemeVariant.Key.ToString(), state = window.WindowState.ToString() };
                });

            if (name == "ui_update")
            {
                if (!request.TryGetProperty("allowWrite", out var updatePermission) || !updatePermission.GetBoolean())
                    throw new InvalidOperationException("Updating TreeGit requires a read/write connection.");
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var action = Text(args, "action");
                    if (action == "check") { App.CheckForUpdateCommand.Execute(null); return (object)new { accepted = true }; }
                    var window = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows.OfType<Views.SelfUpdate>().FirstOrDefault(w => w.IsVisible);
                    if (window?.DataContext is not ViewModels.SelfUpdate model) throw new InvalidOperationException("Open the update window first.");
                    if (action is "download" or "apply")
                    {
                        if (action == "apply" && !model.CanRestart) throw new InvalidOperationException("The update is not ready to apply.");
                        if (action == "download" && !model.CanDownload) throw new InvalidOperationException("Check for an available release before downloading.");
                        Dispatcher.UIThread.Post(async () => await model.ActAsync(), DispatcherPriority.Background);
                    }
                    else if (action == "cancel") model.Dismiss();
                    else if (action != "state") throw new ArgumentException("Unknown update action.");
                    return (object)new { state = model.State, heading = model.Heading, message = model.Message, version = model.NewVersion, progress = model.Progress, progressText = model.ProgressText, canAct = model.CanAct, canDismiss = model.CanDismiss };
                });
            }

            if (name == "ui_fonts")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var preferences = ViewModels.Preferences.Instance;
                    var dialog = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows.OfType<Views.Preferences>().FirstOrDefault(w => w.IsVisible);
                    var action = Text(args, "action");
                    var kind = args.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (action is "list" or "set")
                    {
                        if (kind is not ("interface" or "code"))
                            throw new ArgumentException("A font kind is required.");
                        var options = kind == "interface" ? preferences.InterfaceFonts : preferences.CodeFonts;
                        if (action == "list") return (object)new { kind, fonts = options };
                        if (!args.TryGetProperty("font", out var fontValue) || fontValue.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("A font string is required; use empty for default.");
                        var font = fontValue.GetString();
                        var selected = options.FirstOrDefault(option => option.Family.Equals(font, StringComparison.OrdinalIgnoreCase));
                        if (selected == null) throw new ArgumentException("Choose a listed font, or an empty font for default.");
                        var picker = dialog?.FindControl<ComboBox>(kind == "interface" ? "InterfaceFontPicker" : "CodeFontPicker");
                        if (picker != null) picker.SelectedValue = selected.Family;
                        else if (kind == "interface") preferences.DefaultFontFamily = selected.Family;
                        else preferences.MonospaceFontFamily = selected.Family;
                        preferences.Save();
                    }
                    return (object)new
                    {
                        interfaceFont = preferences.DefaultFontFamily, codeFont = preferences.MonospaceFontFamily,
                        renderedInterfaceFont = (App.Current.FindResource("Fonts.Default") as Avalonia.Media.FontFamily)?.ToString(),
                        renderedCodeFont = (App.Current.FindResource("Fonts.Monospace") as Avalonia.Media.FontFamily)?.ToString(),
                        controls = dialog == null ? null : new
                        {
                            interfaceValue = dialog.FindControl<ComboBox>("InterfaceFontPicker")?.SelectedValue,
                            codeValue = dialog.FindControl<ComboBox>("CodeFontPicker")?.SelectedValue,
                        },
                    };
                });

            var path = Path.GetFullPath(Text(args, "repo")).TrimEnd('\\', '/');
            if (name == "ui_open_repository")
            {
                if (!ViewModels.Preferences.Instance.IsGitConfigured())
                    throw new InvalidOperationException(App.Text("NotConfigured"));
                if (!Directory.Exists(path))
                    throw new ArgumentException("Repository directory does not exist.");
                // Never turn an arbitrary directory into a Git repository as a side effect of opening it.
                var check = await new Query(path, "rev-parse --absolute-git-dir").RunAsync(retrySilentFailure: true).ConfigureAwait(false);
                if (!check.IsSuccess)
                {
                    if (check.IsSilentFailure)
                        throw new InvalidOperationException("Git could not read this directory. Try opening the repository again.");
                    throw new ArgumentException("This directory is not a trusted Git repository: " + Redact(check.StdErr));
                }
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var launcher = App.GetLauncher();
                    launcher.TryOpenRepositoryFromPath(path);
                    if (launcher.ActivePage.Data is not ViewModels.Repository opened ||
                        !string.Equals(Path.GetFullPath(opened.GitDir).TrimEnd('\\', '/'),
                            Path.GetFullPath(check.StdOut.Trim()).TrimEnd('\\', '/'),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        throw new InvalidOperationException("The repository could not be opened. Try opening it again.");
                    return (object)new { opened = true, repo = path };
                });
            }

            var repo = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var launcher = App.GetLauncher();
                var page = launcher.Pages.FirstOrDefault(p => p.Data is ViewModels.Repository r &&
                    string.Equals(Path.GetFullPath(r.FullPath).TrimEnd('\\', '/'), path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (page?.Data is not ViewModels.Repository found)
                    throw new ArgumentException("Open this repository with ui_open_repository first.");
                launcher.ActivePage = page;
                return found;
            });

            if (name == "ui_stashes")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (repo.IsBare) throw new ArgumentException("Bare repositories have no stash view.");
                    var action = args.TryGetProperty("action", out var a) ? a.GetString() : "list";
                    if (action == "expand") repo.IsStashGroupExpanded = true;
                    else if (action == "collapse") repo.IsStashGroupExpanded = false;
                    else if (action == "select")
                    {
                        var nameOrSha = Text(args, "stash");
                        var stash = repo.StashesPage.VisibleStashes.Find(s => s.Name == nameOrSha || s.SHA == nameOrSha)
                            ?? throw new ArgumentException("Stash not found in the visible list.");
                        repo.SelectedViewIndex = 2;
                        repo.StashesPage.SelectedStash = stash;
                    }
                    else if (action != "list") throw new ArgumentException("Unknown stash view action.");
                    if (args.TryGetProperty("filter", out var filter)) repo.StashesPage.SearchFilter = filter.GetString();
                    return (object)new { expanded = repo.IsStashGroupExpanded, selected = repo.StashesPage.SelectedStash?.SHA,
                        stashes = repo.StashesPage.VisibleStashes.Select(s => new { name = s.Name, sha = s.SHA, subject = s.Subject }).ToArray(),
                        changes = repo.StashesPage.Changes?.Select(c => c.Path).ToArray(), diff = repo.StashesPage.DiffContext?.Title };
                });

            if (name == "ui_diff")
            {
                var action = args.TryGetProperty("action", out var a) ? a.GetString() : "state";
                if (action == "select")
                    return await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var file = Text(args, "file");
                        var source = args.TryGetProperty("source", out var s) ? s.GetString() : "unstaged";
                        if (source == "history")
                        {
                            if (repo.Histories.DetailContext is ViewModels.CommitDetail detail)
                            {
                                var change = detail.Changes?.FirstOrDefault(c => c.Path == file) ?? throw new ArgumentException("File is not in the selected commit.");
                                detail.ActiveTabIndex = 1;
                                if (!IsSelected(detail.ChangeSelection, change)) detail.ChangeSelection = new ViewModels.ChangeSelection(new[] { change });
                            }
                            else if (repo.Histories.DetailContext is ViewModels.RevisionCompare comparison)
                            {
                                var change = comparison.VisibleChanges?.FirstOrDefault(c => c.Path == file) ?? throw new ArgumentException("File is not in the selected comparison.");
                                if (!IsSelected(comparison.ChangeSelection, change)) comparison.ChangeSelection = new ViewModels.ChangeSelection(new[] { change });
                            }
                            else throw new InvalidOperationException("Select a commit or two commits to compare first.");
                        }
                        else if (source == "stash")
                        {
                            var stash = repo.StashesPage;
                            var change = stash.Changes?.FirstOrDefault(c => c.Path == file) ?? throw new ArgumentException("File is not in the selected stash.");
                            repo.SelectedViewIndex = 2;
                            if (!IsSelected(stash.ChangeSelection, change)) stash.ChangeSelection = new ViewModels.ChangeSelection(new[] { change });
                        }
                        else
                        {
                            if (source is not ("staged" or "unstaged")) throw new ArgumentException("Unknown diff source.");
                            repo.ShowWorkingCopyOverview();
                            var copy = repo.WorkingCopy;
                            var change = (source == "staged" ? copy.Staged : copy.Unstaged)?.FirstOrDefault(c => c.Path == file)
                                ?? throw new ArgumentException("File is not in the selected change list.");
                            if (source == "staged")
                            {
                                if (!IsSelected(copy.SelectedStaged, change)) copy.SelectedStaged = new ViewModels.ChangeSelection(new[] { change });
                            }
                            else if (!IsSelected(copy.SelectedUnstaged, change)) copy.SelectedUnstaged = new ViewModels.ChangeSelection(new[] { change });
                        }
                        return (object)new { selected = file, source };
                    });
                var view = await Dispatcher.UIThread.InvokeAsync(() =>
                    ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows.Where(w => w.IsVisible)
                    .Reverse().SelectMany(w => w.GetVisualDescendants().OfType<Views.BinaryDiffView>()).FirstOrDefault(v => v.IsEffectivelyVisible &&
                        v.DataContext is Models.BinaryDiff d && string.Equals(Path.GetFullPath(d.Repository).TrimEnd('\\', '/'), path,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
                if (view == null) return new { status = "not_binary" };
                if (action == "retry")
                    await Dispatcher.UIThread.InvokeAsync(view.Retry);
                else if (action is "first" or "last" or "next" or "previous")
                {
                    await Dispatcher.UIThread.InvokeAsync(() => view.NavigateAsync(action));
                }
                else if (action == "goto")
                    await Dispatcher.UIThread.InvokeAsync(() => view.GoTo(args.GetProperty("offset").GetInt64()));
                else if (action == "window")
                    await Dispatcher.UIThread.InvokeAsync(view.OpenComparisonWindow);
                else if (action != "state") throw new ArgumentException("Unknown diff action.");
                return await Dispatcher.UIThread.InvokeAsync(view.Inspect);
            }

            if (name == "ui_working_copy")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (repo.IsBare) throw new ArgumentException("Bare repositories have no working copy.");
                    repo.ShowWorkingCopyOverview();
                    if (args.TryGetProperty("filter", out var filter)) repo.WorkingCopy.Filter = filter.GetString() ?? string.Empty;
                    return (object)new { selected = "uncommitted_changes", repo = path, filter = repo.WorkingCopy.Filter };
                });

            if (name == "ui_search")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    repo.Histories.IsSearchingCommits = true;
                    var search = repo.Histories.SearchCommitContext;
                    if (args.TryGetProperty("method", out var method))
                        search.Method = method.GetString() switch { "sha" => 0, "author" => 1, "message" => 2, "path" => 3, "content" => 4, _ => throw new ArgumentException("Unknown search method.") };
                    if (args.TryGetProperty("currentBranchOnly", out var current)) search.OnlySearchCurrentBranch = current.GetBoolean();
                    if (args.TryGetProperty("query", out var query)) { search.Filter = query.GetString() ?? ""; search.StartSearch(); }
                    if (args.TryGetProperty("select", out var selected))
                        search.Selected = search.Results?.FirstOrDefault(c => c.SHA.StartsWith(selected.GetString() ?? "", StringComparison.Ordinal)) ?? throw new ArgumentException("No matching search result.");
                    if (Flag(args, "open"))
                        search.OpenInHistory(search.Selected ?? throw new ArgumentException("Select an existing search result before opening it in history."));
                    return (object)new { active = repo.Histories.IsSearchingCommits, querying = search.IsQuerying };
                });

            if (name == "ui_history_columns")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    repo.SelectedViewIndex = 0;
                    var owner = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    var histories = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(owner).OfType<Views.Histories>().FirstOrDefault(v => v.DataContext == repo.Histories);
                    if (histories == null) throw new InvalidOperationException("History view is not ready; retry after opening the repository.");
                    var widths = new Dictionary<string, double>();
                    foreach (var property in args.EnumerateObject())
                        if (property.Name is not ("repo" or "reset")) widths[property.Name] = property.Value.GetDouble();
                    return (object)histories.ConfigureColumns(widths, Flag(args, "reset"));
                });

            if (name == "ui_graph")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Models.Branch branch = null;
                    if (args.TryGetProperty("branch", out var branchValue))
                    {
                        var reference = branchValue.GetString();
                        branch = reference == "HEAD" ? repo.CurrentBranch : repo.Branches.Find(b => b.FullName == reference || b.FriendlyName == reference);
                        if (branch == null) throw new ArgumentException("Branch has not been loaded or does not exist.");
                    }
                    if (args.TryGetProperty("showRemotes", out var showRemotes)) repo.Histories.ShowRemoteBranches = showRemotes.GetBoolean();
                    if (args.TryGetProperty("mode", out var mode))
                        repo.Histories.BranchDisplayMode = mode.GetString() switch { "all" => 0, "current" or "only" or "merged" => 1, _ => throw new ArgumentException("Invalid graph mode.") };
                    if (branch != null)
                    {
                        var owner = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                        var repositoryView = owner.GetVisualDescendants().OfType<Views.Repository>().FirstOrDefault(v => v.DataContext == repo);
                        var tree = branch.IsLocal ? repositoryView?.LocalBranchTree : repositoryView?.RemoteBranchTree;
                        tree?.Select(branch);
                        repo.Histories.NavigateToBranch(branch);
                    }
                    return (object)new { branch = repo.Histories.GraphBranchName, mode = repo.Histories.BranchDisplayMode, loading = repo.Histories.IsLoading };
                });

            if (name == "ui_command")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var command = Text(args, "command");
                    var page = App.GetLauncher().ActivePage;
                    if (command == "cancel_popup")
                    {
                        page.CancelPopup();
                        return (object)new { cancelled = page.Popup == null };
                    }
                    if (page.Popup != null)
                        throw new InvalidOperationException("Finish or cancel the current repository dialog first.");
                    var owner = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                    switch (command)
                    {
                        case "repository_settings":
                            _ = ((Views.Launcher)owner).OpenRepositorySettingsAsync(repo);
                            break;
                        case "credentials":
                            _ = new Views.RepositoryCredentials(repo).ShowDialog(owner);
                            break;
                        case "file_history":
                            Views.ControlExtensions.ShowWindow(owner, new ViewModels.FileHistories(repo.FullPath, Text(args, "file")));
                            break;
                        case "directory_history":
                            Views.ControlExtensions.ShowWindow(owner, new ViewModels.DirHistories(repo, Text(args, "file")));
                            break;
                        case "compare_branches":
                            var based = repo.Branches.Find(b => b.FullName == Text(args, "base") || b.FriendlyName == Text(args, "base"))
                                ?? throw new ArgumentException("Base branch does not exist.");
                            var to = repo.Branches.Find(b => b.FullName == Text(args, "to") || b.FriendlyName == Text(args, "to"))
                                ?? throw new ArgumentException("Target branch does not exist.");
                            Views.ControlExtensions.ShowWindow(owner, new ViewModels.Compare(repo, based, to));
                            break;
                        case "compare_commits":
                            var baseCommit = repo.Histories.Commits.Find(c => c.SHA == Text(args, "base")) ?? throw new ArgumentException("Base commit is not loaded in history.");
                            var toCommit = repo.Histories.Commits.Find(c => c.SHA == Text(args, "to")) ?? throw new ArgumentException("Target commit is not loaded in history.");
                            if (baseCommit == toCommit) throw new ArgumentException("Choose two different commits.");
                            repo.ShowHistoryPage();
                            repo.Histories.SelectedCommits = [toCommit, baseCommit];
                            break;
                        case "submodule_compare":
                            var submoduleDiff = owner.GetVisualDescendants().OfType<Views.DiffView>()
                                .Where(v => v.IsEffectivelyVisible).Select(v => (v.DataContext as ViewModels.DiffContext)?.Content)
                                .OfType<Models.SubmoduleDiff>().FirstOrDefault() ?? throw new InvalidOperationException("Select a loaded submodule change first.");
                            Views.ControlExtensions.ShowWindow(owner, new ViewModels.SubmoduleRevisionCompare(submoduleDiff));
                            break;
                        case "interactive_rebase":
                            var rebaseOn = repo.Histories.Commits.Find(c => c.SHA == Text(args, "base")) ?? throw new ArgumentException("Base commit is not loaded in history.");
                            _ = Views.ControlExtensions.ShowDialogAsync(owner, new ViewModels.InteractiveRebase(repo, rebaseOn));
                            break;
                        case "commit_info":
                        case "commit_changes":
                        case "commit_files":
                            if (repo.Histories.DetailContext is not ViewModels.CommitDetail commitDetail)
                                throw new InvalidOperationException("Select a commit first.");
                            commitDetail.ActiveTabIndex = command == "commit_info" ? 0 : command == "commit_changes" ? 1 : 2;
                            break;
                        case "git_logs":
                            _ = Views.ControlExtensions.ShowDialogAsync(owner, new ViewModels.ViewLogs(repo));
                            break;
                        case "create_branch":
                            repo.CreateNewBranch();
                            break;
                        case "create_tag":
                            repo.CreateNewTag();
                            break;
                        case "reset_dialog":
                            var target = repo.Histories.Commits.Find(c => c.IsCurrentHead);
                            if (target == null || repo.CurrentBranch == null)
                                throw new InvalidOperationException("Wait for the current branch history to load before opening reset.");
                            repo.ShowPopup(new ViewModels.Reset(repo, repo.CurrentBranch, target));
                            break;
                        case "fetch_dialog":
                            _ = repo.FetchAsync(false);
                            break;
                        case "pull_dialog":
                            _ = repo.PullAsync(false);
                            break;
                        case "push_dialog":
                            _ = repo.PushAsync(false);
                            break;
                        case "stash_dialog":
                            _ = repo.StashAllAsync(false);
                            break;
                        case "stash_apply_dialog":
                        case "stash_drop_dialog":
                        case "stash_branch_dialog":
                            var stash = repo.StashesPage.SelectedStash ?? throw new ArgumentException("Select a stash first.");
                            if (command == "stash_apply_dialog") repo.StashesPage.Apply(stash);
                            else if (command == "stash_drop_dialog") repo.StashesPage.Drop(stash);
                            else repo.StashesPage.CheckoutBranch(stash);
                            break;
                        case "apply_patch":
                            repo.ApplyPatch();
                            break;
                        case "show_reflog":
                            repo.ShowReflog();
                            break;
                        default:
                            throw new ArgumentException("Unknown GUI command.");
                    }
                    return (object)new { opened = command, repo = path, resetModes = command == "reset_dialog" ? Models.ResetMode.Supported.Select(m => new { name = m.DisplayName, description = m.DisplayDescription, argument = m.Arg }).ToArray() : null };
                });

            if (name == "ui_select_view")
            {
                var view = Text(args, "view");
                var index = view switch { "history" or "search" => 0, "working_copy" => 1, "stashes" => 2, _ => throw new ArgumentException("Unknown view.") };
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (repo.IsBare && index != 0)
                        throw new ArgumentException("Bare repositories have no working copy or stash view.");
                    if (view == "search") repo.Histories.IsSearchingCommits = true;
                    else if (view == "history") repo.ShowHistoryPage();
                    else repo.SelectedViewIndex = index;
                });
                return new { selected = index, repo = path };
            }
            if (name == "ui_select_commit")
            {
                var revision = Revision(args);
                var resolved = await new Query(path, Join("rev-parse", "--verify", revision + "^{commit}")).RunAsync().ConfigureAwait(false);
                if (!resolved.IsSuccess)
                    throw new ArgumentException(Redact(resolved.StdErr));
                var sha = resolved.StdOut.Trim();
                await Dispatcher.UIThread.InvokeAsync(() => { repo.SelectedViewIndex = 0; repo.NavigateToCommit(sha); });
                return new { selected = sha, repo = path };
            }
            if (name == "ui_refresh")
            {
                await Dispatcher.UIThread.InvokeAsync(repo.RefreshAll);
                return new { refreshed = true, repo = path };
            }

            string command;
            var write = name is "git_action" or "git_run";
            var activity = name == "git_action" ? Text(args, "action") : name;
            string remote = null;
            Models.Remote selectedRemote = null;
            string tempMessage = null;
            var preserveSubmoduleArguments = false;
            var credentialScope = path;
            bool? useCredentialManager = null;
            var allowAuthenticationRetry = false;
            var requireAuthenticationTransportExitCode = false;
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            try
            {
                switch (name)
                {
                    case "git_status":
                        command = "--no-optional-locks status --porcelain=v2 --branch --untracked-files=all";
                        break;
                    case "git_log":
                        var count = args.TryGetProperty("count", out var n) ? n.GetInt32() : 50;
                        if (count < 1 || count > 500)
                            throw new ArgumentException("count must be between 1 and 500.");
                        command = $"log -{count} --graph --decorate=short --date=iso-strict --format=fuller" + (Flag(args, "all") ? " --all" : "");
                        break;
                    case "git_diff":
                        command = "--literal-pathspecs diff --no-ext-diff --no-textconv" + (Flag(args, "staged") ? " --cached" : "") + " -- " + Paths(args, false);
                        break;
                    case "git_refs":
                        command = "for-each-ref --format=%(refname:short)%09%(objectname)%09%(upstream:short) refs/heads refs/remotes refs/tags";
                        break;
                    case "git_show":
                        command = Join("show", "--no-ext-diff", "--no-textconv", "--format=fuller", Revision(args), "--");
                        break;
                    case "git_run":
                        if (!request.TryGetProperty("allowWrite", out var permission) || !permission.GetBoolean())
                            throw new InvalidOperationException("Writes are disabled.");
                        var arguments = args.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).ToArray();
                        if (arguments.Length == 0 || arguments.Length > 4096 || string.IsNullOrWhiteSpace(arguments[0]) || arguments.Any(a => a == null || a.Contains('\0')))
                            throw new ArgumentException("arguments must contain a Git command and no NUL characters.");
                        var commandIndex = GitCommandIndex(arguments);
                        var subcommand = commandIndex < arguments.Length ? arguments[commandIndex] : string.Empty;
                        requireAuthenticationTransportExitCode = subcommand is "push" or "pull";
                        allowAuthenticationRetry = subcommand is "clone" or "fetch" or "pull" or "push" or "ls-remote" ||
                            commandIndex + 1 < arguments.Length &&
                            ((subcommand == "submodule" && arguments[commandIndex + 1] is "add" or "update") ||
                             (subcommand == "lfs" && arguments[commandIndex + 1] is "fetch" or "pull" or "push" or "lock" or "unlock"));
                        preserveSubmoduleArguments = subcommand == "submodule";
                        if (preserveSubmoduleArguments && OperatingSystem.IsWindows())
                            arguments = arguments.Select(a => Path.IsPathRooted(a) ? a.Replace('\\', '/') : a).ToArray();
                        command = string.Join(' ', arguments.Select(Quote));
                        var globalOptions = string.Join(' ', arguments.Take(commandIndex).Select(Quote));
                        if (arguments.Take(commandIndex).Any(a => a.StartsWith("-C", StringComparison.Ordinal) ||
                            a.StartsWith("--git-dir", StringComparison.Ordinal) || a.StartsWith("--work-tree", StringComparison.Ordinal)))
                        {
                            var scope = await new Query(path, globalOptions + " rev-parse --show-toplevel").RunAsync(false).ConfigureAwait(false);
                            if (!scope.IsSuccess) scope = await new Query(path, globalOptions + " rev-parse --absolute-git-dir").RunAsync(false).ConfigureAwait(false);
                            if (scope.IsSuccess) credentialScope = scope.StdOut.Trim().Replace('\\', '/');
                        }
                        Dictionary<string, string> invocationConfig = null;
                        if (commandIndex > 0)
                        {
                            invocationConfig = await new Commands.Config(path).ReadAllAsync(globalOptions).ConfigureAwait(false);
                            useCredentialManager = invocationConfig.TryGetValue("sourcegit.credentialmanager", out var systemCredentials) &&
                                systemCredentials.Equals("true", StringComparison.OrdinalIgnoreCase);
                        }
                        if (subcommand is "fetch" or "pull" or "push" or "ls-remote")
                        {
                            var configuredRemotes = invocationConfig == null ? await new Commands.QueryRemotes(path).GetResultAsync().ConfigureAwait(false) : Commands.QueryRemotes.Parse(invocationConfig);
                            selectedRemote = configuredRemotes.FirstOrDefault(r => arguments.Skip(commandIndex + 1).Contains(r.Name)) ??
                                configuredRemotes.FirstOrDefault(r => string.Equals(credentialScope, path, pathComparison) && r.Name == repo.Settings.DefaultRemote) ??
                                configuredRemotes.FirstOrDefault(r => r.Name == "origin");
                        }
                        break;
                    case "git_action":
                        if (!request.TryGetProperty("allowWrite", out var allow) || !allow.GetBoolean())
                            throw new InvalidOperationException("Writes are disabled.");
                        var action = Text(args, "action");
                        var recovery = action is "stage" or "unstage" or "commit" || action.EndsWith("_abort", StringComparison.Ordinal) || action.EndsWith("_continue", StringComparison.Ordinal);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var inProgress = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.Any(f => File.Exists(Path.Combine(repo.GitDir, f))) ||
                                Directory.Exists(Path.Combine(repo.GitDir, "rebase-merge")) || Directory.Exists(Path.Combine(repo.GitDir, "rebase-apply"));
                            if (App.GetLauncher().ActivePage.Popup != null || (inProgress && !recovery))
                                throw new InvalidOperationException("Finish the current TreeGit dialog/operation before starting another Git action.");
                        });
                        switch (action)
                        {
                            case "stage":
                                command = "--literal-pathspecs add -- " + Paths(args, true);
                                break;
                            case "unstage":
                                var hasHead = await new Query(path, "rev-parse --verify HEAD").RunAsync().ConfigureAwait(false);
                                command = (hasHead.IsSuccess ? "--literal-pathspecs restore --staged -- " : "--literal-pathspecs rm --cached -r -- ") + Paths(args, true);
                                break;
                            case "commit":
                                tempMessage = Path.GetTempFileName();
                                await File.WriteAllTextAsync(tempMessage, Text(args, "message"), new UTF8Encoding(false), token).ConfigureAwait(false);
                                command = Join("commit", "--file", tempMessage);
                                break;
                            case "checkout":
                                command = Join("checkout", Revision(args), "--");
                                break;
                            case "create_branch":
                                command = Join("switch", "-c", Revision(args));
                                break;
                            case "fetch":
                            case "pull":
                            case "push":
                                allowAuthenticationRetry = true;
                                requireAuthenticationTransportExitCode = action is "push" or "pull";
                                remote = args.TryGetProperty("remote", out var r) ? r.GetString() : "origin";
                                // Opening a tab starts an asynchronous refresh. Query configuration
                                // directly so a fetch immediately after open cannot see a stale VM list.
                                var remotes = await new Commands.QueryRemotes(path).GetResultAsync().ConfigureAwait(false);
                                selectedRemote = remotes.FirstOrDefault(r => r.Name == remote);
                                if (selectedRemote == null || remote.StartsWith('-'))
                                    throw new ArgumentException("remote must name a configured remote (default origin).");
                                command = action == "fetch" ? Join("fetch", "--", remote) : action == "pull" ? Join("pull", "--ff-only", "--", remote) : Join("push", "--", remote);
                                break;
                            case "stash":
                                command = "stash push" + (args.TryGetProperty("message", out var msg) ? " -m " + Quote(msg.GetString()) : "");
                                break;
                            case "stash_pop":
                                command = "stash pop";
                                break;
                            case "merge":
                                command = Join("merge", "--no-edit", "--", Revision(args));
                                break;
                            case "rebase":
                                command = Join("rebase", "--", Revision(args));
                                break;
                            case "cherry_pick":
                                command = Join("cherry-pick", "--", Revision(args));
                                break;
                            case "revert":
                                command = Join("revert", "--no-edit", "--", Revision(args));
                                break;
                            case "merge_abort":
                                command = "merge --abort";
                                break;
                            case "rebase_abort":
                                command = "rebase --abort";
                                break;
                            case "cherry_pick_abort":
                                command = "cherry-pick --abort";
                                break;
                            case "revert_abort":
                                command = "revert --abort";
                                break;
                            case "merge_continue":
                                command = "merge --continue";
                                break;
                            case "rebase_continue":
                                command = "rebase --continue";
                                break;
                            case "cherry_pick_continue":
                                command = "cherry-pick --continue";
                                break;
                            case "revert_continue":
                                command = "revert --continue";
                                break;
                            default:
                                throw new ArgumentException("Unknown Git action.");
                        }
                        break;
                    default:
                        throw new ArgumentException("Unknown tool.");
                }
                var query = new Query(path, command) { PreserveSubmoduleArguments = preserveSubmoduleArguments, CredentialScope = credentialScope, UseCredentialManager = useCredentialManager };
                if (selectedRemote != null)
                    query.SSHKey = selectedRemote.PrivateSSHKey;
                ViewModels.CommandLog log = null;
                if (write)
                {
                    log = await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        repo.McpActivity = "MCP · " + activity + " …";
                        return repo.CreateLog("MCP · " + activity);
                    });
                    log.AppendLine("$ git " + Redact(command));
                }
                Commands.Command.Result result;
                try
                { result = await query.RunAsync(allowAuthenticationRetry, requireAuthenticationTransportExitCode: requireAuthenticationTransportExitCode).ConfigureAwait(false); }
                catch (Exception ex) { result = Commands.Command.Result.Failed(ex.Message); }
                if (write)
                {
                    log.AppendLine(Redact(result.StdOut));
                    log.AppendLine(Redact(result.StdErr));
                    log.Complete();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        repo.RefreshAll();
                        if (!string.Equals(credentialScope, path, pathComparison))
                            foreach (var page in App.GetLauncher().Pages)
                                if (page.Data is ViewModels.Repository affected && string.Equals(affected.FullPath, credentialScope, pathComparison)) affected.RefreshAll();
                        repo.McpActivity = "MCP · " + activity + (result.IsSuccess ? " ✓" : " ✕ · 查看命令日志");
                    });
                }
                var stdout = Redact(result.StdOut);
                var stderr = Redact(result.StdErr);
                const int limit = 1024 * 1024;
                return new { isSuccess = result.IsSuccess, exitCode = result.ExitCode, stdout = stdout[..Math.Min(stdout.Length, limit)], stderr = stderr[..Math.Min(stderr.Length, limit)], truncated = stdout.Length > limit || stderr.Length > limit };
            }
            finally
            {
                if (tempMessage != null)
                    File.Delete(tempMessage);
            }
        }

        private static int GitCommandIndex(string[] arguments)
        {
            var index = 0;
            while (index < arguments.Length && arguments[index].StartsWith('-'))
            {
                var option = arguments[index++];
                if (option is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix" or "--config-env" or "--attr-source")
                    index = Math.Min(index + 1, arguments.Length);
            }
            return index;
        }

        private static bool IsSelected(ViewModels.ChangeSelection selection, Models.Change change) =>
            selection is { Count: 1, HasFolder: false } && ReferenceEquals(selection.Changes[0], change);

        private static string Text(JsonElement args, string key)
        {
            if (!args.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException(key + " must be a nonempty string.");
            var text = value.GetString();
            if (text.Contains('\0'))
                throw new ArgumentException(key + " contains a NUL character.");
            return text;
        }

        private static bool Flag(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.GetBoolean();
        private static string Revision(JsonElement args)
        {
            var revision = Text(args, "revision");
            if (revision.StartsWith('-') || revision.Any(char.IsControl))
                throw new ArgumentException("revision cannot be an option or contain control characters.");
            return revision;
        }

        private static string Paths(JsonElement args, bool required)
        {
            if (!args.TryGetProperty("paths", out var array))
            {
                if (required)
                    throw new ArgumentException("Explicit paths are required.");
                return string.Empty;
            }
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 1024 || (required && array.GetArrayLength() == 0))
                throw new ArgumentException("paths must contain 1 to 1024 relative paths.");
            return string.Join(' ', array.EnumerateArray().Select(p =>
            {
                var path = p.GetString();
                if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Any(char.IsControl) || path.Replace('\\', '/').Split('/').Contains(".."))
                    throw new ArgumentException("Paths must stay inside the repository.");
                return Quote(path);
            }));
        }

        // Windows command line quoting, including embedded quotes and trailing backslashes.
        internal static string Quote(string text)
        {
            if (text == null || text.Contains('\0'))
                throw new ArgumentException("Invalid argument.");
            var b = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in text)
            {
                if (c == '\\')
                { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                b.Append(c);
                slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static string Join(params string[] args) => string.Join(' ', args.Select(Quote));
        private static string Redact(string value) => Regex.Replace(value ?? string.Empty, @"(https?://)[^\s/@]+(?::[^\s/@]*)?@", "$1[redacted]@", RegexOptions.IgnoreCase);

        private sealed class Query : Commands.Command
        {
            public Query(string repo, string args)
            {
                WorkingDirectory = repo;
                Context = repo;
                Args = args;
                Editor = EditorType.None;
                RaiseError = false;
            }

            public async Task<Result> RunAsync(bool allowAuthenticationRetry = false, bool retrySilentFailure = false, bool requireAuthenticationTransportExitCode = false)
            {
                IsAuthenticationRetry = false;
                while (true)
                {
                    using var process = new System.Diagnostics.Process { StartInfo = CreateGitStartInfo(true) };
#if INTEGRATION_TESTS
                    var trace = Environment.GetEnvironmentVariable("TREEGIT_TEST_QUERY_TRACE");
                    if (!string.IsNullOrEmpty(trace))
                        File.WriteAllText(trace, JsonSerializer.Serialize(new {
                            scope = process.StartInfo.Environment["SOURCEGIT_CREDENTIAL_SCOPE"],
                            ssh = process.StartInfo.Environment.TryGetValue("GIT_SSH_COMMAND", out var ssh) ? ssh : string.Empty,
                            ownCredentialHelper = process.StartInfo.Arguments.Contains("--credential-helper", StringComparison.Ordinal),
                        }));
#endif
                    Native.ProcessLauncher.Start(process);
                    var stdout = ReadCappedAsync(process.StandardOutput);
                    var stderr = ReadCappedAsync(process.StandardError);
                    await Task.WhenAll(stdout, stderr, process.WaitForExitAsync()).ConfigureAwait(false);
                    var result = new Result { IsSuccess = process.ExitCode == 0, ExitCode = process.ExitCode, StdOut = stdout.Result, StdErr = stderr.Result };
                    if (retrySilentFailure && result.IsSilentFailure)
                    {
                        retrySilentFailure = false;
                        await Task.Delay(100).ConfigureAwait(false);
                        continue;
                    }
                    if (!result.IsSuccess && allowAuthenticationRetry && (!requireAuthenticationTransportExitCode || result.ExitCode == 128) &&
                        OperatingSystem.IsWindows() && Native.RepositoryCredentials.IsAuthenticationFailure(result.StdErr))
                    {
                        IsAuthenticationRetry = true;
                        continue;
                    }
                    return result;
                }
            }

            private static async Task<string> ReadCappedAsync(StreamReader reader)
            {
                const int capacity = 1024 * 1024 + 1;
                var result = new StringBuilder();
                var buffer = new char[4096];
                int count;
                // Drain both pipes even when the returned result has reached the limit.
                while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    if (result.Length < capacity)
                        result.Append(buffer, 0, Math.Min(count, capacity - result.Length));
                }
                return result.ToString();
            }
        }
    }
}
