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
        public static void OnOpened(Views.RepositorySettings window, ViewModels.RepositoryConfigure model, Func<Task> save)
        {
            var fixture = Environment.GetEnvironmentVariable("TREEGIT_TEST_SETTINGS_FIXTURE");
            if (string.IsNullOrEmpty(fixture) || !File.Exists(fixture)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(fixture));
            var data = document.RootElement.Clone();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
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
                    var before = new { defaultRemote = model.DefaultRemote, mergeMode = model.PreferredMergeMode, templates = model.CommitTemplates.Count };
                    if (data.GetProperty("action").GetString() == "save") await save();
                    var error = window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RepositorySettingsError");
                    var result = new { before, staysOpen = window.IsVisible, error = error?.IsVisible == true ? error.Text : null,
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
