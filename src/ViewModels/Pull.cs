using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Pull : Popup
    {
        public List<Models.Remote> Remotes => _repo.Remotes;
        public Models.Branch Current { get; }

        public bool HasSpecifiedRemoteBranch
        {
            get;
            private set;
        }

        public Models.Remote SelectedRemote
        {
            get => _selectedRemote;
            set
            {
                if (SetProperty(ref _selectedRemote, value))
                    PostRemoteSelected();
            }
        }

        public List<Models.Branch> RemoteBranches
        {
            get => _remoteBranches;
            private set => SetProperty(ref _remoteBranches, value);
        }

        [Required(ErrorMessageResourceType = typeof(ValidationMessages), ErrorMessageResourceName = nameof(ValidationMessages.RemoteBranchRequired))]
        public Models.Branch SelectedBranch
        {
            get => _selectedBranch;
            set => SetProperty(ref _selectedBranch, value, true);
        }

        public bool HasLocalChanges
        {
            get => _repo.LocalChangesCount > 0;
        }

        public Models.DealWithLocalChanges DealWithLocalChanges
        {
            get;
            set;
        }

        public bool UseRebase
        {
            get => _repo.UIStates.PreferRebaseInsteadOfMerge;
            set => _repo.UIStates.PreferRebaseInsteadOfMerge = value;
        }

        public Pull(Repository repo, Models.Branch specifiedRemoteBranch)
        {
            _repo = repo;
            Current = repo.CurrentBranch;
            CanTerminate = true;

            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;

            if (specifiedRemoteBranch != null)
            {
                _selectedRemote = repo.Remotes.Find(x => x.Name == specifiedRemoteBranch.Remote);
                _selectedBranch = specifiedRemoteBranch;

                var branches = new List<Models.Branch>();
                foreach (var branch in _repo.Branches)
                {
                    if (branch.Remote == specifiedRemoteBranch.Remote)
                        branches.Add(branch);
                }

                _remoteBranches = branches;
                HasSpecifiedRemoteBranch = true;
            }
            else
            {
                Models.Remote autoSelectedRemote = null;
                if (!string.IsNullOrEmpty(Current?.Upstream))
                {
                    var remoteNameEndIdx = Current.Upstream.IndexOf('/', 13);
                    if (remoteNameEndIdx > 0)
                    {
                        var remoteName = Current.Upstream.Substring(13, remoteNameEndIdx - 13);
                        autoSelectedRemote = _repo.Remotes.Find(x => x.Name == remoteName);
                    }
                }

                if (autoSelectedRemote == null)
                {
                    Models.Remote remote = null;
                    if (!string.IsNullOrEmpty(_repo.Settings.DefaultRemote))
                        remote = _repo.Remotes.Find(x => x.Name == _repo.Settings.DefaultRemote);
                    _selectedRemote = remote ?? _repo.Remotes[0];
                }
                else
                {
                    _selectedRemote = autoSelectedRemote;
                }

                PostRemoteSelected();
                HasSpecifiedRemoteBranch = false;
            }
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            if (InProgressContext.HasPendingOperation(_repo.GitDir))
            {
                _repo.WorkingCopy.RequestConflictSelection();
                _repo.ShowWorkingCopyOverview();
                _repo.SendNotification(App.Text("Merge.FinishCurrent"), true);
                return true;
            }
            if (_selectedRemote == null || _selectedBranch == null)
            {
                _repo.SendNotification(App.Text("Pull.NoMergeHead"), true);
                return false;
            }
            var branch = await new Commands.QuerySymbolicHead(_repo.FullPath).GetResultAsync();
            if (Current is { IsDetachedHead: false } && (!branch.IsSuccess || branch.StdOut.Trim() != Current.FullName))
            {
                _repo.RefreshBranches();
                _repo.SendNotification(App.Text("Checkout.DestinationChanged"), true);
                return false;
            }
            var log = _repo.CreateLog("Pull");
            Use(log);
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            CanTerminate = true;
            var protectedChanges = new ProtectedLocalChanges(_repo, log);
            var success = false;
            try
            {
                if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply &&
                    !await protectedChanges.SaveAsync("PULL_AUTO_STASH")) return false;
                if (DealWithLocalChanges == Models.DealWithLocalChanges.Discard &&
                    !await Commands.Discard.AllAsync(_repo.FullPath, true, false, false, log)) return false;
                if (cancellation.IsCancellationRequested) return false;
                var target = Current?.Upstream == _selectedBranch.FullName ? null : _selectedBranch;
                var result = await Commands.SafePull.RunAsync(_repo.FullPath, _repo.GitDir, _selectedRemote, target,
                    _selectedBranch.FullName, UseRebase, false, cancellation.Token, log,
                    () => Avalonia.Threading.Dispatcher.UIThread.Post(() => CanTerminate = false));
                success = result.IsSuccess;
                var conflicts = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(conflicts) || InProgressContext.HasPendingOperation(_repo.GitDir))
                {
                    _repo.WorkingCopy.RequestConflictSelection();
                    _repo.ShowWorkingCopyOverview();
                    return true;
                }
                if (conflicts == null)
                {
                    _repo.NotifyWorkingCopyStatusFailure();
                    _repo.ShowWorkingCopyOverview();
                    return success;
                }
                if (!success)
                {
                    if (!cancellation.IsCancellationRequested) _repo.SendNotification(result.UserMessage ?? App.Text("Pull.Failed"), true);
                    return false;
                }
                try { await _repo.AutoUpdateSubmodulesAsync(log); }
                catch (Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }
                if (_repo.SelectedViewIndex == 0)
                {
                    var head = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
                    _repo.NavigateToCommit(head, true);
                }
                return true;
            }
            finally
            {
                try { await protectedChanges.RestoreAsync(success); }
                finally
                {
                    _cancellation = null;
                    log.Complete();
                    _repo.RefreshBranches();
                    _repo.MarkWorkingCopyDirtyManually();
                    _repo.MarkStashesDirtyManually();
                }
            }
        }

        public override void Terminate()
        {
            // Just fire cancel event and UI will auto wait the `Sure` complete
            var _ = _cancellation?.CancelAsync();
        }

        private void PostRemoteSelected()
        {
            var remoteName = _selectedRemote.Name;
            var branches = new List<Models.Branch>();
            foreach (var branch in _repo.Branches)
            {
                if (!branch.IsLocal && branch.Remote.Equals(remoteName, StringComparison.Ordinal))
                    branches.Add(branch);
            }

            Models.Branch selected = null;
            if (!string.IsNullOrEmpty(Current?.Upstream))
                selected = branches.Find(x => x.FullName.Equals(Current?.Upstream, StringComparison.Ordinal));

            if (selected == null)
                selected = branches.Find(x => x.Name.Equals(Current?.Name, StringComparison.Ordinal));

            RemoteBranches = branches;
            SelectedBranch = selected;
        }

        private readonly Repository _repo = null;
        private Models.Remote _selectedRemote = null;
        private List<Models.Branch> _remoteBranches = null;
        private Models.Branch _selectedBranch = null;
        private CancellationTokenSource _cancellation = null;
    }
}
