using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SourceGit.Tests
{
    internal static class CommitReportProbe
    {
        public static Func<string, Task<string>> Picker { get; private set; }

        public static object Open(Window window, JsonElement args)
        {
            if (Environment.GetEnvironmentVariable("CI") != "true" ||
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" &&
                (!OperatingSystem.IsWindows() || !Automation.LocalAuditHost.IsEnabled))
                throw new InvalidOperationException("Commit-report checks require an owned test application.");

            var history = window.GetVisualDescendants().OfType<Views.Histories>().Single(control => control.IsEffectivelyVisible);
            var grid = history.GetVisualDescendants().OfType<Views.HistoriesCommitList>().Single();
            var page = ((ViewModels.Launcher)window.DataContext).ActivePage;
            var repo = (ViewModels.Repository)page.Data;
            var fixture = Path.GetDirectoryName(repo.FullPath)!;
            if (!Path.GetFileName(fixture).StartsWith("TreeGit report fixture ", StringComparison.Ordinal))
                throw new InvalidOperationException("Open the disposable report fixture first.");

            var file = args.GetProperty("button").GetString();
            if (file != "cancel" && (Path.GetFileName(file) != file || !file.EndsWith(".html", StringComparison.Ordinal)))
                throw new ArgumentException("Use an HTML name in the isolated fixture.");

            var commits = args.GetProperty("text").GetString()!.Split(',')
                .Select(sha => repo.Histories.Commits.Single(commit => commit.SHA == sha)).ToArray();
            grid.SelectedItems.Clear();
            foreach (var commit in commits)
                grid.SelectedItems.Add(commit);

            grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            var menu = history.OpenCommitMenu ?? throw new InvalidOperationException("The history context menu did not open.");
            var item = menu.Items.OfType<MenuItem>().Single(entry => entry.Name == "CommitExportReport");
            var title = item.Header?.ToString();
            var reportFirst = ReferenceEquals(menu.Items[0], item);
            var reportIconIsColored = item.Icon is Avalonia.Controls.Shapes.Path { Data: not null, Fill: LinearGradientBrush gradient } &&
                gradient.GradientStops.Select(stop => stop.Color).Distinct().Count() > 1;
            var menuHeaders = Headers(menu).ToArray();
            Picker = suggested =>
            {
                Picker = null;
                if (Path.GetFileName(suggested) != suggested || !suggested.EndsWith(".html", StringComparison.Ordinal))
                    throw new InvalidOperationException("The suggested report name is invalid.");
                if (file != "cancel")
                    File.WriteAllText(Path.Combine(fixture, file + ".suggested"), suggested);
                return Task.FromResult(file == "cancel" ? null : Path.Combine(fixture, file));
            };
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            if (page.Popup is not ViewModels.ExportCommitReport popup)
                throw new InvalidOperationException("The report dialog did not open.");
            return new { title, menuHeaders, reportFirst, reportIconIsColored, selected = grid.SelectedItems.Count, popup.ContextLines, popup.CommitCount };
        }

        private static IEnumerable<string> Headers(ItemsControl menu) => menu.Items.OfType<MenuItem>()
            .SelectMany(item => Headers(item).Prepend(item.Header?.ToString() ?? string.Empty));
    }
}
