using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace SourceGit.Tests
{
    internal static class RepositorySettingsProbe
    {
        public static void OnOpened(Views.RepositorySettings window, ViewModels.RepositoryConfigure model, ViewModels.Repository repo, Func<Task> save)
        {
            var fixture = Environment.GetEnvironmentVariable("TREEGIT_TEST_SETTINGS_FIXTURE");
            if (string.IsNullOrEmpty(fixture) || !File.Exists(fixture)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(fixture));
            var data = document.RootElement.Clone();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.SelectPage(true);
                    var controls = window.GetLogicalDescendants().OfType<Control>().Where(c => !string.IsNullOrEmpty(c.Name))
                        .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
                    if (data.TryGetProperty("fields", out var fields))
                        foreach (var field in fields.EnumerateObject())
                        {
                            var control = controls[field.Name];
                            if (control is TextBox text) text.Text = field.Value.GetString();
                            else if (control is CheckBox check) check.IsChecked = field.Value.GetBoolean();
                            else if (control is ComboBox combo)
                                combo.SelectedIndex = field.Value.ValueKind == JsonValueKind.Number ? field.Value.GetInt32() :
                                    combo.Items.Cast<object>().Select((v, i) => (v, i)).Single(v => v.v?.ToString() == field.Value.GetString()).i;
                        }
                    if (data.TryGetProperty("links", out var links))
                        foreach (var link in links.EnumerateArray())
                        {
                            model.AddIssueTracker(link.GetProperty("name").GetString(), link.GetProperty("regex").GetString(), link.GetProperty("url").GetString());
                            if (link.TryGetProperty("shared", out var shared)) model.IssueTrackers.Last().IsShared = shared.GetBoolean();
                        }
                    if (data.TryGetProperty("externalLink", out var external) && external.ValueKind == JsonValueKind.Object)
                    {
                        var rule = new Models.IssueTracker { Name = external.GetProperty("name").GetString(), RegexString = external.GetProperty("regex").GetString(), URLTemplate = external.GetProperty("url").GetString() };
                        if (!await new Commands.IssueTracker(repo.FullPath, false) { RaiseError = false }.AddAsync(rule))
                            throw new InvalidOperationException("Could not create external configuration fixture");
                        repo.RefreshAll();
                        for (var attempt = 0; attempt < 100 && !repo.IssueTrackers.Any(r => r.Name == rule.Name); attempt++) await Task.Delay(50);
                        if (!repo.IssueTrackers.Any(r => r.Name == rule.Name)) throw new InvalidOperationException("Background metadata did not observe external change");
                    }
                    bool? operationResult = null;
                    object operationDetails = null;
                    if (data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String)
                    {
                        if (data.TryGetProperty("disableAutoRefresh", out var disableAutoRefresh) && disableAutoRefresh.GetBoolean()) repo.Settings.AutoRefreshEnabled = false;
                        var paths = data.TryGetProperty("paths", out var selectedPaths) ? selectedPaths.EnumerateArray().Select(v => v.GetString()).ToArray() : [];
                        var changes = (await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync()).Where(c => paths.Contains(c.Path, StringComparer.Ordinal)).ToList();
                        switch (operation.GetString())
                        {
                            case "native_merge":
                            case "native_revert":
                            case "native_cherry_pick":
                            case "native_rebase":
                                var targets = data.GetProperty("targets").EnumerateArray().Select((v, i) => new Models.Commit { SHA = v.GetString(), Subject = "Native fixture " + i }).ToList();
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString();
                                ViewModels.Popup popup = operation.GetString() switch
                                {
                                    "native_merge" => new ViewModels.Merge(repo, targets[0], "main") { AutoStash = data.TryGetProperty("autoStash", out var mergeStash) && mergeStash.GetBoolean() },
                                    "native_revert" => new ViewModels.Revert(repo, targets[0]) { AutoCommit = !data.TryGetProperty("autoCommit", out var revertCommit) || revertCommit.GetBoolean() },
                                    "native_rebase" => new ViewModels.Rebase(repo, repo.CurrentBranch ?? (await new Commands.QueryBranches(repo.FullPath).GetResultAsync()).First(b => b.Name == "main"), targets[0]) { AutoStash = data.TryGetProperty("autoStash", out var rebaseStash) && rebaseStash.GetBoolean() },
                                    _ => new ViewModels.CherryPick(repo, targets) { AutoCommit = data.TryGetProperty("autoCommit", out var pickCommit) && pickCommit.GetBoolean() },
                                };
                                if (data.TryGetProperty("switchAfterOpen", out var switchAfterOpen))
                                    await new Commands.Checkout(repo.FullPath).BranchAsync(switchAfterOpen.GetString().Quoted(), false);
                                var nativeSuccess = await popup.Sure(); popup.Cleanup(); repo.RefreshAll(); await Task.Delay(600);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, log = repo.Logs.FirstOrDefault()?.Content, messageFileExists = File.Exists(Path.Combine(repo.GitDir, "MERGE_MSG")),
                                    succeeded = nativeSuccess, error = popup is ViewModels.CommitAction action ? action.ErrorMessage : (popup as ViewModels.Rebase)?.ErrorMessage,
                                    selectedWorkingCopy = repo.Histories.IsWorkingCopySelected, activeLogs = repo.Logs.Count(l => !l.IsComplete) };
                                operationResult = true; break;
                            case "cleanup": operationResult = await new ViewModels.Cleanup(repo).Sure(); break;
                            case "native_binary_launch_recovery":
                            {
                                window.Close();
                                repo.ShowWorkingCopyOverview();
                                repo.WorkingCopy.SelectedUnstaged = null;
                                var denied = fixture + ".denied.exe"; Directory.CreateDirectory(denied);
                                var failures = 0;
                                var previousStart = Native.ProcessLauncher.BeforeStart;
                                Native.ProcessLauncher.BeforeStart = (process, _) =>
                                {
                                    if (process.StartInfo.WorkingDirectory == repo.FullPath && process.StartInfo.ArgumentList.Contains("cat-file") &&
                                        process.StartInfo.ArgumentList.Contains(":" + paths.Single()))
                                    {
                                        System.Threading.Interlocked.Increment(ref failures);
                                        process.StartInfo.FileName = denied;
                                    }
                                };
                                try
                                {
                                    repo.WorkingCopy.SetData(await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync());
                                    repo.WorkingCopy.SelectedUnstaged = new ViewModels.ChangeSelection(changes);
                                    Views.BinaryDiffView view = null;
                                    var main = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                                    for (var attempt = 0; attempt < 200; attempt++)
                                    {
                                        view = main.GetVisualDescendants().OfType<Views.BinaryDiffView>().FirstOrDefault(v => v.IsEffectivelyVisible && v.DataContext is Models.BinaryDiff d && d.Repository == repo.FullPath && d.FilePath == paths.Single());
                                        if (view?.Status == "error") break;
                                        await Task.Delay(25);
                                    }
                                    if (view?.Status != "error") throw new InvalidOperationException("The actual binary view did not report the bounded startup failure");
                                    var failedState = JsonSerializer.SerializeToElement(view.Inspect());
                                    var retry = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RetryBinaryDiff");
                                    if (!retry.IsEffectivelyVisible || !retry.IsEnabled) throw new InvalidOperationException("The binary retry button is unavailable");
                                    Native.ProcessLauncher.BeforeStart = previousStart;
                                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                    for (var attempt = 0; attempt < 200 && view.Status != "ready"; attempt++) await Task.Delay(25);
                                    if (view.Status != "ready") throw new InvalidOperationException("Retry did not recover the same binary view");
                                    operationDetails = new { before = failedState, after = view.Inspect(), failures, retryHiddenAfterRecovery = !retry.IsVisible };
                                    operationResult = true;
                                }
                                finally { Native.ProcessLauncher.BeforeStart = previousStart; }
                                break;
                            }
                            case "binary_snapshot":
                                try
                                {
                                    using var snapshot = await Commands.BinarySnapshots.LoadAsync(new Models.BinaryDiff {
                                        Repository = repo.FullPath, OldRevision = data.GetProperty("oldRevision").GetString(), NewRevision = string.Empty,
                                        OldFilePath = data.GetProperty("oldPath").GetString(), FilePath = data.GetProperty("path").GetString(),
                                    }, default);
                                    await snapshot.ScanAsync(default);
                                    operationDetails = new { success = true, oldSize = snapshot.OldSize, newSize = snapshot.NewSize, differences = snapshot.DifferentBytes };
                                }
                                catch (IOException snapshotError) { operationDetails = new { success = false, error = snapshotError.Message }; }
                                operationResult = true; break;
                            case "popup_fetch":
                            case "popup_pull":
                            case "popup_push":
                            case "popup_reset":
                            case "popup_tag":
                            case "popup_apply_stash":
                                ViewModels.Popup actionPopup = operation.GetString() switch {
                                    "popup_fetch" => new ViewModels.Fetch(repo),
                                    "popup_pull" => new ViewModels.Pull(repo, null),
                                    "popup_push" => new ViewModels.Push(repo, null),
                                    "popup_reset" => new ViewModels.Reset(repo, repo.CurrentBranch, new Models.Commit { SHA = data.GetProperty("revision").GetString() }),
                                    "popup_tag" => new ViewModels.CreateTag(repo, repo.CurrentBranch) { TagName = data.GetProperty("tag").GetString(), PushToRemotes = false },
                                    _ => new ViewModels.ApplyStash(repo, (await new Commands.QueryStashes(repo.FullPath).GetResultAsync()).First()) { RestoreIndex = false, DropAfterApply = true },
                                };
                                var ownerPage = App.GetLauncher().Pages.Single(p => ReferenceEquals(p.Data, repo));
                                ownerPage.Popup = actionPopup;
                                await ownerPage.ProcessPopupAsync();
                                var expectedView = data.GetProperty("expected");
                                for (var attempt = 0; attempt < 100; attempt++)
                                {
                                    if ((!expectedView.TryGetProperty("head", out var expectedHead) || repo.CurrentBranch?.Head == expectedHead.GetString()) &&
                                        (!expectedView.TryGetProperty("ahead", out var expectedAhead) || repo.CurrentBranch?.AheadCount == expectedAhead.GetInt32()) &&
                                        (!expectedView.TryGetProperty("behind", out var expectedBehind) || repo.CurrentBranch?.BehindCount == expectedBehind.GetInt32()) &&
                                        (!expectedView.TryGetProperty("tag", out var expectedTag) || repo.Tags.Any(t => t.Name == expectedTag.GetString())) &&
                                        (!expectedView.TryGetProperty("changes", out var expectedChanges) || repo.LocalChangesCount == expectedChanges.GetInt32())) break;
                                    await Task.Delay(50);
                                }
                                operationDetails = new { head = repo.CurrentBranch?.Head, ahead = repo.CurrentBranch?.AheadCount, behind = repo.CurrentBranch?.BehindCount,
                                    tags = repo.Tags.Select(t => t.Name).ToArray(), changes = repo.LocalChangesCount, context = repo.WorkingCopy.InProgressContext?.GetType().Name,
                                    log = repo.Logs.FirstOrDefault()?.Content, popupClosed = ownerPage.Popup == null };
                                operationResult = true; break;
                            case "native_archive":
                                var archive = new ViewModels.Archive(repo, new Models.Commit { SHA = data.GetProperty("revision").GetString() }) { SaveFile = data.GetProperty("destination").GetString() };
                                operationResult = await archive.Sure(); archive.Cleanup();
                                operationDetails = new { log = repo.Logs.FirstOrDefault()?.Content }; break;
                            case "native_bisect":
                                await repo.ExecBisectCommandAsync(data.GetProperty("arguments").GetString());
                                repo.RefreshAll(); await Task.Delay(600);
                                for (var attempt = 0; attempt < 100 && repo.BisectState == Models.BisectState.Completed && repo.Histories.SelectedCommits.FirstOrDefault()?.SHA != repo.Histories.Bisect?.FirstBadSHA; attempt++) await Task.Delay(50);
                                operationDetails = new { log = repo.Logs.FirstOrDefault()?.Content, state = repo.BisectState.ToString(), busy = repo.IsBisectCommandRunning, firstBad = repo.Histories.Bisect?.FirstBadSHA, selected = repo.Histories.SelectedCommits.FirstOrDefault()?.SHA };
                                operationResult = true; break;
                            case "native_patch":
                                var patch = new ViewModels.Apply(repo) { PatchFile = data.GetProperty("file").GetString() };
                                operationResult = await patch.Sure(); patch.Cleanup(); operationDetails = new { log = repo.Logs.FirstOrDefault()?.Content }; break;
                            case "native_push_hook":
                                for (var attempt = 0; attempt < 100 && (repo.Remotes.Count == 0 || repo.CurrentBranch == null); attempt++) await Task.Delay(50);
                                var pushHookRemote = repo.Remotes.Single(r => r.Name == "origin");
                                operationResult = await new Commands.Push(repo.FullPath, repo.CurrentBranch, pushHookRemote,
                                    new Models.Branch { Name = "main" }, false, false, false, false, false).ExecAsync();
                                operationDetails = new { log = repo.Logs.FirstOrDefault()?.Content };
                                break;
                            case "native_lfs_push":
                                for (var attempt = 0; attempt < 100 && repo.Remotes.Count == 0; attempt++) await Task.Delay(50);
                                var lfsOwner = App.GetLauncher().Pages.Single(p => ReferenceEquals(p.Data, repo));
                                lfsOwner.Popup = new ViewModels.LFSPush(repo);
                                window.Close();
                                await lfsOwner.ProcessPopupAsync();
                                operationResult = lfsOwner.Popup?.OperationSucceeded == true;
                                operationDetails = new { popupClosed = lfsOwner.Popup == null, resultVisible = lfsOwner.Popup?.CanCloseOperation == true,
                                    busy = lfsOwner.Popup?.InProgress == true, log = repo.Logs.FirstOrDefault()?.Content };
                                break;
                            case "native_blame":
                                var blame = await new Commands.Blame(repo.FullPath, paths.Single(), "HEAD", false).ReadAsync();
                                operationDetails = new { content = blame.Content, lines = blame.LineInfos.Select(l => l.CommitSHA).ToArray(),
                                    authors = blame.LineInfos.Select(l => l.Author.TrimEnd()).ToArray(), paths = blame.LineInfos.Select(l => l.File).ToArray(), binary = blame.IsBinary }; operationResult = true; break;
                            case "native_diff_race":
                                var originalDiff = new ViewModels.DiffContext(repo.FullPath, new Models.DiffOption(changes.Single(), true));
                                // A busy UI frame can delay publication while Git has
                                // already returned. Select again before that frame runs.
                                System.Threading.Thread.Sleep(500);
                                var selectedAgain = new ViewModels.DiffContext(repo.FullPath, new Models.DiffOption(changes.Single(), true), originalDiff);
                                await Task.Delay(1000);
                                operationDetails = new { original = originalDiff.Content?.GetType().Name, repeated = selectedAgain.Content?.GetType().Name };
                                operationResult = true; break;
                            case "native_partial_stage":
                            case "native_partial_unstage":
                                window.Close();
                                ViewModels.Preferences.Instance.UseSideBySideDiff = false;
                                repo.WorkingCopy.SetData(await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync());
                                repo.ShowWorkingCopyOverview();
                                if (operation.GetString() == "native_partial_stage") repo.WorkingCopy.SelectedUnstaged = new ViewModels.ChangeSelection(changes);
                                else repo.WorkingCopy.SelectedStaged = new ViewModels.ChangeSelection(changes);
                                ViewModels.TextDiffContext textContext = null;
                                for (var attempt = 0; attempt < 100; attempt++)
                                {
                                    textContext = (repo.WorkingCopy.DetailContext as ViewModels.DiffContext)?.Content as ViewModels.TextDiffContext;
                                    if (textContext != null && textContext.Option.IsUnstaged == (operation.GetString() == "native_partial_stage")) break;
                                    textContext = null;
                                    await Task.Delay(50);
                                }
                                if (textContext == null) throw new InvalidOperationException("Text diff did not load");
                                var firstLine = textContext.Data.Lines.FindIndex(l => l.Type is Models.TextDiffLineType.Added or Models.TextDiffLineType.Deleted);
                                var lastLine = firstLine;
                                while (lastLine + 1 < textContext.Data.Lines.Count && textContext.Data.Lines[lastLine + 1].Type is Models.TextDiffLineType.Added or Models.TextDiffLineType.Deleted) lastLine++;
                                textContext.SelectedChunk = new ViewModels.TextDiffSelectedChunk(0, 40, firstLine, lastLine, true, false);
                                await Task.Delay(200);
                                var mainWindow = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow;
                                Views.TextDiffView textView = null;
                                for (var attempt = 0; attempt < 100 && textView == null; attempt++)
                                {
                                    textView = mainWindow.GetVisualDescendants().OfType<Views.TextDiffView>().FirstOrDefault(v => v.IsEffectivelyVisible && ReferenceEquals(v.DataContext, textContext));
                                    if (textView == null) await Task.Delay(50);
                                }
                                if (textView == null) throw new InvalidOperationException("Text diff UI did not render");
                                var partialButton = textView.FindControl<Button>(operation.GetString() == "native_partial_stage" ? "BtnStageChunk" : "BtnUnstageChunk");
                                if (partialButton?.IsEffectivelyVisible != true || !partialButton.IsEnabled) throw new InvalidOperationException("Partial-stage UI entry is unavailable");
                                partialButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                await Task.Delay(1000);
                                operationDetails = new { visible = true, button = partialButton.Name, firstLine, lastLine };
                                operationResult = true; break;
                            case "native_close_save_failure":
                            {
                                window.Close();
                                var cacheDirectory = fixture + ".cache-test"; Directory.CreateDirectory(cacheDirectory);
                                var cachedFile = Path.Combine(cacheDirectory, "sourcegit.settings");
                                File.WriteAllText(cachedFile, "{\"DefaultRemote\":\"original-remote\"}");
                                using (var heldCache = new FileStream(cachedFile, FileMode.Open, FileAccess.Read, FileShare.None))
                                    if (Models.RepositorySettings.Get(cacheDirectory).LoadRecovery != 2) throw new InvalidOperationException("An unreadable repository settings file was not protected");
                                var reread = Models.RepositorySettings.Get(cacheDirectory);
                                if (reread.LoadRecovery != 0 || reread.DefaultRemote != "original-remote") throw new InvalidOperationException("Repository settings remained stuck in a stale recovery cache");
                                var preferences = ViewModels.Preferences.Instance;
                                preferences.SetCanModify(); preferences.Save(); repo.SaveUIState(requireSuccess: true);
                                var targetFile = data.GetProperty("target").GetString() == "draft" ? Path.Combine(repo.GitDir, "sourcegit.uistates") : Path.Combine(Native.OS.BasicDirectories.ConfigDir, "preference.json");
                                using var held = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString(); preferences.Theme = "Dark";
                                var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                                if (data.GetProperty("target").GetString() == "draft")
                                {
                                    var launcher = App.GetLauncher();
                                    var page = launcher.Pages.Single(p => ReferenceEquals(p.Data, repo));
                                    launcher.CloseTab(page);
                                    if (!launcher.Pages.Any(p => ReferenceEquals(p.Data, repo))) throw new InvalidOperationException("Closing the tab lost an unsavable draft");
                                }
                                lifetime.MainWindow.Close();
                                Views.Confirm confirm = null;
                                for (var attempt = 0; attempt < 100 && confirm == null; attempt++)
                                {
                                    confirm = lifetime.Windows.OfType<Views.Confirm>().FirstOrDefault(v => v.IsVisible);
                                    if (confirm == null) await Task.Delay(25);
                                }
                                if (confirm == null || !lifetime.MainWindow.IsVisible) throw new InvalidOperationException("Saving failed without keeping the window and a clear choice");
                                var message = confirm.FindControl<TextBlock>("Message")?.Text;
                                using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(confirm.Bounds.Width), (int)Math.Ceiling(confirm.Bounds.Height)), new Vector(96, 96)))
                                { bitmap.Render(confirm); bitmap.Save(fixture + ".close.png"); }
                                if (data.GetProperty("choice").GetString() == "exit")
                                {
                                    WriteResult(fixture, new { operationResult = true, operationDetails = new { visible = true, message, explicitExit = true } });
                                    confirm.Close(true); return;
                                }
                                confirm.Close(false); await Task.Delay(100);
                                operationDetails = new { visible = lifetime.MainWindow.IsVisible, message, draft = repo.WorkingCopy.CommitMessage, theme = preferences.Theme, explicitExit = false };
                                operationResult = true; break;
                            }
                            case "native_close_during_commit":
                                window.Close();
                                repo.WorkingCopy.SetData(await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync());
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString();
                                var commitTask = repo.WorkingCopy.CommitAsync(false, false);
                                var startedPath = data.GetProperty("started").GetString();
                                var releasePath = data.GetProperty("release").GetString();
                                try
                                {
                                    for (var attempt = 0; attempt < 200 && !File.Exists(startedPath); attempt++) await Task.Delay(25);
                                    if (!File.Exists(startedPath)) throw new InvalidOperationException("The delayed commit hook did not start");
                                    var closeLauncher = App.GetLauncher();
                                    var closingPage = closeLauncher.Pages.Single(p => ReferenceEquals(p.Data, repo));
                                    closeLauncher.CloseTab(closingPage);
                                    var stayedOpen = closeLauncher.Pages.Any(p => ReferenceEquals(p.Data, repo));
                                    var windowStayedOpen = false;
                                    var quitStayedOpen = false;
                                    var otherTabsStayedOpen = false;
                                    var rightTabsStayedOpen = false;
                                    if (stayedOpen)
                                    {
                                        closeLauncher.AddNewTab();
                                        closeLauncher.CloseOtherTabs();
                                        otherTabsStayedOpen = closeLauncher.Pages.Contains(closingPage);
                                        closeLauncher.MoveTab(closingPage, closeLauncher.ActivePage);
                                        closeLauncher.CloseRightTabs();
                                        rightTabsStayedOpen = closeLauncher.Pages.Contains(closingPage);
                                        ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow.Close();
                                        windowStayedOpen = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow.IsVisible;
                                        App.Quit(0);
                                        quitStayedOpen = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).MainWindow.IsVisible;
                                    }
                                    operationDetails = new { stayedOpen, windowStayedOpen, quitStayedOpen, otherTabsStayedOpen, rightTabsStayedOpen, busy = repo.WorkingCopy.IsCommitting };
                                }
                                finally { await File.WriteAllTextAsync(releasePath, "continue"); }
                                await commitTask;
                                operationResult = true; break;
                            case "native_amend_unstage":
                                window.Close();
                                for (var attempt = 0; attempt < 100 && repo.CurrentBranch == null; attempt++) await Task.Delay(25);
                                repo.WorkingCopy.SetData(await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync());
                                repo.WorkingCopy.UseAmend = true;
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString();
                                var allAmendChanges = data.TryGetProperty("allChanges", out var allChanges) && allChanges.GetBoolean();
                                var amendSelection = repo.WorkingCopy.Staged.Where(c => allAmendChanges || paths.Contains(c.Path, StringComparer.Ordinal)).ToList();
                                if (amendSelection.Count == 0 || !allAmendChanges && amendSelection.Count != paths.Length) throw new InvalidOperationException("The native amend view did not contain all selected changes");
                                await repo.WorkingCopy.UnstageChangesAsync(amendSelection, null);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, busy = repo.WorkingCopy.IsUnstaging,
                                    selected = amendSelection.Select(c => new { path = c.Path, original = c.OriginalPath, state = c.Index.ToString(), mode = c.DataForAmend?.FileMode }).ToArray() };
                                operationResult = true; break;
                            case "native_commit":
                                repo.WorkingCopy.SetData(await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync());
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString();
                                if (data.TryGetProperty("twice", out var twice) && twice.GetBoolean())
                                    await Task.WhenAll(repo.WorkingCopy.CommitAsync(data.GetProperty("autoStage").GetBoolean(), false), repo.WorkingCopy.CommitAsync(data.GetProperty("autoStage").GetBoolean(), false));
                                else await repo.WorkingCopy.CommitAsync(data.GetProperty("autoStage").GetBoolean(), false);
                                repo.RefreshAll(); await Task.Delay(600);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, busy = repo.WorkingCopy.IsCommitting,
                                    staging = repo.WorkingCopy.IsStaging, logs = repo.Logs.Take(3).Select(l => l.Content).ToArray() };
                                operationResult = true; break;
                            case "native_interactive":
                                for (var attempt = 0; attempt < 100 && repo.CurrentBranch == null; attempt++) { repo.RefreshBranches(); await Task.Delay(50); }
                                var interactive = new ViewModels.InteractiveRebase(repo, new Models.Commit { SHA = data.GetProperty("onto").GetString() }) { AutoStash = false };
                                for (var attempt = 0; attempt < 200 && interactive.IsLoading; attempt++) await Task.Delay(25);
                                if (interactive.IsLoading) throw new InvalidOperationException("Interactive history did not load");
                                foreach (var job in data.GetProperty("jobs").EnumerateArray())
                                {
                                    var item = interactive.Items.Single(i => i.Commit.SHA == job.GetProperty("sha").GetString());
                                    interactive.ChangeAction([item], Enum.Parse<Models.InteractiveRebaseAction>(job.GetProperty("action").GetString()));
                                    if (job.TryGetProperty("message", out var message)) { item.FullMessage = message.GetString(); item.IsMessageUserEdited = true; }
                                }
                                operationResult = await interactive.Start(); repo.RefreshAll(); await Task.Delay(600);
                                operationDetails = new { log = repo.Logs.FirstOrDefault()?.Content, draft = repo.WorkingCopy.CommitMessage };
                                break;
                            case "read_draft":
                            case "set_draft":
                            case "native_continue":
                            case "native_skip":
                                if (data.TryGetProperty("draft", out var draft)) repo.WorkingCopy.CommitMessage = draft.GetString();
                                if (operation.GetString() is "native_continue" or "native_skip")
                                {
                                    if (repo.WorkingCopy.InProgressContext == null) throw new InvalidOperationException("No operation to continue");
                                    if (operation.GetString() == "native_skip") await repo.WorkingCopy.SkipMergeAsync();
                                    else await repo.WorkingCopy.ContinueMergeAsync();
                                }
                                if (operation.GetString() is "native_continue" or "native_skip") await WaitForOperationView(repo);
                                else { repo.RefreshAll(); await Task.Delay(600); }
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, context = repo.WorkingCopy.InProgressContext?.GetType().Name,
                                    step = (repo.WorkingCopy.InProgressContext as ViewModels.RebaseInProgress)?.StoppedAt?.SHA ?? (repo.WorkingCopy.InProgressContext as ViewModels.PatchInProgress)?.Step, log = repo.Logs.FirstOrDefault()?.Content, busy = repo.WorkingCopy.IsCommitting };
                                operationResult = true; break;
                            case "native_abort":
                                for (var attempt = 0; attempt < 100 && repo.WorkingCopy.InProgressContext == null; attempt++) { repo.RefreshAll(); await Task.Delay(50); }
                                if (repo.WorkingCopy.InProgressContext == null) throw new InvalidOperationException("No native operation context loaded");
                                repo.WorkingCopy.CommitMessage = data.GetProperty("draft").GetString();
                                if (!data.TryGetProperty("lock", out var takeLock) || takeLock.GetBoolean())
                                {
                                    using var indexLock = new FileStream(Path.Combine(repo.GitDir, "index.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);
                                    await repo.WorkingCopy.AbortMergeAsync();
                                }
                                else await repo.WorkingCopy.AbortMergeAsync();
                                await WaitForOperationView(repo);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, log = repo.Logs.FirstOrDefault()?.Content, busy = repo.WorkingCopy.IsCommitting, context = repo.WorkingCopy.InProgressContext?.GetType().Name };
                                operationResult = true; break;
                            case "stage": await repo.WorkingCopy.StageChangesAsync(changes, null); operationResult = true; break;
                            case "submodule_state":
                                operationDetails = (await new Commands.QuerySubmodules(repo.FullPath).GetResultAsync()).Select(m => new { path = m.Path, url = m.URL, branch = m.Branch, status = m.Status.ToString() }).ToArray(); operationResult = true; break;
                            case "submodule_url": operationResult = await new Commands.Submodule(repo.FullPath).SetURLAsync(paths.Single(), data.GetProperty("value").GetString()); break;
                            case "submodule_branch": operationResult = await new Commands.Submodule(repo.FullPath).SetBranchAsync(paths.Single(), data.GetProperty("value").GetString()); break;
                            case "submodule_add":
                                var moduleLog = repo.CreateLog("Add module audit");
                                operationResult = await new Commands.Submodule(repo.FullPath).Use(moduleLog).AddAsync(data.GetProperty("value").GetString(), paths.Single(), false);
                                moduleLog.Complete(); operationDetails = new { log = moduleLog.Content }; break;
                            case "credential_mode":
                                var modes = new System.Collections.Generic.List<bool> { Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath) };
                                foreach (var enabled in new[] { true, false })
                                {
                                    await new Commands.Config(repo.FullPath).SetAsync("sourcegit.credentialmanager", enabled ? "true" : "false");
                                    repo.RefreshAll();
                                    modes.Add(Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath));
                                }
                                operationDetails = modes; operationResult = true; break;
                            case "submodule_update": operationResult = await new Commands.Submodule(repo.FullPath).UpdateAsync(paths.ToList(), true, false, false); break;
                            case "submodule_deinit": operationResult = await new Commands.Submodule(repo.FullPath).DeinitAsync(paths.Single(), true); break;
                            case "submodule_delete": operationResult = await new ViewModels.DeleteSubmodule(repo, paths.Single()).Sure(); break;
                            case "unstage": await repo.WorkingCopy.UnstageChangesAsync(changes, null); operationResult = true; break;
                            case "ours": await repo.WorkingCopy.UseMineAsync(changes); operationResult = true; break;
                            case "theirs": await repo.WorkingCopy.UseTheirsAsync(changes); operationResult = true; break;
                            case "discard":
                                var log = repo.CreateLog("Selected-path audit");
                                await Commands.Discard.ChangesAsync(repo.FullPath, changes, log); log.Complete(); operationResult = true; break;
                            case "stash": operationResult = await new Commands.Stash(repo.FullPath).PushAsync("Selected-path audit", changes, false); break;
                            case "stash_file":
                                var spec = Path.GetTempFileName();
                                try { await File.WriteAllLinesAsync(spec, paths); operationResult = await new Commands.Stash(repo.FullPath).PushAsync("Selected-path file audit", spec, false); }
                                finally { File.Delete(spec); }
                                break;
                            case "checkout_file": operationResult = await new Commands.Checkout(repo.FullPath).FileWithRevisionAsync(paths.Single(), "HEAD"); break;
                            case "patch": operationResult = await Commands.SaveChangesAsPatch.ProcessLocalChangesAsync(repo.FullPath, changes, true, fixture + ".patch"); break;
                            case "file_history":
                                operationDetails = (await new Commands.QueryFileHistory(repo.FullPath, paths.Single(), "HEAD").GetResultAsync()).Select(v => v.SHA).ToArray();
                                operationResult = true; break;
                            case "dialog_remote":
                                operationDetails = await InspectDialog(window, new Views.RepositoryRemoteEditor(repo, null), fixture, false); operationResult = true; break;
                            case "dialog_template":
                                operationDetails = await InspectDialog(window, new Views.RepositoryForm("编辑提交模板", ["名称", "内容"], ["模板", string.Join('\n', Enumerable.Repeat("Multiline body", 25))], 1), fixture, false); operationResult = true; break;
                            case "dialog_credentials":
                                await new Commands.Config(repo.FullPath).SetAsync("sourcegit.credentialmanager", "true");
                                Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, true);
                                var configFile = Path.Combine(Path.GetDirectoryName(repo.Settings.StoragePath)!, "config");
                                var originalConfig = File.ReadAllBytes(configFile);
                                var bounds = await InspectDialog(window, new Views.RepositoryCredentials(repo), fixture, true);
                                operationDetails = new { bounds, invalidInputPreservedConfig = originalConfig.AsSpan().SequenceEqual(File.ReadAllBytes(configFile)) };
                                operationResult = true; break;
                            case "settings_dropdown_scroll":
                                foreach (var expander in window.GetLogicalDescendants().OfType<Expander>())
                                    expander.IsExpanded = expander.Name is "TreeGitSettingsCommitLinks" or "TreeGitSettingsSyncMerge";
                                window.Height = Math.Min(window.Height, 420);
                                await Task.Delay(150);
                                var settingsScroll = (ScrollViewer)controls["RepositorySettingsScroll"];
                                var mergeMode = (ComboBox)controls["MergeMode"];
                                var defaultRemote = (ComboBox)controls["DefaultRemote"];
                                var scrollChecks = new System.Collections.Generic.List<object>();
                                foreach (var dropdown in new[] { defaultRemote, mergeMode })
                                {
                                    var label = ((Grid)dropdown.Parent).Children.OfType<TextBlock>().Single();
                                    settingsScroll.Offset = new Vector(0, Math.Max(0, settingsScroll.Offset.Y + dropdown.TranslatePoint(default, settingsScroll).Value.Y + dropdown.Bounds.Height - settingsScroll.Viewport.Height + 4));
                                    await Task.Delay(80);
                                    var offsetBefore = settingsScroll.Offset;
                                    var anchorBefore = dropdown.TranslatePoint(default, window).Value;
                                    dropdown.Focus(); dropdown.BringIntoView(); dropdown.IsDropDownOpen = true;
                                    await Task.Delay(100);
                                    var anchorAfter = dropdown.TranslatePoint(default, window).Value;
                                    scrollChecks.Add(new { dropdown = dropdown.Name, stable = Math.Abs(offsetBefore.Y - settingsScroll.Offset.Y) < .5 && Math.Abs(anchorBefore.Y - anchorAfter.Y) < .5,
                                        rightAligned = label.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right,
                                        offsetBefore = offsetBefore.Y, offsetAfter = settingsScroll.Offset.Y, anchorBefore = anchorBefore.Y, anchorAfter = anchorAfter.Y });
                                    dropdown.IsDropDownOpen = false;
                                }
                                operationDetails = scrollChecks; operationResult = true; break;
                            case "credential_dropdown_scroll":
                                operationDetails = await InspectCredentialDropdown(window, repo);
                                operationResult = true; break;
                            case "credential_save":
                                operationDetails = await EditCredential(window, repo, data.GetProperty("case").GetString()); operationResult = true; break;
                            default: throw new ArgumentException("Unknown audit operation");
                        }
                    }
                    var before = new { defaultRemote = model.DefaultRemote, mergeMode = model.PreferredMergeMode, templates = repo.Settings.CommitTemplates.Count };
                    if (data.GetProperty("action").GetString() == "save") await save();
                    var error = window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RepositorySettingsError");
                    var result = new { before, operationResult, operationDetails, links = model.IssueTrackers.Select(r => new { name = r.Name, shared = r.IsShared }).ToArray(), staysOpen = window.IsVisible, error = error?.IsVisible == true ? error.Text : null,
                        controls = controls.Keys.ToArray(), hasOtherConfiguration = window.GetLogicalDescendants().OfType<Button>().Any(b => b.Content?.ToString()?.Contains("其他配置", StringComparison.Ordinal) == true) };
                    if (window.IsVisible) window.Close();
                    WriteResult(fixture, result);
                }
                catch (Exception ex)
                {
                    if (window.IsVisible) window.Close();
                    WriteResult(fixture, new { exception = ex.ToString() });
                }
            }, DispatcherPriority.Background);
        }

        private static void WriteResult(string fixture, object result)
        {
            File.WriteAllText(fixture + ".result.tmp", JsonSerializer.Serialize(result));
            File.Move(fixture + ".result.tmp", fixture + ".result", true);
        }

        private static async Task WaitForOperationView(ViewModels.Repository repo)
        {
            // Observe the result of the real action; never refresh on its behalf.
            var kind = string.Empty;
            var step = string.Empty;
            foreach (var state in new[] {
                ("CHERRY_PICK_HEAD", "CherryPickInProgress"), ("rebase-apply/applying", "PatchInProgress"),
                ("rebase-merge/stopped-sha", "RebaseInProgress"), ("rebase-apply/original-commit", "RebaseInProgress"),
                ("REVERT_HEAD", "RevertInProgress"), ("MERGE_HEAD", "MergeInProgress") })
            {
                var file = Path.Combine(repo.GitDir, state.Item1);
                if (!File.Exists(file)) continue;
                kind = state.Item2;
                step = File.ReadAllText(kind == "PatchInProgress" ? Path.Combine(repo.GitDir, "rebase-apply/next") : file).Trim();
                break;
            }
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var context = repo.WorkingCopy.InProgressContext;
                var current = context switch {
                    ViewModels.RebaseInProgress rebase => rebase.StoppedAt?.SHA,
                    ViewModels.PatchInProgress patch => patch.Step,
                    ViewModels.CherryPickInProgress cherry => cherry.Head?.SHA,
                    ViewModels.RevertInProgress revert => revert.Head?.SHA,
                    ViewModels.MergeInProgress merge => merge.Source?.SHA,
                    _ => string.Empty,
                };
                if ((context?.GetType().Name ?? string.Empty) == kind && current == step) return;
                await Task.Delay(50);
            }
        }

        private static async Task<object> InspectDialog(Window owner, Window dialog, string fixture, bool credential)
        {
            var result = new TaskCompletionSource<object>();
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    foreach (var expander in dialog.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
                    if (credential)
                    {
                        dialog.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "CredentialMode").IsChecked = false;
                        var credentialConfirm = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCredentials");
                        for (var wait = 0; wait < 250 && !credentialConfirm.IsEnabled; wait++) await Task.Delay(20);
                        if (!credentialConfirm.IsEnabled) throw new InvalidOperationException("Credential account lookup did not complete");
                        dialog.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "CredentialPassword").Text = "password-without-account";
                        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCredentials").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var required = App.Text("TreeGit.Credentials.UsernameRequired");
                        for (var attempt = 0; attempt < 100 && !dialog.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Name == "CredentialStatus" && t.Text == required); attempt++) await Task.Delay(30);
                        if (!dialog.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Name == "CredentialStatus" && t.Text == required))
                            throw new InvalidOperationException("The invalid-input action did not complete");
                    }
                    else if (dialog is Views.RepositoryRemoteEditor)
                    {
                        dialog.GetLogicalDescendants().OfType<TextBox>().First().Text = "new-audit-remote";
                        dialog.GetLogicalDescendants().OfType<Button>().First(b => b.Classes.Contains("dialog_confirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    await Task.Delay(200);
                    var area = dialog.Screens.ScreenFromWindow(dialog)?.WorkingArea ?? throw new InvalidOperationException("Missing screen");
                    var footerTexts = new[] { "Sure", "Cancel", "Close", "TreeGit.Ok", "TreeGit.Cancel", "TreeGit.Save", "TreeGit.Close", "TreeGit.Credentials.Forget" }.Select(key => App.Text(key)).ToHashSet(StringComparer.Ordinal);
                    var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string text && footerTexts.Contains(text))
                        .Select(b =>
                        {
                            var start = b.TranslatePoint(default, dialog);
                            var end = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), dialog);
                            var caption = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                            var captionStart = caption?.TranslatePoint(default, b);
                            return new { text = b.Content, standard = b.Classes.Contains("dialog_action"), width = b.Bounds.Width, height = b.Bounds.Height,
                                primary = b.Classes.Contains("primary"), background = b.Background?.ToString(), foreground = b.Foreground?.ToString(),
                                fontSize = caption?.FontSize,
                                centered = caption != null && captionStart.HasValue && Math.Abs(captionStart.Value.X + caption.Bounds.Width / 2 - b.Bounds.Width / 2) < 1 && Math.Abs(captionStart.Value.Y + caption.Bounds.Height / 2 - b.Bounds.Height / 2) < 1,
                                visible = b.IsEffectivelyVisible && start.HasValue && end.HasValue && start.Value.X >= 0 && start.Value.Y >= 0 && end.Value.X <= dialog.Bounds.Width && end.Value.Y <= dialog.Bounds.Height };
                        }).ToArray();
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(dialog.Bounds.Width), (int)Math.Ceiling(dialog.Bounds.Height)), new Vector(96, 96));
                    bitmap.Render(dialog); bitmap.Save(fixture + ".dialog.png");
                    var header = dialog.GetVisualDescendants().OfType<Grid>().SingleOrDefault(c => c.Name == "DialogTitleBar");
                    var headerTitle = header?.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(c => c.Name == "DialogTitleText");
                    var headerClose = header?.GetVisualDescendants().OfType<Views.CaptionButtons>().SingleOrDefault();
                    result.SetResult(new { width = dialog.Bounds.Width, height = dialog.Bounds.Height, x = dialog.Position.X, y = dialog.Position.Y, scaling = dialog.RenderScaling,
                        header = new { title = headerTitle?.Text, windowTitle = dialog.Title, height = header?.Bounds.Height,
                            closeVisible = headerClose?.IsEffectivelyVisible == true, closeEnabled = headerClose?.IsEnabled == true,
                            centered = headerTitle != null && Math.Abs(headerTitle.Bounds.Center.Y - header.Bounds.Height / 2) < 1 },
                        workArea = new { x = area.X, y = area.Y, width = area.Width, height = area.Height }, buttons, topmost = dialog.Topmost, hasOwner = dialog.Owner == owner,
                        withinScreen = dialog.Position.X >= area.X && dialog.Position.Y >= area.Y && dialog.Position.X + dialog.Bounds.Width * dialog.RenderScaling <= area.Right + 1 && dialog.Position.Y + dialog.Bounds.Height * dialog.RenderScaling <= area.Bottom + 1 });
                }
                catch (Exception error) { result.SetException(error); }
                finally { dialog.Close(); }
            };
            await dialog.ShowDialog(owner);
            return await result.Task;
        }

        private static async Task<object> InspectCredentialDropdown(Window owner, ViewModels.Repository repo)
        {
            var dialog = new Views.RepositoryCredentials(repo);
            var showing = dialog.ShowDialog(owner);
            try
            {
                var dropdown = dialog.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "CredentialRemote");
                var confirm = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCredentials");
                for (var attempt = 0; attempt < 160 && !confirm.IsEnabled; attempt++)
                    await Task.Delay(25);
                dialog.UpdateLayout();
                var scroll = dropdown.FindAncestorOfType<ScrollViewer>() ?? throw new InvalidOperationException("Credential viewport is unavailable.");
                if (!confirm.IsEnabled || dropdown.ItemCount < 2)
                    throw new InvalidOperationException("Credential fixture did not load both remotes.");

                const double clippedHeight = 8;
                var pickerTop = dropdown.TranslatePoint(default, scroll).Value.Y + scroll.Offset.Y;
                scroll.Height = scroll.Extent.Height - pickerTop - clippedHeight;
                dialog.UpdateLayout();
                scroll.ScrollToEnd();
                await Task.Delay(75);
                var offset = scroll.Offset.Y;
                var bottom = scroll.Extent.Height - scroll.Viewport.Height;
                var relativeTop = dropdown.TranslatePoint(default, scroll).Value.Y;
                var clippedAtBottom = bottom > 0 && Math.Abs(offset - bottom) < 1 &&
                    relativeTop < -1 && relativeTop + dropdown.Bounds.Height > 1;
                var anchor = dropdown.TranslatePoint(default, dialog).Value.Y;

                dropdown.Focus(NavigationMethod.Pointer);
                dropdown.BringIntoView();
                await Task.Delay(50);
                var afterFocus = scroll.Offset.Y;
                dropdown.IsDropDownOpen = true;
                await Task.Delay(75);
                var popup = dropdown.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "PART_Popup");
                var popupOffset = popup.Child?.TranslatePoint(default, dropdown)?.X;
                var afterOpen = scroll.Offset.Y;
                var anchorAfter = dropdown.TranslatePoint(default, dialog).Value.Y;
                var popupAligned = popup.IsOpen && popupOffset.HasValue && Math.Abs(popupOffset.Value) <= 1;
                dropdown.IsDropDownOpen = false;
                await Task.Delay(50);
                var afterClose = scroll.Offset.Y;
                var textBlocks = dialog.GetLogicalDescendants().OfType<TextBlock>().ToArray();
                var description = textBlocks.Single(c => c.Name == "CredentialDescription");
                var status = textBlocks.Single(c => c.Name == "CredentialStatus");
                var hint = textBlocks.Single(c => c.Name == "CredentialIdentityHint");
                var hintGap = hint.Bounds.Top - status.Bounds.Bottom;
                return new
                {
                    clippedAtBottom, popupAligned, offset, afterFocus, afterOpen, afterClose, anchor, anchorAfter,
                    pathTopInset = ((Control)scroll.Content).Margin.Top,
                    hintGap,
                    descriptionFlows = description.Text.Contains("SSH", StringComparison.Ordinal) && !description.Text.Contains('\n') && !description.Text.Contains('\r'),
                    stable = Math.Abs(offset - afterFocus) < .5 && Math.Abs(offset - afterOpen) < .5 &&
                        Math.Abs(offset - afterClose) < .5 && Math.Abs(anchor - anchorAfter) < .5,
                };
            }
            finally
            {
                dialog.Close();
                await showing;
            }
        }

        private static async Task<object> EditCredential(Window owner, ViewModels.Repository repo, string scenario)
        {
            var remote = new Commands.QueryRemotes(repo.FullPath).GetResult().Select(r => new Uri(r.URL)).First(u => u.Scheme is "http" or "https");
            var storageRemote = scenario.StartsWith("redirect_", StringComparison.Ordinal) ? new Uri(remote.AbsoluteUri.TrimEnd('/') + ".git") : remote;
            scenario = scenario.Replace("redirect_", string.Empty, StringComparison.Ordinal);
            var initiallySystemMode = scenario != "early_input";
            await new Commands.Config(repo.FullPath).SetAsync("sourcegit.credentialmanager", initiallySystemMode ? "true" : "false");
            Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, initiallySystemMode);
            Native.RepositoryCredentials.Save(repo.FullPath, storageRemote, "stored-test-user", "stored-test-secret");
            var config = Path.Combine(Path.GetDirectoryName(repo.Settings.StoragePath)!, "config");
            var before = File.ReadAllBytes(config);
            var attributes = File.GetAttributes(config);
            var locked = false;
            var completion = new TaskCompletionSource<object>();
            var dialog = new Views.RepositoryCredentials(repo);
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    var controls = dialog.GetLogicalDescendants().OfType<Control>().Where(c => c.Name != null).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
                    var mode = (CheckBox)controls["CredentialMode"];
                    var save = (Button)controls["ConfirmCredentials"];
                    var user = (TextBox)controls["CredentialUsername"];
                    var password = (TextBox)controls["CredentialPassword"];
                    var modeEnabledAtOpen = mode.IsEffectivelyEnabled;
                    var stillLoadingAtOpen = !save.IsEnabled;
                    var fieldsEnabledAtOpen = user.IsEffectivelyEnabled && password.IsEffectivelyEnabled;
                    if (scenario is not ("clear_button" or "clear_cancel")) mode.IsChecked = false;
                    var fieldsMatchModeWhileLoading = !save.IsEnabled && user.IsEffectivelyEnabled == (mode.IsChecked != true) &&
                        password.IsEffectivelyEnabled == (mode.IsChecked != true);
                    if (scenario == "early_input")
                    {
                        user.Text = "early-test-user";
                        password.Text = "early-test-secret";
                    }
                    for (var wait = 0; wait < 250 && !save.IsEnabled; wait++) await Task.Delay(20);
                    if (!save.IsEnabled) throw new InvalidOperationException("Credential account lookup did not complete");
                    var userLabel = (TextBlock)controls["CredentialUsernameLabel"];
                    var passwordLabel = (TextBlock)controls["CredentialPasswordLabel"];
                    var remotePicker = (ComboBox)controls["CredentialRemote"];
                    var remoteLabel = (TextBlock)controls["CredentialRemoteLabel"];
                    dialog.UpdateLayout();
                    Point Position(Control control, double y = 0) => control.TranslatePoint(new Point(0, y), dialog).Value;
                    bool Inline(Control label, Control field) => Math.Abs(Position(label, label.Bounds.Height / 2).Y - Position(field, field.Bounds.Height / 2).Y) <= 1 &&
                        Position(label).X + label.Bounds.Width < Position(field).X;
                    var inlineFields = Inline(userLabel, user) && Inline(passwordLabel, password) && Math.Abs(Position(user).X - Position(password).X) <= 1;
                    var remoteAligned = Inline(remoteLabel, remotePicker) && Math.Abs(Position(remotePicker).X - Position(user).X) <= 1 &&
                        Math.Abs(remotePicker.Bounds.Width - user.Bounds.Width) <= 1 && Math.Abs(remotePicker.Bounds.Height - user.Bounds.Height) <= 1;
                    var rightAligned = new[] { remoteLabel, userLabel, passwordLabel }.All(label => label.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right &&
                        Math.Abs(Position(user).X - Position(label).X - label.Bounds.Width - 8) <= 1);
                    var remoteCaption = remoteLabel.Text;
                    var modeToFieldsGap = Position(user).Y - Position(mode, mode.Bounds.Height).Y;
                    var passwordCaption = passwordLabel.Text;
                    var passwordWatermark = password.Watermark?.ToString();
                    var storedAccountShown = user.Text == "stored-test-user";
                    var earlyInputPreserved = user.Text == "early-test-user" && password.Text == "early-test-secret";
                    var clearCancelled = scenario == "clear_cancel";
                    if (scenario == "empty_password") user.Text = "new-test-user";
                    else if (scenario == "explicit_clear") { password.Text = "typed value"; password.Text = string.Empty; }
                    else if (scenario == "oversized") { user.Text = "changed-test-user"; password.Text = new string('x', 6000); }
                    else if (scenario is "config_locked" or "commit_failure") { user.Text = "changed-test-user"; password.Text = "changed-test-secret"; }
                    else if (scenario is "clear_button" or "clear_cancel")
                    {
                        var clearButton = (Button)controls["ForgetCredentials"];
                        if (!clearButton.IsEnabled) throw new InvalidOperationException("Clearing must remain available for the system authentication mode.");
                        clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (scenario == "clear_text") user.Text = string.Empty;
                    else if (scenario is not ("unchanged" or "early_input")) throw new ArgumentException("Unknown credential scenario");
                    if (scenario == "config_locked") { File.WriteAllText(config + ".lock", "owned credential test"); locked = true; }
                    if (scenario == "commit_failure") File.SetAttributes(config, attributes | FileAttributes.ReadOnly);
                    if (!clearCancelled) save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else ((Button)controls["CancelCredentials"]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 200 && !save.IsEnabled; attempt++) await Task.Delay(30);
                    if (!save.IsEnabled) throw new InvalidOperationException("Credential save did not complete");
                    var saved = Native.RepositoryCredentials.Get(repo.FullPath, storageRemote);
                    var status = (TextBlock)controls["CredentialStatus"];
                    var success = status.Text == App.Text("TreeGit.Credentials.Saved");
                    completion.SetResult(new { success, configurationUnchanged = before.AsSpan().SequenceEqual(File.ReadAllBytes(config)),
                        originalCredentialPreserved = saved.HasValue && saved.Value.Username == "stored-test-user" && saved.Value.Password == "stored-test-secret",
                        emptyPasswordSaved = saved.HasValue && saved.Value.Password.Length == 0,
                        newUsernameSaved = saved.HasValue && saved.Value.Username == "new-test-user",
                        storedAccountShown, cleared = !saved.HasValue, automaticallyClosed = !dialog.IsVisible,
                        modeEnabledAtOpen, stillLoadingAtOpen, fieldsEnabledAtOpen, fieldsMatchModeWhileLoading, earlyInputPreserved,
                        earlyInputSaved = saved.HasValue && saved.Value.Username == "early-test-user" && saved.Value.Password == "early-test-secret",
                        inlineFields, remoteAligned, rightAligned, remoteCaption, modeToFieldsGap, passwordCaption, passwordWatermark,
                        localizedInputError = scenario != "oversized" || status.Text == App.Text("TreeGit.Credentials.TooLong") && status.Foreground == Avalonia.Media.Brushes.IndianRed,
                        systemMode = Native.RepositoryCredentials.UsesCredentialManager(repo.FullPath) });
                }
                catch (Exception error) { completion.SetException(error); }
                finally
                {
                    File.SetAttributes(config, attributes);
                    if (locked) File.Delete(config + ".lock");
                    dialog.Close();
                }
            };
            try { await dialog.ShowDialog(owner); return await completion.Task; }
            finally { Native.RepositoryCredentials.Forget(repo.FullPath, remote); Native.RepositoryCredentials.Forget(repo.FullPath, storageRemote); }
        }
    }
}
