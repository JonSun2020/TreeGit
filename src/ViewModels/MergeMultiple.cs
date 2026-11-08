using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class MergeMultiple : Popup
    {
        public List<object> Targets
        {
            get;
        } = [];

        public bool AutoCommit
        {
            get;
            set;
        }

        public Models.MergeStrategy Strategy
        {
            get;
            set;
        }

        public MergeMultiple(Repository repo, List<Models.Commit> commits)
        {
            _repo = repo;
            Targets.AddRange(commits);
            AutoCommit = true;
            Strategy = Models.MergeStrategy.ForMultiple[0];
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            _sources = commits.ConvertAll(c => c.SHA);
        }

        public MergeMultiple(Repository repo, List<Models.Branch> branches)
        {
            _repo = repo;
            Targets.AddRange(branches);
            AutoCommit = true;
            Strategy = Models.MergeStrategy.ForMultiple[0];
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            _sources = branches.ConvertAll(b => b.Head);
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            var current = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
            if (current != _expectedHead) { _repo.SendNotification(App.Text("Merge.DestinationChanged"), true); return false; }
            var before = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
            if (before == null) { _repo.NotifyWorkingCopyStatusFailure(); return false; }
            if (before.Length > 0 || InProgressContext.HasPendingOperation(_repo.GitDir))
            {
                ShowWorkingCopy(true);
                _repo.SendNotification(App.Text("Merge.FinishCurrent"), true);
                return true;
            }
            var safety = new Commands.CheckMergeSafety(_repo.FullPath, _sources);
            if (!await safety.GetResultAsync())
            {
                _repo.SendNotification(App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "Merge.LocalFilesBlocked") +
                    (safety.Obstruction == null ? string.Empty : "\n" + safety.Obstruction), true);
                return false;
            }
            ProgressDescription = App.Text("Merge.Progress", _repo.CurrentBranch?.Name ?? "HEAD");

            var log = _repo.CreateLog("Merge Multiple Heads");
            Use(log);

            try
            {
                var command = new Commands.Merge(_repo.FullPath, _sources, AutoCommit, Strategy.Arg,
                    Native.OS.GitVersion >= Models.GitVersions.MERGE_AUTOSTASH) { RaiseError = false };
                var success = await command.Use(log).ExecAsync();
                var conflicts = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(conflicts)) { ShowWorkingCopy(true); return true; }
                if (!success) { _repo.SendNotification(App.Text("Merge.MultipleFailed"), true); return false; }
                try { await _repo.AutoUpdateSubmodulesAsync(log); }
                catch (System.Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Merge.SubmodulesPending"), true); }
                if (conflicts == null) { _repo.NotifyWorkingCopyStatusFailure(); ShowWorkingCopy(false); }
                else if (!AutoCommit || InProgressContext.HasPendingOperation(_repo.GitDir)) ShowWorkingCopy(false);
                else _repo.NavigateToCommit(await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync(), true);
                return true;
            }
            finally { log.Complete(); _repo.MarkWorkingCopyDirtyManually(); }
        }

        private void ShowWorkingCopy(bool selectConflict)
        {
            if (selectConflict) _repo.WorkingCopy.RequestConflictSelection();
            _repo.ShowWorkingCopyOverview();
            _repo.MarkWorkingCopyDirtyManually();
        }

        private readonly Repository _repo = null;
        private readonly List<string> _sources;
        private readonly string _expectedHead;
    }
}
