using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
                            model.AddIssueTracker(link.GetProperty("name").GetString(), link.GetProperty("regex").GetString(), link.GetProperty("url").GetString());
                    bool? operationResult = null;
                    object operationDetails = null;
                    if (data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String)
                    {
                        var paths = data.TryGetProperty("paths", out var selectedPaths) ? selectedPaths.EnumerateArray().Select(v => v.GetString()).ToArray() : [];
                        var changes = (await new Commands.QueryLocalChanges(repo.FullPath).GetResultAsync()).Where(c => paths.Contains(c.Path, StringComparer.Ordinal)).ToList();
                        switch (operation.GetString())
                        {
                            case "cleanup": operationResult = await new ViewModels.Cleanup(repo).Sure(); break;
                            case "stage": await repo.WorkingCopy.StageChangesAsync(changes, null); operationResult = true; break;
                            case "unstage": await repo.WorkingCopy.UnstageChangesAsync(changes, null); operationResult = true; break;
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
                            default: throw new ArgumentException("Unknown audit operation");
                        }
                    }
                    var before = new { defaultRemote = model.DefaultRemote, mergeMode = model.PreferredMergeMode, templates = model.CommitTemplates.Count };
                    if (data.GetProperty("action").GetString() == "save") await save();
                    var error = window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RepositorySettingsError");
                    var result = new { before, operationResult, operationDetails, staysOpen = window.IsVisible, error = error?.IsVisible == true ? error.Text : null,
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
    }
}
