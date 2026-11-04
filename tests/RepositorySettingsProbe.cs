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
                                if (data.TryGetProperty("draft", out var draft)) repo.WorkingCopy.CommitMessage = draft.GetString();
                                if (operation.GetString() == "native_continue")
                                {
                                    if (repo.WorkingCopy.InProgressContext == null) throw new InvalidOperationException("No operation to continue");
                                    await repo.WorkingCopy.ContinueMergeAsync();
                                }
                                repo.RefreshAll(); await Task.Delay(600);
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
                                repo.RefreshAll(); await Task.Delay(600);
                                operationDetails = new { draft = repo.WorkingCopy.CommitMessage, log = repo.Logs.FirstOrDefault()?.Content, busy = repo.WorkingCopy.IsCommitting };
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
                                    repo.RefreshAll(); await Task.Delay(650);
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
                    File.WriteAllText(fixture + ".result", JsonSerializer.Serialize(result));
                }
                catch (Exception ex)
                {
                    if (window.IsVisible) window.Close();
                    File.WriteAllText(fixture + ".result", JsonSerializer.Serialize(new { exception = ex.ToString() }));
                }
            }, DispatcherPriority.Background);
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
