using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class StashChanges : Popup
    {
        public string Message
        {
            get;
            set;
        }

        public bool HasSelectedFiles
        {
            get => _changes != null;
        }

        public bool IncludeUntracked
        {
            get => _repo.UIStates.IncludeUntrackedWhenStash;
            set { _repo.UIStates.IncludeUntrackedWhenStash = value; OnPropertyChanged(); }
        }

        public bool OnlyStaged
        {
            get => _repo.UIStates.OnlyStagedWhenStash;
            set
            {
                if (_repo.UIStates.OnlyStagedWhenStash != value)
                {
                    _repo.UIStates.OnlyStagedWhenStash = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ChangesAfterStashing
        {
            get => _repo.UIStates.ChangesAfterStashing;
            set { _repo.UIStates.ChangesAfterStashing = value; OnPropertyChanged(); }
        }

        public StashChanges(Repository repo, List<Models.Change> selectedChanges)
        {
            _repo = repo;
            _changes = selectedChanges;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Stash changes ...";

            var log = _repo.CreateLog("Stash Local Changes");
            Use(log);

            try
            {
                var mode = (DealWithChangesAfterStashing)ChangesAfterStashing;
                var keepIndex = mode == DealWithChangesAfterStashing.KeepIndex;
                bool succ;

                var before = await new Commands.QueryStashHead(_repo.FullPath).GetResultAsync();
                if (!before.IsSuccess && before.ExitCode != 1)
                {
                    _repo.SendNotification(App.Text("Stash.RefreshFailed"), true);
                    return false;
                }

                if (_changes == null)
                {
                    if (OnlyStaged)
                    {
                        if (Native.OS.GitVersion >= Models.GitVersions.STASH_PUSH_ONLY_STAGED)
                        {
                            var query = new Commands.QueryLocalChanges(_repo.FullPath, false);
                            var changes = await query.GetResultAsync();
                            if (!query.Succeeded) { _repo.NotifyWorkingCopyStatusFailure(); return false; }
                            if (!changes.Exists(c => c.Index != Models.ChangeState.None && c.Index != Models.ChangeState.Untracked))
                            {
                                _repo.SendNotification(App.Text("Stash.NoStagedChanges"), false);
                                return true;
                            }
                            succ = await new Commands.Stash(_repo.FullPath).Use(log).PushOnlyStagedAsync(Message, keepIndex);
                        }
                        else
                        {
                            // Selecting paths on older Git also stashes their unstaged
                            // edits. That is not equivalent to "only staged".
                            _repo.SendNotification(App.Text("Stash.OnlyStagedUnsupported"), true);
                            succ = false;
                        }
                    }
                    else
                    {
                        succ = await new Commands.Stash(_repo.FullPath)
                            .Use(log)
                            .PushAsync(Message, IncludeUntracked, keepIndex);
                    }
                }
                else
                {
                    succ = await StashWithChangesAsync(_changes, keepIndex, log);
                }

                if (mode == DealWithChangesAfterStashing.KeepAll && succ)
                {
                    var after = await new Commands.QueryStashHead(_repo.FullPath).GetResultAsync();
                    if (!after.IsSuccess && after.ExitCode != 1)
                    {
                        _repo.SendNotification(App.Text("Stash.RefreshFailed"), true);
                        succ = false;
                    }
                    // "No local changes to save" exits successfully but creates no
                    // stash. Never apply an unrelated pre-existing stash in that case.
                    else if (after.IsSuccess && after.StdOut.Trim() != before.StdOut.Trim())
                        succ = await new Commands.Stash(_repo.FullPath).Use(log).ApplyAsync(after.StdOut.Trim(), true);
                }

                return succ;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.OS.LogException(ex);
                _repo.SendNotification(App.Text("Stash.Failed"), true);
                return false;
            }
            finally
            {
                log.Complete();
                _repo.MarkWorkingCopyDirtyManually();
                _repo.MarkStashesDirtyManually();
            }
        }

        private async Task<bool> StashWithChangesAsync(List<Models.Change> changes, bool keepIndex, CommandLog log)
        {
            if (changes.Count == 0)
                return true;

            var succ = false;
            if (Native.OS.GitVersion >= Models.GitVersions.STASH_PUSH_WITH_PATHSPECFILE)
            {
                var paths = new List<string>();
                foreach (var c in changes)
                    paths.Add(c.Path);

                var pathSpecFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(pathSpecFile, string.Join('\0', paths) + '\0');
                    succ = await new Commands.Stash(_repo.FullPath)
                        .Use(log)
                        .PushAsync(Message, pathSpecFile, keepIndex, true)
                        .ConfigureAwait(false);
                }
                finally { File.Delete(pathSpecFile); }
            }
            else
            {
                // Splitting into multiple pushes creates multiple stash objects;
                // restoring only the last object loses the earlier working state.
                var length = Message?.Length ?? 0;
                foreach (var change in changes) length += change.Path.Quoted().Length + 1;
                if (length > 24000)
                {
                    _repo.SendNotification(App.Text("Stash.SelectionUnsupported"), true);
                    return false;
                }
                succ = await new Commands.Stash(_repo.FullPath).Use(log).PushAsync(Message, changes, keepIndex).ConfigureAwait(false);
            }

            return succ;
        }

        private enum DealWithChangesAfterStashing
        {
            Discard = 0,
            KeepIndex,
            KeepAll,
        }

        private readonly Repository _repo = null;
        private readonly List<Models.Change> _changes = null;
    }
}
