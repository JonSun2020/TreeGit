using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public enum MergeTestingState { Disabled = 0, Testing, WillCauseConflicts, UnknownError, NoConflicts }

    public class Merge : Popup
    {
        public Repository Repository => _repo;
        public object Source { get; }
        public string Into { get; }
        public string SourceRevision => _sourceName;
        public bool AutoStashAvailable => Native.OS.GitVersion >= Models.GitVersions.MERGE_AUTOSTASH;
        public bool AutoStash { get => _autoStash; set => SetProperty(ref _autoStash, value); }
        public bool AllowUnrelated { get => _allowUnrelated; set => SetProperty(ref _allowUnrelated, value); }
        public bool ShowAllowUnrelated { get => _showAllowUnrelated; private set => SetProperty(ref _showAllowUnrelated, value); }
        public bool ShowAuthorSettings { get => _showAuthorSettings; private set => SetProperty(ref _showAuthorSettings, value); }
        public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
        public bool CanUseRegularMerge => Mode == Models.MergeMode.FastForward || _fastForwardFailure;
        public Models.MergeMode Mode
        {
            get => _mode;
            set
            {
                if (!SetProperty(ref _mode, value)) return;
                CanEditMessage = _mode == Models.MergeMode.Default || _mode == Models.MergeMode.NoFastForward;
                ErrorMessage = string.Empty;
                _fastForwardFailure = false;
                OnPropertyChanged(nameof(CanUseRegularMerge));
            }
        }
        public bool CanEditMessage { get => _canEditMessage; set => SetProperty(ref _canEditMessage, value); }
        public bool Edit { get; set; }
        public MergeTestingState TestingState { get => _testingState; private set => SetProperty(ref _testingState, value); }

        public Merge(Repository repo, Models.Branch source, string into, bool forceFastForward)
        {
            _repo = repo; _sourceName = string.IsNullOrEmpty(source.Head) ? source.FriendlyName : source.Head;
            Source = source; Into = into;
            _expectedInto = repo.CurrentBranch is { IsDetachedHead: false } current ? current.FullName : null;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            Mode = forceFastForward ? Models.MergeMode.FastForward : AutoSelectMergeMode();
            if (!forceFastForward) Test();
        }
        public Merge(Repository repo, Models.Commit source, string into)
        {
            _repo = repo; _sourceName = source.SHA; Source = source; Into = into;
            _expectedInto = repo.CurrentBranch is { IsDetachedHead: false } current ? current.FullName : null;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            Mode = AutoSelectMergeMode(); Test();
        }
        public Merge(Repository repo, Models.Tag source, string into)
        {
            _repo = repo; _sourceName = string.IsNullOrEmpty(source.SHA) ? source.Name : source.SHA;
            Source = source; Into = into;
            _expectedInto = repo.CurrentBranch is { IsDetachedHead: false } current ? current.FullName : null;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            Mode = AutoSelectMergeMode(); Test();
        }

        public void UseRegularMerge() => Mode = Models.MergeMode.NoFastForward;

        public override async Task<bool> Sure()
        {
            StopTesting();
            ErrorMessage = string.Empty; ShowAuthorSettings = false;
            using var lockWatcher = _repo.LockWatcher();
            var destination = await new Commands.QuerySymbolicHead(_repo.FullPath).GetResultAsync();
            if (!destination.IsSuccess && destination.ExitCode != 1) { ErrorMessage = App.Text("WorkingCopy.RefreshFailed"); return false; }
            var currentRef = destination.IsSuccess ? destination.StdOut.Trim() : null;
            if (currentRef != _expectedInto) { ErrorMessage = App.Text("Merge.DestinationChanged"); return false; }
            var currentHead = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
            if (currentHead != _expectedHead) { ErrorMessage = App.Text("Merge.DestinationChanged"); return false; }
            var before = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
            if (before == null) { ErrorMessage = App.Text("WorkingCopy.RefreshFailed"); return false; }
            if (before.Length > 0 || HasPendingOperation())
            {
                RevealWorkingCopy(true);
                _repo.SendNotification(App.Text("Merge.FinishCurrent"), true);
                return true;
            }

            var safety = new Commands.CheckMergeSafety(_repo.FullPath, [_sourceName]);
            if (!await safety.GetResultAsync())
            {
                ErrorMessage = App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "Merge.LocalFilesBlocked");
                if (safety.Obstruction != null) ErrorMessage += "\n" + safety.Obstruction;
                return false;
            }

            ProgressDescription = App.Text("Merge.Progress", Into);
            var log = _repo.CreateLog($"Merging '{_sourceName}' into '{Into}'");
            Use(log);
            try
            {
                var command = new Commands.Merge(_repo.FullPath, _sourceName, Mode.Arg, _canEditMessage && Edit,
                    AutoStashAvailable && AutoStash, AllowUnrelated) { RaiseError = false };
                var success = await command.Use(log).ExecAsync();
                var after = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(after))
                {
                    await LoadSquashMessageIfEmpty();
                    RevealWorkingCopy(true);
                    return true; // Conflicts are a normal result, ready for manual resolution.
                }
                if (!success)
                {
                    SetFailure(command.FailureMessage);
                    return false;
                }
                await LoadSquashMessageIfEmpty();
                try { await _repo.AutoUpdateSubmodulesAsync(log); }
                catch (Exception ex)
                {
                    Native.OS.LogException(ex);
                    _repo.SendNotification(App.Text("Merge.SubmodulesPending"), true);
                }
                if (after == null)
                {
                    RevealWorkingCopy(false);
                    _repo.NotifyWorkingCopyStatusFailure();
                }
                else if (HasPendingOperation() || Mode == Models.MergeMode.Squash || Mode == Models.MergeMode.DontCommit)
                    RevealWorkingCopy(false);
                else if (_repo.SelectedViewIndex == 0)
                {
                    var head = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
                    _repo.NavigateToCommit(head, true);
                }
                return true;
            }
            finally
            {
                if (!log.IsComplete) log.Complete();
                _repo.MarkWorkingCopyDirtyManually();
            }
        }

        public override void Cleanup()
        {
            StopTesting();
            // Cancellation may race with the queued process starting. Keep the
            // token source alive until the readonly probe has actually finished.
            _ = _testingTask.ContinueWith(_ => _testingCancellation.Dispose(), TaskScheduler.Default);
            base.Cleanup();
        }

        private void RevealWorkingCopy(bool selectConflict)
        {
            if (selectConflict) _repo.WorkingCopy.RequestConflictSelection();
            _repo.ShowWorkingCopyOverview();
            _repo.MarkWorkingCopyDirtyManually();
        }
        private bool HasPendingOperation() => InProgressContext.HasPendingOperation(_repo.GitDir);

        private async Task LoadSquashMessageIfEmpty()
        {
            // A merge does not consume the user's unrelated commit draft.
            if (Mode != Models.MergeMode.Squash || !string.IsNullOrWhiteSpace(_repo.WorkingCopy.CommitMessage)) return;
            var path = Path.Combine(_repo.GitDir, "SQUASH_MSG");
            if (File.Exists(path)) _repo.SetCommitMessage(await File.ReadAllTextAsync(path));
        }
        private void SetFailure(string reason)
        {
            reason ??= string.Empty;
            _fastForwardFailure = reason.Contains("fast-forward", StringComparison.OrdinalIgnoreCase);
            ShowAllowUnrelated = reason.Contains("unrelated histories", StringComparison.OrdinalIgnoreCase);
            ShowAuthorSettings = reason.Contains("identity unknown", StringComparison.OrdinalIgnoreCase) || reason.Contains("unable to auto-detect email", StringComparison.OrdinalIgnoreCase);
            ErrorMessage = App.Text(_fastForwardFailure ? "Merge.FastForwardBlocked" : ShowAllowUnrelated ? "Merge.Unrelated" :
                ShowAuthorSettings ? "Merge.IdentityRequired" : reason.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase) ? "Merge.LocalFilesBlocked" :
                reason.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ? "Merge.IndexBusy" : "Merge.Failed");
            OnPropertyChanged(nameof(CanUseRegularMerge));
        }
        private Models.MergeMode AutoSelectMergeMode()
        {
            var config = new Commands.Config(_repo.FullPath);
            var option = config.Get($"branch.{Into}.mergeoptions");
            var mode = option switch
            {
                "--ff-only" => Models.MergeMode.FastForward, "--no-ff" => Models.MergeMode.NoFastForward,
                "--squash" => Models.MergeMode.Squash, "--no-commit" or "--no-ff --no-commit" => Models.MergeMode.DontCommit, _ => null,
            };
            if (mode != null) return mode;
            var index = _repo.Settings.PreferredMergeMode;
            if (index < 0 || index >= Models.MergeMode.Supported.Length) index = 0;
            if (index == 0 && config.Get("merge.ff") == "only") return Models.MergeMode.FastForward;
            return Models.MergeMode.Supported[index];
        }
        private void StopTesting()
        {
            if (!_testingStopped) { _testingStopped = true; _testingCancellation.Cancel(); }
            if (TestingState == MergeTestingState.Testing) TestingState = MergeTestingState.Disabled;
        }
        private void Test()
        {
            if (Native.OS.GitVersion < Models.GitVersions.TESTING_MERGE) return;
            TestingState = MergeTestingState.Testing;
            var token = _testingCancellation.Token;
            _testingTask = Task.Run(async () =>
            {
                var exit = await new Commands.MergeTree(_repo.FullPath, _sourceName, "HEAD").WithCancellation(token).GetExitCodeAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        TestingState = exit switch { 0 => MergeTestingState.NoConflicts, 1 => MergeTestingState.WillCauseConflicts, _ => MergeTestingState.UnknownError };
                });
            });
        }
        private readonly Repository _repo;
        private readonly string _sourceName, _expectedInto;
        private readonly string _expectedHead;
        private readonly CancellationTokenSource _testingCancellation = new();
        private Task _testingTask = Task.CompletedTask;
        private bool _testingStopped, _fastForwardFailure, _showAllowUnrelated, _showAuthorSettings;
        private bool _autoStash = true, _allowUnrelated;
        private string _errorMessage = string.Empty;
        private Models.MergeMode _mode = Models.MergeMode.Default;
        private bool _canEditMessage = true;
        private MergeTestingState _testingState;
    }
}
