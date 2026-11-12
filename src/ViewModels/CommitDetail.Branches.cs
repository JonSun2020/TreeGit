using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public partial class CommitDetail
    {
        public Models.CommitBranchDetails BranchDetails
        {
            get => _branchDetails;
            private set { if (SetProperty(ref _branchDetails, value)) { OnPropertyChanged(nameof(BranchDisplayStatus)); OnPropertyChanged(nameof(BranchDisplayDescription)); OnPropertyChanged(nameof(CanShowOtherBranches)); } }
        }

        public bool IsBranchDetailsLoading { get => _isBranchDetailsLoading; private set { if (SetProperty(ref _isBranchDetailsLoading, value)) OnPropertyChanged(nameof(BranchDisplayStatus)); } }
        public bool HasBranchDetailsError { get => _hasBranchDetailsError; private set { if (SetProperty(ref _hasBranchDetailsError, value)) { OnPropertyChanged(nameof(BranchDisplayStatus)); OnPropertyChanged(nameof(BranchDisplayDescription)); } } }
        public bool CanShowOtherBranches => BranchDetails?.HasMore == true;
        public string BranchDisplayStatus => BranchDetails?.Primary != null ? string.Empty : IsBranchDetailsLoading ? App.Text("CommitBranches.Loading") :
            HasBranchDetailsError ? App.Text("CommitBranches.Failed") : BranchDetails?.Primary == null ? App.Text("CommitBranches.Unknown") : string.Empty;
        public string BranchDisplayDescription => HasBranchDetailsError ? App.Text("CommitBranches.Failed") : BranchDetails?.Description;

        private void RefreshBranchDetails(Models.Commit commit, CancellationToken token)
        {
            var request = ++_branchDetailsRequest;
            if (BranchDetails?.Commit.SHA != commit?.SHA) BranchDetails = null;
            IsBranchDetailsLoading = commit != null;
            HasBranchDetailsError = false;
            if (commit == null) return;
            var graph = _repo.Histories.Commits?.ToList() ?? [];
            var locals = _repo.Branches.Where(b => b.IsLocal && !b.IsDetachedHead).ToList();
            var key = string.Join('\n', locals.OrderBy(b => b.FullName, StringComparer.Ordinal).Select(b => b.FullName + " " + b.Head));
            if (_originSnapshot == null || _originSnapshotKey != key || (_originSnapshotToken.IsCancellationRequested && !_originSnapshot.IsCompletedSuccessfully))
            {
                _originSnapshotKey = key;
                _originSnapshotToken = token;
                _originSnapshot = Task.Run(() => new Commands.QueryLocalCommitOrigins(_repo.FullPath) { CancellationToken = token }
                    .GetResultAsync(new HashSet<string>(locals.Select(b => b.FullName), StringComparer.Ordinal)), token);
            }
            var origins = _originSnapshot;
            Task.Run(async () =>
            {
                try
                {
                    var names = await new Commands.QuerySearchBranchNames(_repo.FullPath) { CancellationToken = token }
                        .GetResultAsync([commit]).ConfigureAwait(false);
                    if (!names.TryGetValue(commit.SHA, out var coverage)) throw new InvalidOperationException("Branch query did not complete.");
                    var recorded = await origins.ConfigureAwait(false);
                    var origin = recorded.TryGetValue(commit.SHA, out var reference) ? locals.Find(b => b.FullName == reference) : null;
                    var details = new Models.CommitBranchDetails(commit, coverage, graph, origin);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || request != _branchDetailsRequest || _commit?.SHA != commit.SHA) return;
                        BranchDetails = details; IsBranchDetailsLoading = false;
                        _repo.SynchronizeCommitBranchSelection(commit, details);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    Native.OS.LogException(error);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || request != _branchDetailsRequest || _commit?.SHA != commit.SHA) return;
                        HasBranchDetailsError = true; IsBranchDetailsLoading = false;
                    });
                }
            }, token);
        }

        private Models.CommitBranchDetails _branchDetails;
        private int _branchDetailsRequest;
        private bool _isBranchDetailsLoading;
        private bool _hasBranchDetailsError;
        private Task<Dictionary<string, string>> _originSnapshot;
        private string _originSnapshotKey;
        private CancellationToken _originSnapshotToken;
    }
}
