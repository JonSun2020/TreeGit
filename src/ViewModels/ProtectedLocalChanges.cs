using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    // Preserve the exact stash created by this action. On failure, restore it
    // automatically only when Git is still at the original, non-conflicted HEAD.
    internal sealed class ProtectedLocalChanges(Repository repo, CommandLog log)
    {
        public async Task<bool> SaveAsync(string message)
        {
            var count = new Commands.CountLocalChanges(repo.FullPath, false);
            var changes = await count.GetResultAsync();
            if (!count.Succeeded) { repo.NotifyWorkingCopyStatusFailure(); return false; }
            if (changes == 0) return true;
            if (!await new Commands.CheckRestoreSafety(repo.FullPath, true).GetResultAsync())
            {
                repo.SendNotification(App.Text("Merge.LocalFilesBlocked"), true);
                return false;
            }
            var before = await new Commands.QueryStashHead(repo.FullPath).GetResultAsync();
            var branch = await new Commands.QuerySymbolicHead(repo.FullPath).GetResultAsync();
            _originalHead = await new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResultAsync();
            if ((!before.IsSuccess && before.ExitCode != 1) || (!branch.IsSuccess && branch.ExitCode != 1) || string.IsNullOrEmpty(_originalHead))
            {
                repo.NotifyWorkingCopyStatusFailure();
                return false;
            }
            _originalBranch = branch.IsSuccess ? branch.StdOut.Trim() : null;
            var saved = await new Commands.Stash(repo.FullPath).Use(log).PushAsync(message, false);
            var after = await new Commands.QueryStashHead(repo.FullPath).GetResultAsync();
            if (!after.IsSuccess && after.ExitCode != 1)
            {
                repo.SendNotification(App.Text("Stash.RefreshFailed"), true);
                repo.MarkWorkingCopyDirtyManually();
                repo.MarkStashesDirtyManually();
                return false;
            }
            if (after.IsSuccess && after.StdOut.Trim() != before.StdOut.Trim()) _stash = after.StdOut.Trim();
            return saved;
        }

        public async Task<bool> RestoreAsync(bool succeeded)
        {
            if (_stash == null) return true;
            var conflicts = await new Commands.QueryConflictEntries(repo.FullPath).GetResultAsync();
            if (conflicts == null || conflicts.Length > 0 || InProgressContext.HasPendingOperation(repo.GitDir)) return Kept();
            if (!succeeded)
            {
                var branch = await new Commands.QuerySymbolicHead(repo.FullPath).GetResultAsync();
                if (!branch.IsSuccess && branch.ExitCode != 1) return Kept();
                var head = await new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResultAsync();
                if (head != _originalHead || (branch.IsSuccess ? branch.StdOut.Trim() : null) != _originalBranch) return Kept();
            }
            if (!await new Commands.Stash(repo.FullPath).Use(log).ApplyAsync(_stash, true)) return Kept();
            var top = await new Commands.QueryStashHead(repo.FullPath).GetResultAsync();
            if (top.IsSuccess && top.StdOut.Trim() == _stash)
                await new Commands.Stash(repo.FullPath).Use(log).DropAsync("stash@{0}");
            _stash = null;
            return true;
        }

        private bool Kept()
        {
            if (!_notified)
            {
                _notified = true;
                repo.SendNotification(App.Text("CheckoutRemote.StashKept"), true);
            }
            repo.WorkingCopy.RequestConflictSelection();
            repo.ShowWorkingCopyOverview();
            repo.MarkStashesDirtyManually();
            repo.MarkWorkingCopyDirtyManually();
            return false;
        }

        private string _stash, _originalHead, _originalBranch;
        private bool _notified;
    }
}
