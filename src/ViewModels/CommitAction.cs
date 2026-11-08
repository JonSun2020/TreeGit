using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public abstract class CommitAction : Popup
    {
        public string ErrorMessage
        {
            get => _errorMessage;
            private set => SetProperty(ref _errorMessage, value);
        }

        protected CommitAction(Repository repo)
        {
            _repo = repo;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            var branch = new Commands.QuerySymbolicHead(repo.FullPath).GetResult();
            _valid = branch.IsSuccess || branch.ExitCode == 1;
            _expectedRef = branch.IsSuccess ? branch.StdOut.Trim() : null;
        }

        protected async Task<bool> RunGitActionAsync(string name, Func<CommandLog, Task<bool>> run,
            IEnumerable<(string Revision, int Parent)> patches)
        {
            using var watcher = _repo.LockWatcher();
            ErrorMessage = string.Empty;
            var log = _repo.CreateLog(name);
            Use(log);
            try
            {
                if (InProgressContext.HasPendingOperation(_repo.GitDir))
                {
                    RevealConflicts();
                    _repo.SendNotification(App.Text("CommitAction.FinishCurrent"), true);
                    return true;
                }
                var branch = await new Commands.QuerySymbolicHead(_repo.FullPath).GetResultAsync();
                if (!_valid || !branch.IsSuccess && branch.ExitCode != 1)
                { ErrorMessage = App.Text("WorkingCopy.RefreshFailed"); return false; }
                var head = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
                if (head != _expectedHead || (branch.IsSuccess ? branch.StdOut.Trim() : null) != _expectedRef)
                { ErrorMessage = App.Text("CommitAction.DestinationChanged"); return false; }
                var safety = new Commands.CheckMergeSafety(_repo.FullPath, [], patches: patches);
                if (!await safety.GetResultAsync())
                {
                    ErrorMessage = App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "CommitAction.LocalFilesBlocked") +
                        (safety.Obstruction == null ? string.Empty : "\n" + safety.Obstruction);
                    return false;
                }
                var success = await run(log);
                var conflicts = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(conflicts) || InProgressContext.HasPendingOperation(_repo.GitDir))
                { RevealConflicts(); return true; }
                if (!success) { ErrorMessage = App.Text("CommitAction.Failed"); return false; }
                if (conflicts == null) _repo.NotifyWorkingCopyStatusFailure();
                // These operations do not consume the user's unrelated draft.
                return true;
            }
            finally
            {
                log.Complete();
                _repo.RefreshBranches();
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        private void RevealConflicts()
        {
            _repo.WorkingCopy.RequestConflictSelection();
            _repo.ShowWorkingCopyOverview();
        }

        private readonly Repository _repo;
        private readonly string _expectedHead, _expectedRef;
        private readonly bool _valid;
        private string _errorMessage = string.Empty;
    }
}
