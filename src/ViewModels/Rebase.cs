using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public enum RebaseTestingState
    {
        Disabled = 0,
        Testing,
        WillCauseConflicts,
        UnknownError,
        NoConflicts,
    }

    public class Rebase : Popup
    {
        public Models.Branch Current
        {
            get;
            private set;
        }

        public object On
        {
            get;
            private set;
        }

        public bool AutoStash
        {
            get;
            set;
        }

        public bool NoVerify
        {
            get;
            set;
        }

        public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

        public RebaseTestingState TestingState
        {
            get => _testingState;
            private set => SetProperty(ref _testingState, value);
        }

        public Rebase(Repository repo, Models.Branch current, Models.Branch on)
        {
            _repo = repo;
            _revision = on.Head;
            Current = current;
            On = on;
            AutoStash = true;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            _expectedRef = current is { IsDetachedHead: false } ? current.FullName : null;

            Test();
        }

        public Rebase(Repository repo, Models.Branch current, Models.Commit on)
        {
            _repo = repo;
            _revision = on.SHA;
            Current = current;
            On = on;
            AutoStash = true;
            _expectedHead = new Commands.QueryRevisionByRefName(repo.FullPath, "HEAD").GetResult();
            _expectedRef = current is { IsDetachedHead: false } ? current.FullName : null;

            Test();
        }

        public override async Task<bool> Sure()
        {
            StopTesting();
            await _testingTask;
            using var lockWatcher = _repo.LockWatcher();
            ErrorMessage = string.Empty;
            ProgressDescription = App.Text("Rebase.Progress");
            var log = _repo.CreateLog("Rebase");
            Use(log);
            try
            {
                if (InProgressContext.HasPendingOperation(_repo.GitDir))
                {
                    RevealConflicts();
                    _repo.SendNotification(App.Text("Rebase.FinishCurrent"), true);
                    return true;
                }
                var branch = await new Commands.QuerySymbolicHead(_repo.FullPath).GetResultAsync();
                if (!branch.IsSuccess && branch.ExitCode != 1) { ErrorMessage = App.Text("WorkingCopy.RefreshFailed"); return false; }
                var head = await new Commands.QueryRevisionByRefName(_repo.FullPath, "HEAD").GetResultAsync();
                if (head != _expectedHead || (branch.IsSuccess ? branch.StdOut.Trim() : null) != _expectedRef)
                { ErrorMessage = App.Text("Rebase.DestinationChanged"); return false; }
                var safety = new Commands.CheckMergeSafety(_repo.FullPath, [_revision]);
                if (!await safety.GetResultAsync() || !await new Commands.CheckRestoreSafety(_repo.FullPath, true).GetResultAsync())
                { ErrorMessage = App.Text("Rebase.LocalFilesBlocked"); return false; }
                var command = new Commands.Rebase(_repo.FullPath, _revision, AutoStash, NoVerify) { RaiseError = false };
                var success = await command.Use(log).ExecAsync();
                var conflicts = await new Commands.QueryConflictEntries(_repo.FullPath).GetResultAsync();
                if (!string.IsNullOrEmpty(conflicts) || InProgressContext.HasPendingOperation(_repo.GitDir))
                { RevealConflicts(); return true; }
                if (!success) { ErrorMessage = App.Text("Rebase.Failed"); return false; }
                if (conflicts == null) _repo.NotifyWorkingCopyStatusFailure();
                // Rebase does not consume an unrelated commit-message draft.
                try { await _repo.AutoUpdateSubmodulesAsync(log); }
                catch (Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }
                return true;
            }
            finally
            {
                log.Complete();
                _repo.RefreshBranches();
                _repo.MarkWorkingCopyDirtyManually();
                _repo.MarkStashesDirtyManually();
            }
        }

        private void RevealConflicts()
        {
            _repo.WorkingCopy.RequestConflictSelection();
            _repo.ShowWorkingCopyOverview();
        }

        private void StopTesting()
        {
            if (!_testingCancellation.IsCancellationRequested) _testingCancellation.Cancel();
            if (TestingState == RebaseTestingState.Testing) TestingState = RebaseTestingState.Disabled;
        }

        public override void Cleanup()
        {
            if (Interlocked.Exchange(ref _cleanupStarted, 1) == 0)
            {
                StopTesting();
                _ = _testingTask.ContinueWith(_ => _testingCancellation.Dispose(), TaskScheduler.Default);
            }
            base.Cleanup();
        }

        private void Test()
        {
            if (Native.OS.GitVersion < Models.GitVersions.REPLAY)
                return;

            var head = _expectedHead;
            if (string.IsNullOrEmpty(head)) { TestingState = RebaseTestingState.UnknownError; return; }
            TestingState = RebaseTestingState.Testing;
            _testingTask = Task.Run(async () =>
            {
                try
                {
                    if (_testingCancellation.IsCancellationRequested) return;
                    var mergeBase = await new Commands.MergeBase(_repo.FullPath, head, _revision).GetResultAsync().ConfigureAwait(false);
                    if (_testingCancellation.IsCancellationRequested) return;
                    var state = RebaseTestingState.UnknownError;
                    if (!string.IsNullOrEmpty(mergeBase))
                    {
                        if (head == mergeBase) state = RebaseTestingState.NoConflicts;
                        else
                        {
                            var exitCode = await new Commands.Replay(_repo.FullPath, _revision, $"{mergeBase}..{head}")
                                { CancellationToken = _testingCancellation.Token }.GetExitCodeAsync().ConfigureAwait(false);
                            state = exitCode switch { 0 => RebaseTestingState.NoConflicts, 1 => RebaseTestingState.WillCauseConflicts, _ => RebaseTestingState.UnknownError };
                        }
                    }
                    if (!_testingCancellation.IsCancellationRequested)
                        Dispatcher.UIThread.Post(() => { if (!_testingCancellation.IsCancellationRequested) TestingState = state; });
                }
                catch (Exception ex)
                {
                    Native.OS.LogException(ex);
                    if (!_testingCancellation.IsCancellationRequested)
                        Dispatcher.UIThread.Post(() => { if (!_testingCancellation.IsCancellationRequested) TestingState = RebaseTestingState.UnknownError; });
                }
            });
        }

        private readonly Repository _repo;
        private readonly string _revision, _expectedHead, _expectedRef;
        private readonly CancellationTokenSource _testingCancellation = new();
        private Task _testingTask = Task.CompletedTask;
        private int _cleanupStarted;
        private string _errorMessage = string.Empty;
        private RebaseTestingState _testingState = RebaseTestingState.Disabled;
    }
}
