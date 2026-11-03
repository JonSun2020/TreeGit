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
            var args = request.GetProperty("arguments");
            if (args.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("arguments must be an object.");
            if (name == "ui_state")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var launcher = App.GetLauncher();
                    return (object)new
                    {
                        application = "TreeGit",
                        processId = Environment.ProcessId,
#if INTEGRATION_TESTS
                        layoutProbe = UiLayoutProbe.Inspect(((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow),
#endif
                        mcp = "server",
                        defaultReadWrite = true,
                        readOnlyFlag = "--read-only",
                        activePage = new { kind = launcher.ActivePage.Data.GetType().Name, pendingDialog = launcher.ActivePage.Popup?.GetType().Name, notifications = launcher.ActivePage.Notifications.Count },
                        dialogs = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows
                            .Where(w => w != ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow && w.IsVisible)
                            .Select(w => new { title = w.Title, type = w.GetType().Name }).ToArray(),
                        repositories = launcher.Pages.Where(p => p.Data is ViewModels.Repository).Select(p =>
                        {
                            var r = (ViewModels.Repository)p.Data;
                            var mainWindow = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                            var repositoryView = mainWindow.GetVisualDescendants().OfType<Views.Repository>().FirstOrDefault(v => v.DataContext == r);
                            return new
                            {
                                repo = r.FullPath,
                                active = p == launcher.ActivePage,
                                view = r.SelectedViewIndex,
                                sidebarVisible = repositoryView?.IsNormalSidebarVisible,
                                sidebarWidth = repositoryView?.SidebarWidth.Value,
                                branch = r.CurrentBranch?.Name,
                                changes = r.LocalChangesCount,
                                ahead = r.CurrentBranch?.Ahead.Count ?? 0,
                                behind = r.CurrentBranch?.Behind.Count ?? 0,
                                workingCopySelected = r.Histories?.IsWorkingCopySelected,
                                searchActive = r.Histories?.IsSearchingCommits,
                                searchQuerying = r.Histories?.SearchCommitContext.IsQuerying,
                                searchSelected = r.Histories?.SearchCommitContext.Selected?.SHA,
                                searchResults = r.Histories?.SearchCommitContext.Results?.Select(c => new { sha = c.SHA, subject = c.Subject }).ToArray(),
                                detailView = r.Histories?.DetailContext?.GetType().Name,
                                graphBranch = r.Histories?.GraphBranchName,
                                graphMode = r.Histories?.BranchDisplayMode,
                                graphLoading = r.Histories?.IsLoading,
                                graphTotal = r.Histories?.Commits.Count,
                                graphCommits = r.Histories?.Commits.Take(256).Select((c, index) => new { sha = c.SHA, subject = c.Subject, x = r.Histories.Graph?.Dots[index].Center.X, subjectLeft = c.LeftMargin, refs = c.Decorators.Select(d => d.Name).ToArray() }).ToArray(),
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
                    var width = Math.Max(1, (int)window.Bounds.Width);
                    var height = Math.Max(1, (int)window.Bounds.Height);
                    using var image = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                    image.Render(window);
                    using var stream = new MemoryStream();
                    image.Save(stream);
                    return (object)new { png = Convert.ToBase64String(stream.ToArray()), width, height };
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
                    if (dialog != null)
                        throw new InvalidOperationException("Close the current dialog before opening another one.");
                    if (uiCommand == "mcp_connection")
                    {
                        App.OpenMcpConnectionCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "preferences")
                    {
                        App.OpenPreferencesCommand.Execute(null);
                        return (object)new { opened = uiCommand };
                    }
                    if (uiCommand == "about")
                    {
                        App.OpenAboutCommand.Execute(null);
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
                    if (width.HasValue && (width < 1024 || width > 3840) || height.HasValue && (height < 640 || height > 2160))
                        throw new ArgumentException("Window size is out of range.");
                    if (theme != null && theme is not ("Light" or "Dark" or "Default" or "SourceTree"))
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
                    return (object)new { width = window.Width, height = window.Height, theme = ViewModels.Preferences.Instance.Theme, state = window.WindowState.ToString() };
                });

            var path = Path.GetFullPath(Text(args, "repo")).TrimEnd('\\', '/');
            if (name == "ui_open_repository")
            {
                if (!Directory.Exists(path))
                    throw new ArgumentException("Repository directory does not exist.");
                // Never turn an arbitrary directory into a Git repository as a side effect of opening it.
                var check = await new Query(path, "rev-parse --absolute-git-dir").RunAsync().ConfigureAwait(false);
                if (!check.IsSuccess)
                    throw new ArgumentException("This directory is not a trusted Git repository: " + Redact(check.StdErr));
                return await Dispatcher.UIThread.InvokeAsync(() =>
                    (object)new { opened = App.GetLauncher().TryOpenRepositoryFromPath(path), repo = path });
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

            if (name == "ui_working_copy")
                return await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (repo.IsBare) throw new ArgumentException("Bare repositories have no working copy.");
                    repo.ShowWorkingCopyOverview();
                    return (object)new { selected = "uncommitted_changes", repo = path };
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
                    return (object)new { active = true, querying = search.IsQuerying };
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
                    if (args.TryGetProperty("branch", out var branchValue))
                    {
                        var reference = branchValue.GetString();
                        if (reference == "HEAD") repo.Histories.FocusGraphBranch(null);
                        else
                        {
                            var branch = repo.Branches.Find(b => b.FullName == reference || b.FriendlyName == reference);
                            if (branch == null) throw new ArgumentException("Branch has not been loaded or does not exist.");
                            repo.Histories.FocusGraphBranch(branch);
                        }
                    }
                    if (args.TryGetProperty("mode", out var mode))
                        repo.Histories.BranchDisplayMode = mode.GetString() switch { "all" => 0, "current" or "only" or "merged" => 1, _ => throw new ArgumentException("Invalid graph mode.") };
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
                        case "apply_patch":
                            repo.ApplyPatch();
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
            try
            {
                switch (name)
                {
                    case "git_status":
                        command = "status --porcelain=v2 --branch --untracked-files=all";
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
                        command = string.Join(' ', arguments.Select(Quote));
                        if (arguments[0] is "fetch" or "pull" or "push" or "ls-remote")
                        {
                            var configuredRemotes = await new Commands.QueryRemotes(path).GetResultAsync().ConfigureAwait(false);
                            selectedRemote = configuredRemotes.FirstOrDefault(r => arguments.Skip(1).Contains(r.Name)) ??
                                configuredRemotes.FirstOrDefault(r => r.Name == repo.Settings.DefaultRemote) ??
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
                var query = new Query(path, command);
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
                { result = await query.RunAsync().ConfigureAwait(false); }
                catch (Exception ex) { result = Commands.Command.Result.Failed(ex.Message); }
                if (write)
                {
                    log.AppendLine(Redact(result.StdOut));
                    log.AppendLine(Redact(result.StdErr));
                    log.Complete();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        repo.RefreshAll();
                        repo.McpActivity = "MCP · " + activity + (result.IsSuccess ? " ✓" : " ✕ · 查看命令日志");
                    });
                }
                var stdout = Redact(result.StdOut);
                var stderr = Redact(result.StdErr);
                const int limit = 1024 * 1024;
                return new { isSuccess = result.IsSuccess, stdout = stdout[..Math.Min(stdout.Length, limit)], stderr = stderr[..Math.Min(stderr.Length, limit)], truncated = stdout.Length > limit || stderr.Length > limit };
            }
            finally
            {
                if (tempMessage != null)
                    File.Delete(tempMessage);
            }
        }

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

            public async Task<Result> RunAsync(bool allowAuthenticationRetry = true)
            {
                using var process = new System.Diagnostics.Process { StartInfo = CreateGitStartInfo(true) };
                process.Start();
                var stdout = ReadCappedAsync(process.StandardOutput);
                var stderr = ReadCappedAsync(process.StandardError);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync()).ConfigureAwait(false);
                var result = new Result { IsSuccess = process.ExitCode == 0, StdOut = stdout.Result, StdErr = stderr.Result };
                if (!result.IsSuccess && allowAuthenticationRetry && OperatingSystem.IsWindows() && Native.RepositoryCredentials.IsAuthenticationFailure(result.StdErr))
                    return await RunAsync(false).ConfigureAwait(false);
                return result;
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
