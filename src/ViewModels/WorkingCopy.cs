using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class WorkingCopy : ObservableObject
    {
        public Repository Repository
        {
            get => _repo;
        }

        public bool IncludeUntracked
        {
            get => _repo.IncludeUntracked;
            set
            {
                if (_repo.IncludeUntracked != value)
                {
                    _repo.IncludeUntracked = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasRemotes
        {
            get => _hasRemotes;
            set => SetProperty(ref _hasRemotes, value);
        }

        public bool HasUnsolvedConflicts
        {
            get => _hasUnsolvedConflicts;
            set => SetProperty(ref _hasUnsolvedConflicts, value);
        }

        public bool CanSwitchBranchDirectly
        {
            get;
            set;
        } = true;

        public InProgressContext InProgressContext
        {
            get => _inProgressContext;
            private set => SetProperty(ref _inProgressContext, value);
        }

        public bool IsStaging
        {
            get => _isStaging;
            private set { if (SetProperty(ref _isStaging, value)) OnPropertyChanged(nameof(CanChangeIndex)); }
        }

        public bool IsUnstaging
        {
            get => _isUnstaging;
            private set { if (SetProperty(ref _isUnstaging, value)) OnPropertyChanged(nameof(CanChangeIndex)); }
        }

        public bool IsCommitting
        {
            get => _isCommitting;
            private set
            {
                if (SetProperty(ref _isCommitting, value))
                {
                    _repo.NotifyIsSkippingOrAbortingMergeChanged();
                    OnPropertyChanged(nameof(CanChangeIndex));
                }
            }
        }

        public bool EnableSignOff
        {
            get => _repo.UIStates.EnableSignOffForCommit;
            set => _repo.UIStates.EnableSignOffForCommit = value;
        }

        public bool NoVerifyOnCommit
        {
            get => _repo.UIStates.NoVerifyOnCommit;
            set => _repo.UIStates.NoVerifyOnCommit = value;
        }

        public bool UseAmend
        {
            get => _useAmend;
            set
            {
                if (SetProperty(ref _useAmend, value))
                {
                    if (value)
                    {
                        var currentBranch = _repo.CurrentBranch;
                        if (currentBranch == null)
                        {
                            _repo.SendNotification(App.Text("WorkingCopy.NoCommitsToAmend"), true);
                            _useAmend = false;
                            OnPropertyChanged();
                            return;
                        }

                        CommitMessage = new Commands.QueryCommitFullMessage(_repo.FullPath, currentBranch.Head).GetResult();
                    }
                    else
                    {
                        CommitMessage = string.Empty;
                        ResetAuthor = false;
                    }

                    Staged = GetStagedChanges(_cached);
                    VisibleStaged = GetVisibleChanges(_staged);
                    SelectedStaged = new(null);
                }
            }
        }

        public bool ResetAuthor
        {
            get => _resetAuthor;
            set => SetProperty(ref _resetAuthor, value);
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                {
                    if (_isLoadingData)
                        return;

                    VisibleUnstaged = GetVisibleChanges(_unstaged);
                    VisibleStaged = GetVisibleChanges(_staged);
                    SelectedUnstaged = new(null);
                    SelectedStaged = new(null);
                }
            }
        }

        public List<Models.Change> Unstaged
        {
            get => _unstaged;
            private set
            {
                if (SetProperty(ref _unstaged, value))
                    OnPropertyChanged(nameof(UnstagedCountInfo));
            }
        }

        public List<Models.Change> VisibleUnstaged
        {
            get => _visibleUnstaged;
            private set
            {
                if (SetProperty(ref _visibleUnstaged, value))
                    OnPropertyChanged(nameof(UnstagedCountInfo));
            }
        }

        public List<Models.Change> Staged
        {
            get => _staged;
            private set
            {
                if (SetProperty(ref _staged, value))
                    OnPropertyChanged(nameof(StagedCountInfo));
            }
        }

        public List<Models.Change> VisibleStaged
        {
            get => _visibleStaged;
            private set
            {
                if (SetProperty(ref _visibleStaged, value))
                    OnPropertyChanged(nameof(StagedCountInfo));
            }
        }

        public string UnstagedCountInfo
        {
            get => string.IsNullOrEmpty(_filter) ? $"({_unstaged.Count})" : $"({_visibleUnstaged.Count}/{_unstaged.Count})";
        }

        public string StagedCountInfo
        {
            get => string.IsNullOrEmpty(_filter) ? $"({_staged.Count})" : $"({_visibleStaged.Count}/{_staged.Count})";
        }

        public ChangeSelection SelectedUnstaged
        {
            get => _selectedUnstaged;
            set
            {
                if (SetProperty(ref _selectedUnstaged, value))
                {
                    if (value is { Count: > 0 })
                    {
                        _isLoadingData = true;
                        if (_selectedStaged is { Count: > 0 })
                            SelectedStaged = new(null);
                        _isLoadingData = false;
                    }

                    if (!_isLoadingData)
                        UpdateDetail();
                }
            }
        }

        public ChangeSelection SelectedStaged
        {
            get => _selectedStaged;
            set
            {
                if (SetProperty(ref _selectedStaged, value))
                {
                    if (value is { Count: > 0 })
                    {
                        _isLoadingData = true;
                        if (_selectedUnstaged is { Count: > 0 })
                            SelectedUnstaged = new(null);
                        _isLoadingData = false;
                    }

                    if (!_isLoadingData)
                        UpdateDetail();
                }
            }
        }

        public object DetailContext
        {
            get => _detailContext;
            private set => SetProperty(ref _detailContext, value);
        }

        public string CommitMessage
        {
            get => _commitMessage;
            set => SetProperty(ref _commitMessage, value);
        }

        public WorkingCopy(Repository repo)
        {
            _repo = repo;
            _autoLoadedCommitMessage = repo.UIStates.LastAutoLoadedCommitMessage;
        }

        internal string AutoLoadedCommitMessage => _autoLoadedCommitMessage ?? string.Empty;
        internal string CommitMessageContext => _inProgressContext == null ? string.Empty :
            $"{_inProgressContext.GetType().Name}:{InProgressStep(_inProgressContext)}" +
            (_inProgressContext is PatchInProgress patch ? $":{patch.Subject}:{SourceGit.ViewModels.InProgressContext.ReadStateFile(Path.Combine(_repo.GitDir, "ORIG_HEAD"))}" : string.Empty);

        public void SetData(List<Models.Change> changes)
        {
            if (!IsChanged(_cached, changes))
            {
                if (_useAmend)
                {
                    var testStaged = GetStagedChanges(_cached);
                    if (IsChanged(_staged, testStaged))
                    {
                        var visibleStagedNew = GetVisibleChanges(testStaged);
                        var selectedStagedNew = new List<Models.Change>();

                        if (_selectedStaged is { Count: > 0 })
                        {
                            var set = new HashSet<string>();
                            foreach (var c in _selectedStaged.Changes)
                                set.Add(c.Path);

                            foreach (var c in visibleStagedNew)
                            {
                                if (set.Contains(c.Path))
                                    selectedStagedNew.Add(c);
                            }
                        }

                        _isLoadingData = true;
                        Staged = testStaged;
                        VisibleStaged = visibleStagedNew;
                        SelectedStaged = new(selectedStagedNew);
                        _isLoadingData = false;
                    }
                }

                HasUnsolvedConflicts = _cached.Find(x => x.IsConflicted) != null;
                UpdateInProgressState();
                ApplyRequestedConflictSelection();
                UpdateDetail();
                return;
            }

            var lastSelectedUnstaged = new HashSet<string>();
            if (_selectedUnstaged is { Count: > 0 })
            {
                foreach (var c in _selectedUnstaged.Changes)
                    lastSelectedUnstaged.Add(c.Path);
            }

            var unstaged = new List<Models.Change>();
            var visibleUnstaged = new List<Models.Change>();
            var selectedUnstaged = new List<Models.Change>();
            var noFilter = string.IsNullOrEmpty(_filter);
            var conflicts = new List<Models.Change>();
            var canSwitchDirectly = true;
            foreach (var c in changes)
            {
                if (c.WorkTree != Models.ChangeState.None)
                {
                    unstaged.Add(c);

                    if (c.IsConflicted)
                        conflicts.Add(c);

                    if (noFilter || c.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    {
                        visibleUnstaged.Add(c);
                        if (lastSelectedUnstaged.Contains(c.Path))
                            selectedUnstaged.Add(c);
                    }
                }

                if (!canSwitchDirectly)
                    continue;

                if (c.WorkTree == Models.ChangeState.Untracked || c.Index == Models.ChangeState.Added)
                    continue;

                canSwitchDirectly = false;
            }

            var staged = GetStagedChanges(changes);
            var visibleStaged = GetVisibleChanges(staged);
            var selectedStaged = new List<Models.Change>();
            if (_selectedStaged is { Count: > 0 })
            {
                var set = new HashSet<string>();
                foreach (var c in _selectedStaged.Changes)
                    set.Add(c.Path);

                foreach (var c in visibleStaged)
                {
                    if (set.Contains(c.Path))
                        selectedStaged.Add(c);
                }
            }

            if (selectedUnstaged.Count == 0 && selectedStaged.Count == 0 && conflicts.Count > 0)
            {
                var firstConflict = FindFirstConflict(conflicts);
                selectedUnstaged.Add(firstConflict);
            }

            _isLoadingData = true;
            _cached = changes;
            HasUnsolvedConflicts = conflicts.Count > 0;
            CanSwitchBranchDirectly = canSwitchDirectly;
            VisibleUnstaged = visibleUnstaged;
            VisibleStaged = visibleStaged;
            Unstaged = unstaged;
            Staged = staged;
            SelectedUnstaged = new(selectedUnstaged);
            SelectedStaged = new(selectedStaged);
            ApplyRequestedConflictSelection();
            _isLoadingData = false;

            UpdateInProgressState();
            UpdateDetail();
        }

        public bool CanChangeIndex => !IsStaging && !IsUnstaging && !IsCommitting;

        public void RequestConflictSelection()
        {
            Filter = string.Empty;
            _selectConflictAfterRefresh = true;
            ApplyRequestedConflictSelection();
        }

        private void ApplyRequestedConflictSelection()
        {
            if (!_selectConflictAfterRefresh) return;
            var conflict = VisibleUnstaged?.Find(c => c.IsConflicted);
            if (conflict == null) return;
            _selectConflictAfterRefresh = false;
            SelectedUnstaged = new(new[] { conflict });
        }

        public Task<bool> StageChangesAsync(List<Models.Change> changes, Models.Change next)
            => StageAsync(new List<Models.Change>(changes), next, false);

        public Task<bool> StageAllChangesAsync() => StageAsync(null, null, true);

        private async Task<bool> StageAsync(List<Models.Change> changes, Models.Change next, bool allVisible)
        {
            if (IsStaging || IsUnstaging) return false;
            var filter = _filter;
            var includeUntracked = IncludeUntracked;
            var originalSelection = _selectedUnstaged;
            IsStaging = true;
            using var lockWatcher = _repo.LockWatcher();
            CommandLog log = null;
            string pathSpecFile = null;
            try
            {
                // A previous index operation can finish before the file watcher
                // repaints the lists. Batch actions must use the current index.
                if (allVisible)
                {
                    var query = new Commands.QueryLocalChanges(_repo.FullPath, includeUntracked);
                    var current = await query.GetResultAsync();
                    if (!query.Succeeded) { _repo.NotifyWorkingCopyStatusFailure(); return false; }
                    changes = GetVisibleChanges(current.FindAll(c => c.WorkTree != Models.ChangeState.None), filter);
                }
                var canStage = await GetCanStageChangesAsync(changes);
                if (canStage.Count == 0) return changes.Count == 0;
                log = _repo.CreateLog("Stage");
                pathSpecFile = Path.GetTempFileName();
                await using (var writer = new StreamWriter(pathSpecFile))
                {
                    foreach (var c in canStage) await writer.WriteLineAsync(c.Path);
                }
                var success = await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                if (success && canStage.Count == changes.Count && ReferenceEquals(_selectedUnstaged, originalSelection))
                    _selectedUnstaged = new(next != null ? new List<Models.Change> { next } : null);
                return success && canStage.Count == changes.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.OS.LogException(ex);
                _repo.SendNotification("暂存未完成，请确认磁盘空间和文件访问权限后重试。", true);
                return false;
            }
            finally
            {
                DeletePathSpec(pathSpecFile);
                log?.Complete();
                _repo.MarkWorkingCopyDirtyManually();
                IsStaging = false;
            }
        }

        public Task UnstageChangesAsync(List<Models.Change> changes, Models.Change next)
            => UnstageAsync(new List<Models.Change>(changes), next, false);

        public Task UnstageAllChangesAsync() => UnstageAsync(null, null, true);

        private async Task UnstageAsync(List<Models.Change> changes, Models.Change next, bool allVisible)
        {
            if (IsStaging || IsUnstaging || IsCommitting) return;
            var filter = _filter;
            var includeUntracked = IncludeUntracked;
            var useAmend = _useAmend;
            var originalSelection = _selectedStaged;
            IsUnstaging = true;
            using var lockWatcher = _repo.LockWatcher();
            CommandLog log = null;
            string pathSpecFile = null;
            try
            {
                if (allVisible)
                {
                    var query = new Commands.QueryLocalChanges(_repo.FullPath, includeUntracked);
                    var current = await query.GetResultAsync();
                    if (!query.Succeeded) { _repo.NotifyWorkingCopyStatusFailure(); return; }
                    var staged = useAmend
                        ? await Task.Run(() => new Commands.QueryStagedChangesWithAmend(_repo.FullPath).GetResult())
                        : current.FindAll(c => c.Index != Models.ChangeState.None);
                    changes = GetVisibleChanges(staged, filter);
                }
                if (changes.Count == 0) return;
                log = _repo.CreateLog("Unstage");
                bool success;
                if (useAmend)
                {
                    log.AppendLine("$ git update-index --index-info ");
                    success = await new Commands.UpdateIndexInfo(_repo.FullPath, changes).ExecAsync();
                }
                else
                {
                    pathSpecFile = Path.GetTempFileName();
                    await using (var writer = new StreamWriter(pathSpecFile))
                    {
                        foreach (var c in changes)
                        {
                            await writer.WriteLineAsync(c.Path);
                            if (c.Index == Models.ChangeState.Renamed) await writer.WriteLineAsync(c.OriginalPath);
                        }
                    }
                    success = await new Commands.Reset(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                }
                if (success && ReferenceEquals(_selectedStaged, originalSelection))
                    _selectedStaged = new(next != null ? new List<Models.Change> { next } : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.OS.LogException(ex);
                _repo.SendNotification("取消暂存未完成，请确认磁盘空间和文件访问权限后重试。", true);
            }
            finally
            {
                DeletePathSpec(pathSpecFile);
                log?.Complete();
                _repo.MarkWorkingCopyDirtyManually();
                IsUnstaging = false;
            }
        }

        private static void DeletePathSpec(string path)
        {
            try { if (path != null) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public async Task SaveChangesToPatchAsync(List<Models.Change> changes, bool isUnstaged, string saveTo)
        {
            var succ = await Commands.SaveChangesAsPatch.ProcessLocalChangesAsync(_repo.FullPath, changes, isUnstaged, saveTo);
            if (succ)
                _repo.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public void DiscardAllChanges()
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo));
        }

        public void Discard(List<Models.Change> changes, Models.Change next)
        {
            if (_repo.CanCreatePopup())
                _repo.ShowPopup(new Discard(_repo, changes, next));
        }

        public void ClearFilter()
        {
            Filter = string.Empty;
        }

        public async Task UseTheirsAsync(List<Models.Change> changes)
        {
            using var lockWatcher = _repo.LockWatcher();

            var files = new List<string>();
            var needStage = new List<string>();
            var log = _repo.CreateLog("Use Theirs");

            foreach (var change in changes)
            {
                if (!change.IsConflicted)
                    continue;

                if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByThem or Models.ConflictReason.AddedByUs)
                {
                    var fullpath = Path.Combine(_repo.FullPath, change.Path);
                    if (File.Exists(fullpath))
                        File.Delete(fullpath);

                    needStage.Add(change.Path);
                }
                else
                {
                    files.Add(change.Path);
                }
            }

            if (files.Count > 0)
            {
                var succ = await new Commands.Checkout(_repo.FullPath).Use(log).UseTheirsAsync(files);
                if (succ)
                    needStage.AddRange(files);
            }

            if (needStage.Count > 0)
            {
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(pathSpecFile, needStage);
                await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                File.Delete(pathSpecFile);
            }

            log.Complete();
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task UseMineAsync(List<Models.Change> changes)
        {
            using var lockWatcher = _repo.LockWatcher();

            var files = new List<string>();
            var needStage = new List<string>();
            var log = _repo.CreateLog("Use Mine");

            foreach (var change in changes)
            {
                if (!change.IsConflicted)
                    continue;

                if (change.ConflictReason is Models.ConflictReason.BothDeleted or Models.ConflictReason.DeletedByUs or Models.ConflictReason.AddedByThem)
                {
                    var fullpath = Path.Combine(_repo.FullPath, change.Path);
                    if (File.Exists(fullpath))
                        File.Delete(fullpath);

                    needStage.Add(change.Path);
                }
                else
                {
                    files.Add(change.Path);
                }
            }

            if (files.Count > 0)
            {
                var succ = await new Commands.Checkout(_repo.FullPath).Use(log).UseMineAsync(files);
                if (succ)
                    needStage.AddRange(files);
            }

            if (needStage.Count > 0)
            {
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(pathSpecFile, needStage);
                await new Commands.Add(_repo.FullPath, pathSpecFile).Use(log).ExecAsync();
                File.Delete(pathSpecFile);
            }

            log.Complete();
            _repo.MarkWorkingCopyDirtyManually();
        }

        public async Task<bool> UseExternalMergeToolAsync(Models.Change change)
        {
            return await new Commands.MergeTool(_repo.FullPath, change?.Path).OpenAsync();
        }

        public void UseExternalDiffTool(Models.Change change, bool isUnstaged)
        {
            new Commands.DiffTool(_repo.FullPath, new Models.DiffOption(change, isUnstaged)).Open();
        }

        public Task ContinueMergeAsync() => ProcessInProgressAsync("Continue", (context, log) => context.ContinueAsync(log,
            IsAutoLoadedCommitMessage() ? null : _commitMessage, IsAutoLoadedCommitMessage() ? _commitMessage : null), true);

        public Task SkipMergeAsync() => ProcessInProgressAsync("Skip", (context, log) => context.SkipAsync(log));

        public Task AbortMergeAsync() => ProcessInProgressAsync("Abort", (context, log) => context.AbortAsync(log));

        private async Task ProcessInProgressAsync(string operation, Func<InProgressContext, CommandLog, Task<bool>> execute, bool writeMessage = false)
        {
            if (IsCommitting)
                return;

            var context = _inProgressContext;
            if (context == null)
            {
                _repo.MarkWorkingCopyDirtyManually();
                return;
            }
            using var lockWatcher = _repo.LockWatcher();
            IsCommitting = true;
            var log = _repo.CreateLog($"{operation} {context.Name}");
            try
            {
                var mergeMsgFile = Path.Combine(_repo.GitDir, "MERGE_MSG");
                if (writeMessage && File.Exists(mergeMsgFile) && !string.IsNullOrWhiteSpace(_commitMessage))
                    await File.WriteAllTextAsync(mergeMsgFile, _commitMessage);
                if (await execute(context, log)) CommitMessage = string.Empty;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.AppendLine(error.Message);
                _repo.SendNotification(error.Message, true);
            }
            finally
            {
                log.Complete();
                IsCommitting = false;
                // An explicit operation must update the UI even when automatic file watching is disabled.
                _repo.MarkBranchesDirtyManually();
            }
        }

        public void ApplyCommitMessageTemplate(Models.CommitTemplate tmpl)
        {
            CommitMessage = tmpl.Apply(_repo.CurrentBranch, _staged);
        }

        public async Task ClearCommitMessageHistoryAsync()
        {
            var sure = await App.AskConfirmAsync(App.Text("WorkingCopy.ClearCommitHistories.Confirm"));
            if (sure)
                _repo.UIStates.RecentCommitMessages.Clear();
        }

        public async Task CommitAsync(bool autoStage, bool autoPush)
        {
            if (!CanChangeIndex || string.IsNullOrWhiteSpace(_commitMessage))
                return;

            if (!_repo.CanCreatePopup())
            {
                _repo.SendNotification(App.Text("WorkingCopy.OperationBusy"), true);
                return;
            }

            if (autoStage && HasUnsolvedConflicts)
            {
                _repo.SendNotification(App.Text("WorkingCopy.ConflictsBeforeAutoStage"), true);
                return;
            }

            if (_repo.CurrentBranch is { IsDetachedHead: true })
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithDetachedHead");
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            if (!string.IsNullOrEmpty(_filter) && _staged.Count > _visibleStaged.Count)
            {
                var msg = App.Text("WorkingCopy.ConfirmCommitWithFilter", _staged.Count, _visibleStaged.Count, _staged.Count - _visibleStaged.Count);
                var sure = await App.AskConfirmAsync(msg);
                if (!sure)
                    return;
            }

            if (!_useAmend)
            {
                if ((!autoStage && _staged.Count == 0) || (autoStage && _cached.Count == 0))
                {
                    var rs = await App.AskConfirmEmptyCommitAsync(_cached.Count > 0, _selectedUnstaged is { Count: > 0 });
                    if (rs == Models.ConfirmEmptyCommitResult.Cancel)
                        return;

                    if (rs == Models.ConfirmEmptyCommitResult.StageAllAndCommit)
                        autoStage = true;
                    else if (rs == Models.ConfirmEmptyCommitResult.StageSelectedAndCommit)
                        if (!await StageChangesAsync(_selectedUnstaged.Changes, null)) return;
                }
            }

            if (IsCommitting) return;
            using var lockWatcher = _repo.LockWatcher();
            IsCommitting = true;
            CommandLog log = null;
            try
            {
                _repo.UIStates.AddRecentCommitMessage(_commitMessage);

                if (autoStage && _unstaged.Count > 0 && !await StageChangesAsync(_unstaged, null))
                    return;

                log = _repo.CreateLog("Commit");
                var succ = await new Commands.Commit(_repo.FullPath, _commitMessage, EnableSignOff, NoVerifyOnCommit, _useAmend, _resetAuthor)
                        .Use(log)
                        .RunAsync();

                if (succ)
                {
                    UseAmend = false;
                    CommitMessage = string.Empty;

                    if (autoPush && _repo.Remotes.Count > 0)
                    {
                        Models.Branch pushBranch = null;
                        if (_repo.CurrentBranch == null)
                        {
                            var currentBranchName = await new Commands.QueryCurrentBranch(_repo.FullPath).GetResultAsync();
                            pushBranch = new Models.Branch() { Name = currentBranchName };
                        }

                        if (_repo.CanCreatePopup())
                            await _repo.ShowAndStartPopupAsync(new Push(_repo, pushBranch));
                    }
                }
            }
            finally
            {
                log?.Complete();
                _repo.MarkBranchesDirtyManually();
                _repo.RefreshSubmodules();
                IsCommitting = false;
            }
        }

        private List<Models.Change> GetVisibleChanges(List<Models.Change> changes) => GetVisibleChanges(changes, _filter);

        private static List<Models.Change> GetVisibleChanges(List<Models.Change> changes, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return changes;

            var visible = new List<Models.Change>();

            foreach (var c in changes)
            {
                if (c.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    visible.Add(c);
            }

            return visible;
        }

        private async Task<List<Models.Change>> GetCanStageChangesAsync(List<Models.Change> changes)
        {
            if (!changes.Exists(c => c.IsConflicted))
                return changes;

            var outs = new List<Models.Change>();
            foreach (var c in changes)
            {
                if (c.IsConflicted)
                {
                    if (c.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified)
                    {
                        var state = await new Commands.QueryConflictFileState(_repo.FullPath, c).GetResultAsync();
                        if (state != Models.ConflictFileState.Resolved)
                            continue;
                    }
                    else
                    {
                        continue;
                    }
                }

                outs.Add(c);
            }

            return outs;
        }

        private List<Models.Change> GetStagedChanges(List<Models.Change> cached)
        {
            if (_useAmend)
            {
                var changes = new Commands.QueryStagedChangesWithAmend(_repo.FullPath).GetResult();
                changes.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));
                return changes;
            }

            var rs = new List<Models.Change>();
            foreach (var c in cached)
            {
                if (c.Index != Models.ChangeState.None)
                    rs.Add(c);
            }
            return rs;
        }

        private void UpdateDetail()
        {
            if (_selectedUnstaged is { Count: 1, HasFolder: false })
                SetDetail(_selectedUnstaged.Changes[0], true);
            else if (_selectedStaged is { Count: 1, HasFolder: false })
                SetDetail(_selectedStaged.Changes[0], false);
            else
                SetDetail(null, false);
        }

        private void UpdateInProgressState()
        {
            var oldContext = _inProgressContext;
            var oldType = oldContext?.GetType();

            if (File.Exists(Path.Combine(_repo.GitDir, "CHERRY_PICK_HEAD")))
                InProgressContext = new CherryPickInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "rebase-apply", "applying")))
                InProgressContext = new PatchInProgress(_repo);
            else if (Directory.Exists(Path.Combine(_repo.GitDir, "rebase-merge")) || Directory.Exists(Path.Combine(_repo.GitDir, "rebase-apply")))
                InProgressContext = new RebaseInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "REVERT_HEAD")))
                InProgressContext = new RevertInProgress(_repo);
            else if (File.Exists(Path.Combine(_repo.GitDir, "MERGE_HEAD")))
                InProgressContext = new MergeInProgress(_repo);
            else
                InProgressContext = null;

            if (!_hasLoadedInitialProgressState)
            {
                _hasLoadedInitialProgressState = true;
                var savedContext = _repo.UIStates.LastCommitMessageContext;
                // An external Git command may have advanced the operation while its tab was closed.
                if (_inProgressContext != null && !string.IsNullOrEmpty(savedContext) && savedContext != CommitMessageContext)
                    CommitMessage = string.Empty;
            }

            if (_inProgressContext != null && _inProgressContext.GetType() == oldType)
            {
                if (InProgressStep(_inProgressContext) == InProgressStep(oldContext))
                {
                    if (!string.IsNullOrEmpty(_commitMessage)) return;
                }
                else CommitMessage = string.Empty;
            }

            // The operation that filled the commit message ended outside SourceGit (for example, a
            // rebase or merge finished/aborted from the terminal). Drop the stale message unless the
            // user has edited it since it was loaded.
            if (_inProgressContext == null && IsAutoLoadedCommitMessage())
                CommitMessage = string.Empty;

            if (_inProgressContext is PatchInProgress patching)
            {
                LoadCommitMessageFromFile(patching.MessageFile);
                return;
            }

            if (_inProgressContext is RebaseInProgress applying && Path.GetFileName(applying.MessageFile) == "final-commit")
            {
                LoadCommitMessageFromFile(applying.MessageFile);
                return;
            }

            if (LoadCommitMessageFromFile(Path.Combine(_repo.GitDir, "MERGE_MSG")))
                return;

            if (_inProgressContext is not RebaseInProgress { } rebasing)
                return;

            if (LoadCommitMessageFromFile(rebasing.MessageFile))
                return;

            if (rebasing.StoppedAt != null)
                SetAutoLoadedCommitMessage(new Commands.QueryCommitFullMessage(_repo.FullPath, rebasing.StoppedAt.SHA).GetResult());
        }

        private bool LoadCommitMessageFromFile(string file)
        {
            if (!File.Exists(file))
                return false;

            var msg = SourceGit.ViewModels.InProgressContext.ReadStateFile(file);
            if (string.IsNullOrEmpty(msg))
                return false;

            if (Path.GetFileName(file) != "final-commit" && msg != _autoLoadedCommitMessage)
                msg = RemoveConflictHints(msg);
            SetAutoLoadedCommitMessage(msg);
            return true;
        }

        private string RemoveConflictHints(string message)
        {
            var lines = message.ReplaceLineEndings("\n").Split('\n');
            for (var i = lines.Length - 2; i >= 0; i--)
            {
                if (!lines[i].EndsWith(" Conflicts:", StringComparison.Ordinal)) continue;
                var marker = lines[i][..^11];
                if (marker.Length == 0) continue;
                var end = i + 1;
                while (end < lines.Length && lines[end].StartsWith(marker + "\t", StringComparison.Ordinal)) end++;
                if (end == i + 1) continue;

                // An authored block with this spelling is part of the original message,
                // not a generated conflict footer. Re-reading a cleaned message is safe.
                var source = _inProgressContext switch {
                    RebaseInProgress rebase => rebase.StoppedAt?.SHA,
                    CherryPickInProgress cherry => cherry.Head?.SHA,
                    _ => null,
                };
                if (!string.IsNullOrEmpty(source) && new Commands.QueryCommitFullMessage(_repo.FullPath, source).GetResult() == message)
                    return message;
                if (!_hasUnsolvedConflicts && !new Commands.QueryResolvedConflicts(_repo.FullPath).HasRecords()) return message;
                return (string.Join('\n', lines[..i]) + (end < lines.Length ? "\n" + string.Join('\n', lines[end..]) : string.Empty)).TrimEnd();
            }
            return message;
        }

        private static string InProgressStep(InProgressContext context) => context switch
        {
            RebaseInProgress rebase => rebase.StoppedAt?.SHA,
            PatchInProgress patch => patch.Step,
            CherryPickInProgress cherryPick => cherryPick.Head?.SHA,
            RevertInProgress revert => revert.Head?.SHA,
            MergeInProgress merge => merge.Source?.SHA,
            _ => null,
        };

        private void SetAutoLoadedCommitMessage(string msg)
        {
            var preserveDraft = !string.IsNullOrEmpty(_commitMessage) && !IsAutoLoadedCommitMessage();
            _autoLoadedCommitMessage = msg;
            if (!preserveDraft) CommitMessage = msg;
        }

        private bool IsAutoLoadedCommitMessage()
        {
            return !string.IsNullOrEmpty(_autoLoadedCommitMessage) &&
                string.Equals(_autoLoadedCommitMessage, _commitMessage, StringComparison.Ordinal);
        }

        private void SetDetail(Models.Change change, bool isUnstaged)
        {
            if (_isLoadingData)
                return;

            if (change == null)
                DetailContext = null;
            else if (change.IsConflicted)
                DetailContext = new Conflict(_repo, this, change);
            else
                DetailContext = new DiffContext(_repo.FullPath, new Models.DiffOption(change, isUnstaged), _detailContext as DiffContext);
        }

        private bool IsChanged(List<Models.Change> old, List<Models.Change> cur)
        {
            if (old.Count != cur.Count)
                return true;

            for (int idx = 0; idx < old.Count; idx++)
            {
                var o = old[idx];
                var c = cur[idx];
                if (!o.Path.Equals(c.Path, StringComparison.Ordinal) || o.Index != c.Index || o.WorkTree != c.WorkTree)
                    return true;
            }

            return false;
        }

        private Models.Change FindFirstConflict(List<Models.Change> conflicts)
        {
            if (conflicts.Count == 1 || Preferences.Instance.ChangeViewMode != Models.ChangeViewMode.Tree)
                return conflicts[0];

            var finder = (ChangeInTree l, ChangeInTree r) =>
            {
                var lDirs = l.PathSegments.Length - 1;
                var rDirs = r.PathSegments.Length - 1;
                if (lDirs == 0)
                {
                    if (rDirs == 0)
                        return Models.NumericSort.Compare(l.PathSegments[0], r.PathSegments[0]) <= 0 ? l : r;
                    else
                        return r;
                }
                else if (rDirs == 0)
                {
                    return l;
                }

                var min = Math.Min(lDirs, rDirs);
                for (var idx = 0; idx < min; idx++)
                {
                    var cmp = Models.NumericSort.Compare(l.PathSegments[idx], r.PathSegments[idx]);
                    if (cmp < 0)
                        return l;
                    else if (cmp > 0)
                        return r;
                }

                if (lDirs == rDirs)
                    return Models.NumericSort.Compare(l.PathSegments[^1], r.PathSegments[^1]) <= 0 ? l : r;

                return lDirs < rDirs ? r : l;
            };

            var collection = new List<ChangeInTree>();
            foreach (var c in conflicts)
                collection.Add(new ChangeInTree(c));

            var first = collection[0];
            for (var idx = 1; idx < collection.Count; idx++)
                first = finder(first, collection[idx]);

            return first.Change;
        }

        private class ChangeInTree
        {
            public Models.Change Change { get; set; }
            public string[] PathSegments { get; set; }

            public ChangeInTree(Models.Change change)
            {
                Change = change;
                PathSegments = change.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private Repository _repo = null;
        private bool _isLoadingData = false;
        private bool _isStaging = false;
        private bool _isUnstaging = false;
        private bool _isCommitting = false;
        private bool _useAmend = false;
        private bool _resetAuthor = false;
        private bool _hasRemotes = false;
        private List<Models.Change> _cached = [];
        private List<Models.Change> _unstaged = [];
        private List<Models.Change> _visibleUnstaged = [];
        private List<Models.Change> _staged = [];
        private List<Models.Change> _visibleStaged = [];
        private ChangeSelection _selectedUnstaged = new(null);
        private ChangeSelection _selectedStaged = new(null);
        private object _detailContext = null;
        private string _filter = string.Empty;
        private string _commitMessage = string.Empty;

        private bool _hasUnsolvedConflicts = false;
        private InProgressContext _inProgressContext = null;
        private bool _selectConflictAfterRefresh;
        private string _autoLoadedCommitMessage = null;
        private bool _hasLoadedInitialProgressState = false;
    }
}
