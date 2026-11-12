using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public partial class Histories
    {
        public ContextMenu OpenCommitMenu { get; private set; }

        private ContextMenu CreateContextMenuForSingleCommit(ViewModels.Repository repo, Models.Commit commit)
        {
            var menu = new ContextMenu();
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
            var patch = old.FirstOrDefault(item => item.Name == "LegacyCommitPatch");
            if (patch != null)
            {
                patch.Name = "CommitPatch"; patch.Header = App.Text("CommitMenu.Patch"); menu.Items.Add(patch); old.Remove(patch);
            }
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
    }
}
