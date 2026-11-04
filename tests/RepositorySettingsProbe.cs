using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
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
                    if (data.TryGetProperty("templates", out var templates))
                        foreach (var template in templates.EnumerateArray())
                            model.CommitTemplates.Add(new Models.CommitTemplate { Name = template.GetProperty("name").GetString(), Content = template.GetProperty("content").GetString() });
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
                                    "native_merge" => new ViewModels.Merge(repo, targets[0], "main"),
                                    "native_revert" => new ViewModels.Revert(repo, targets[0]),
                                    "native_rebase" => new ViewModels.Rebase(repo, repo.CurrentBranch ?? (await new Commands.QueryBranches(repo.FullPath).GetResultAsync()).First(b => b.Name == "main"), targets[0]) { AutoStash = false },
                                    _ => new ViewModels.CherryPick(repo, targets) { AutoCommit = false },
                                };
                                await popup.Sure(); popup.Cleanup(); repo.RefreshAll(); await Task.Delay(600);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, log = repo.Logs.FirstOrDefault()?.Content, messageFileExists = File.Exists(Path.Combine(repo.GitDir, "MERGE_MSG")) };
                                operationResult = true; break;
                            case "cleanup": operationResult = await new ViewModels.Cleanup(repo).Sure(); break;
                            case "binary_snapshot":
                                try
                                {
                                    using var snapshot = await Commands.BinarySnapshots.LoadAsync(new Models.BinaryDiff {
                                        Repository = repo.FullPath, OldRevision = data.GetProperty("oldRevision").GetString(), NewRevision = string.Empty,
                                        OldFilePath = data.GetProperty("oldPath").GetString(), FilePath = data.GetProperty("path").GetString(),
                                    }, default);
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
                                        (!expectedView.TryGetProperty("ahead", out var expectedAhead) || repo.CurrentBranch?.Ahead.Count == expectedAhead.GetInt32()) &&
                                        (!expectedView.TryGetProperty("behind", out var expectedBehind) || repo.CurrentBranch?.Behind.Count == expectedBehind.GetInt32()) &&
                                        (!expectedView.TryGetProperty("tag", out var expectedTag) || repo.Tags.Any(t => t.Name == expectedTag.GetString())) &&
                                        (!expectedView.TryGetProperty("changes", out var expectedChanges) || repo.LocalChangesCount == expectedChanges.GetInt32())) break;
                                    await Task.Delay(50);
                                }
                                operationDetails = new { head = repo.CurrentBranch?.Head, ahead = repo.CurrentBranch?.Ahead.Count, behind = repo.CurrentBranch?.Behind.Count,
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
                            case "native_lfs_push":
                                for (var attempt = 0; attempt < 100 && repo.Remotes.Count == 0; attempt++) await Task.Delay(50);
                                var lfsOwner = App.GetLauncher().Pages.Single(p => ReferenceEquals(p.Data, repo));
                                lfsOwner.Popup = new ViewModels.LFSPush(repo);
                                window.Close();
                                await lfsOwner.ProcessPopupAsync();
                                operationResult = lfsOwner.Popup == null;
                                operationDetails = new { popupClosed = lfsOwner.Popup == null, busy = lfsOwner.Popup?.InProgress == true, log = repo.Logs.FirstOrDefault()?.Content };
                                break;
                            case "native_blame":
                                var blame = await new Commands.Blame(repo.FullPath, paths.Single(), "HEAD", false).ReadAsync();
                                operationDetails = new { content = blame.Content, lines = blame.LineInfos.Select(l => l.CommitSHA).ToArray(), binary = blame.IsBinary }; operationResult = true; break;
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
                            case "credential_save":
                                operationDetails = await EditCredential(window, repo, data.GetProperty("case").GetString()); operationResult = true; break;
                            default: throw new ArgumentException("Unknown audit operation");
                        }
                    }
                    var before = new { defaultRemote = model.DefaultRemote, mergeMode = model.PreferredMergeMode, templates = model.CommitTemplates.Count };
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
                        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ConfirmCredentials").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (var attempt = 0; attempt < 100 && !dialog.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Name == "CredentialStatus" && t.Text?.Contains("请输入账号", StringComparison.Ordinal) == true); attempt++) await Task.Delay(30);
                        if (!dialog.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Name == "CredentialStatus" && t.Text?.Contains("请输入账号", StringComparison.Ordinal) == true))
                            throw new InvalidOperationException("The invalid-input action did not complete");
                    }
                    else if (dialog is Views.RepositoryRemoteEditor)
                    {
                        dialog.GetLogicalDescendants().OfType<TextBox>().First().Text = "new-audit-remote";
                        dialog.GetLogicalDescendants().OfType<Button>().First(b => b.Content?.ToString() == "确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    await Task.Delay(200);
                    var area = dialog.Screens.ScreenFromWindow(dialog)?.WorkingArea ?? throw new InvalidOperationException("Missing screen");
                    var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string text && text is "确定" or "取消" or "保存" or "关闭" or "清除已保存认证")
                        .Select(b =>
                        {
                            var start = b.TranslatePoint(default, dialog);
                            var end = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), dialog);
                            return new { text = b.Content, visible = b.IsEffectivelyVisible && start.HasValue && end.HasValue && start.Value.X >= 0 && start.Value.Y >= 0 && end.Value.X <= dialog.Bounds.Width && end.Value.Y <= dialog.Bounds.Height };
                        }).ToArray();
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(dialog.Bounds.Width), (int)Math.Ceiling(dialog.Bounds.Height)), new Vector(96, 96));
                    bitmap.Render(dialog); bitmap.Save(fixture + ".dialog.png");
                    result.SetResult(new { width = dialog.Bounds.Width, height = dialog.Bounds.Height, buttons, topmost = dialog.Topmost,
                        withinScreen = dialog.Position.X >= area.X && dialog.Position.Y >= area.Y && dialog.Position.X + dialog.Bounds.Width * dialog.RenderScaling <= area.Right + 1 && dialog.Position.Y + dialog.Bounds.Height * dialog.RenderScaling <= area.Bottom + 1 });
                }
                catch (Exception error) { result.SetException(error); }
                finally { dialog.Close(); }
            };
            await dialog.ShowDialog(owner);
            return await result.Task;
        }

        private static async Task<object> EditCredential(Window owner, ViewModels.Repository repo, string scenario)
        {
            var remote = new Commands.QueryRemotes(repo.FullPath).GetResult().Select(r => new Uri(r.URL)).First(u => u.Scheme is "http" or "https");
            await new Commands.Config(repo.FullPath).SetAsync("sourcegit.credentialmanager", "true");
            Native.RepositoryCredentials.SetCredentialManagerMode(repo.FullPath, true);
            Native.RepositoryCredentials.Save(repo.FullPath, remote, "stored-test-user", "stored-test-secret");
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
                    ((CheckBox)controls["CredentialMode"]).IsChecked = false;
                    var user = (TextBox)controls["CredentialUsername"];
                    var password = (TextBox)controls["CredentialPassword"];
                    if (scenario == "empty_password") user.Text = "new-test-user";
                    else if (scenario == "explicit_clear") { password.Text = "typed value"; password.Text = string.Empty; }
                    else if (scenario == "oversized") { user.Text = "changed-test-user"; password.Text = new string('x', 6000); }
                    else if (scenario is "config_locked" or "commit_failure") { user.Text = "changed-test-user"; password.Text = "changed-test-secret"; }
                    else if (scenario != "unchanged") throw new ArgumentException("Unknown credential scenario");
                    if (scenario == "config_locked") { File.WriteAllText(config + ".lock", "owned credential test"); locked = true; }
                    if (scenario == "commit_failure") File.SetAttributes(config, attributes | FileAttributes.ReadOnly);
                    var save = (Button)controls["ConfirmCredentials"];
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 200 && !save.IsEnabled; attempt++) await Task.Delay(30);
                    if (!save.IsEnabled) throw new InvalidOperationException("Credential save did not complete");
                    var saved = Native.RepositoryCredentials.Get(repo.FullPath, remote);
                    var success = ((TextBlock)controls["CredentialStatus"]).Text?.StartsWith("已保存此仓库", StringComparison.Ordinal) == true;
                    completion.SetResult(new { success, configurationUnchanged = before.AsSpan().SequenceEqual(File.ReadAllBytes(config)),
                        originalCredentialPreserved = saved.HasValue && saved.Value.Username == "stored-test-user" && saved.Value.Password == "stored-test-secret",
                        emptyPasswordSaved = saved.HasValue && saved.Value.Password.Length == 0,
                        newUsernameSaved = saved.HasValue && saved.Value.Username == "new-test-user",
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
            finally { Native.RepositoryCredentials.Forget(repo.FullPath, remote); }
        }
    }
}
