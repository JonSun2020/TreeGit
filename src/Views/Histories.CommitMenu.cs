using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class Histories
    {
        public ContextMenu OpenCommitMenu { get; private set; }

        private ContextMenu CreateContextMenuForSingleCommit(ViewModels.Repository repo, Models.Commit commit)
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateCommitReportMenuItem(repo, [commit]));
            var current = repo.CurrentBranch;
            var vm = DataContext as ViewModels.Histories;
            var pending = ViewModels.InProgressContext.HasPendingOperation(repo.GitDir);
            var canChange = !repo.IsBare && current != null && !pending;
            MenuItem Add(string name, string key, string icon, Action action, bool enabled = true)
            {
                var item = new MenuItem { Name = name, Header = App.Text("CommitMenu." + key), IsEnabled = enabled,
                    Icon = this.CreateMenuIcon(icon) };
                if (!enabled && pending) ToolTip.SetTip(item, App.Text("CommitAction.FinishCurrent"));
                item.Click += (_, e) => { action(); e.Handled = true; };
                menu.Items.Add(item);
                return item;
            }
            void Popup(ViewModels.Popup popup) { if (repo.CanCreatePopup()) repo.ShowPopup(popup); }
            Add("CommitCheckout", "Checkout", "Icons.Detached", () => Popup(new ViewModels.CheckoutDetached(repo, commit)), canChange);
            Add("CommitMerge", "Merge", "Icons.Merge", () => Popup(new ViewModels.Merge(repo, commit, current.Name)), canChange && !commit.IsCurrentHead && !commit.IsMerged);
            Add("CommitRebase", "Rebase", "Icons.Rebase", () => Popup(new ViewModels.Rebase(repo, current, commit)), canChange && !commit.IsCurrentHead && !commit.IsMerged);
            Add("CommitTag", "Tag", "Icons.Tag.Add", () => Popup(new ViewModels.CreateTag(repo, commit)));
            Add("CommitArchive", "Archive", "Icons.Archive", () => Popup(new ViewModels.Archive(repo, commit)));
            Add("CommitBranch", "Branch", "Icons.Branch.Add", () => Popup(new ViewModels.CreateBranch(repo, commit)));
            Add("CommitInteractiveRebase", "InteractiveRebase", "Icons.InteractiveRebase", async () =>
                await this.ShowDialogAsync(new ViewModels.InteractiveRebase(repo, commit)), canChange && commit.IsMerged && !commit.IsCurrentHead);
            menu.Items.Add(new Separator());
            // Reset to HEAD is useful too: mixed/hard reset can operate on the
            // index/worktree even when the branch pointer does not move.
            Add("CommitReset", "Reset", "Icons.Reset", () => Popup(new ViewModels.Reset(repo, current, commit)), !repo.IsBare && current != null);
            Add("CommitRevert", "Revert", "Icons.Undo", () => Popup(new ViewModels.Revert(repo, commit)), canChange);

            var extended = CreateExtendedContextMenuForSingleCommit(repo, commit);
            var old = extended?.Items.OfType<MenuItem>().ToList() ?? [];
            extended?.Items.Clear();
            Add("CommitCherryPick", "CherryPick", "Icons.CherryPick", async () => { if (vm != null) await vm.CherryPickAsync(commit); }, canChange);
            Add("CommitCopySHA", "CopySHA", "Icons.Hash", async () => await this.CopyTextAsync(commit.SHA));

            var advanced = new MenuItem { Name = "CommitAdvanced", Header = App.Text("CommitMenu.Advanced") };
            foreach (var item in old)
            {
                if (item.Name == "LegacyCommitCore" || item.Header is string text && text == "-") continue;
                foreach (var child in item.Items.OfType<MenuItem>().Where(c => c.Name == "LegacyCommitCore").ToArray()) item.Items.Remove(child);
                advanced.Items.Add(item);
            }
            if (advanced.Items.Count > 0) { menu.Items.Add(new Separator()); menu.Items.Add(advanced); }
            return TrackCommitMenu(menu);
        }

        private ContextMenu TrackCommitMenu(ContextMenu menu)
        {
            OpenCommitMenu?.Close(); OpenCommitMenu = menu;
            menu.Closed += (_, _) => { if (OpenCommitMenu == menu) OpenCommitMenu = null; };
            return menu;
        }

        private MenuItem CreateCommitReportMenuItem(ViewModels.Repository repo, IReadOnlyList<Models.Commit> selected)
        {
            var icon = this.CreateMenuIcon("Icons.Save");
            if (icon != null)
            {
                icon.Width = icon.Height = 14;
                icon.Fill = ReportIconBrush;
            }
            var item = new MenuItem
            {
                Name = "CommitExportReport",
                Header = App.Text("CommitReport.Menu"),
                Icon = icon,
            };
            item.Click += (_, e) =>
            {
                e.Handled = true;
                if (!repo.CanCreatePopup())
                    return;

                IEnumerable<Models.Commit> ordered = selected;
                var history = (DataContext as ViewModels.Histories)?.Commits;
                if (selected.Count > 1 && history != null)
                {
                    var selectedIds = selected.Select(c => c.SHA).ToHashSet(StringComparer.Ordinal);
                    var inHistoryOrder = history.Where(c => selectedIds.Contains(c.SHA)).ToArray();
                    if (inHistoryOrder.Length == selected.Count)
                        ordered = inHistoryOrder;
                }
                var snapshot = ordered.OrderByDescending(c => c.CommitterTime).ToArray();
                repo.ShowPopup(new ViewModels.ExportCommitReport(repo, snapshot, PickCommitReportDestinationAsync));
            };
            return item;
        }

        private static readonly IBrush ReportIconBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(26, 135, 73), 0),
                new GradientStop(Color.FromRgb(32, 123, 190), 1),
            ],
        };

        private async Task<string> PickCommitReportDestinationAsync(string suggestedName)
        {
#if INTEGRATION_TESTS
            if (Tests.CommitReportProbe.Picker is { } picker)
                return await picker(suggestedName);
#endif
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null)
                return null;

            var options = new FilePickerSaveOptions
            {
                Title = App.Text("CommitReport.Title"),
                SuggestedFileName = suggestedName,
                DefaultExtension = "html",
                FileTypeChoices = [new FilePickerFileType("HTML") { Patterns = ["*.html"], MimeTypes = ["text/html"] }],
                ShowOverwritePrompt = true,
            };
            using var file = await storage.SaveFilePickerAsync(options);
            if (file == null)
                return null;
            return file.TryGetLocalPath() ?? throw new System.IO.IOException(App.Text("CommitReport.LocalFileRequired"));
        }
    }
}
