using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Conflict : ObservableObject
    {
        public WorkingCopy WorkingCopy => _wc;
        public string FilePath => _change.Path;

        public string Marker
        {
            get => _change.ConflictMarker;
        }

        public string Description
        {
            get
            {
                var reason = _change.ConflictReason;
                if (_isRebase)
                    reason = reason switch {
                        Models.ConflictReason.AddedByUs => Models.ConflictReason.AddedByThem,
                        Models.ConflictReason.AddedByThem => Models.ConflictReason.AddedByUs,
                        Models.ConflictReason.DeletedByUs => Models.ConflictReason.DeletedByThem,
                        Models.ConflictReason.DeletedByThem => Models.ConflictReason.DeletedByUs,
                        _ => reason,
                    };
                return reason == Models.ConflictReason.None ? string.Empty : App.Text("Conflict.State." + reason);
            }
        }

        public Models.ConflictFileState State
        {
            get => _state;
            private set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(CanOpenMergeEditor)); }
        }

        public bool CanOpenMergeEditor => _state is Models.ConflictFileState.UnmergedText or Models.ConflictFileState.Resolved || _canEditStructuralConflict;

        public object Theirs
        {
            get => _theirs;
            private set => SetProperty(ref _theirs, value);
        }

        public object Mine
        {
            get => _mine;
            private set => SetProperty(ref _mine, value);
        }

        public Conflict(Repository repo, WorkingCopy wc, Models.Change change)
        {
            _repo = repo;
            _wc = wc;
            Refresh(change);
        }

        public void Refresh(Models.Change change)
        {
            _change = change;
            _isRebase = InProgressContext.IsRebasing(_repo.GitDir);
            OnPropertyChanged(nameof(Marker));
            OnPropertyChanged(nameof(Description));

            var version = ++_refreshVersion;
            var inProgress = _wc.InProgressContext;

            Task.Run(async () =>
            {
                var head = new Commands.QuerySingleCommit(_repo.FullPath, "HEAD").GetResult();

                var (mine, theirs) = inProgress switch
                {
                    CherryPickInProgress cherryPick => (head, cherryPick.Head),
                    RebaseInProgress rebase => (rebase.StoppedAt, head),
                    RevertInProgress => (head, (object)App.Text("Conflict.RevertVersion")),
                    MergeInProgress merge => (head, merge.Source),
                    _ => (head, (object)App.Text("Conflict.StashOrPatch")),
                };

                var state = Models.ConflictFileState.Unknown;
                if ((change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified) && !Directory.Exists(Path.Combine(_repo.FullPath, change.Path)))
                    state = await new Commands.QueryConflictFileState(_repo.FullPath, change)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                var canEditStructural = false;
                if (state == Models.ConflictFileState.Unknown)
                {
                    try
                    {
                        var path = Path.Combine(_repo.FullPath, change.Path);
                        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length <= Models.MergeConflictTextFile.MaxBytes)
                        {
                            Models.MergeConflictTextFile.Read(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
                            canEditStructural = true;
                        }
                    }
                    catch { /* Keep the existing side-selection flow for non-text conflicts. */ }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _refreshVersion)
                        return;

                    _head = head;
                    _canEditStructuralConflict = canEditStructural;
                    State = state;
                    OnPropertyChanged(nameof(CanOpenMergeEditor));
                    Mine = mine;
                    Theirs = theirs;
                });
            });
        }

        public async Task UseTheirsAsync()
        {
            if (_isRebase) await _wc.UseMineAsync([_change]);
            else await _wc.UseTheirsAsync([_change]);
        }

        public async Task UseMineAsync()
        {
            if (_isRebase) await _wc.UseTheirsAsync([_change]);
            else await _wc.UseMineAsync([_change]);
        }

        public MergeConflictEditor CreateOpenMergeEditorRequest()
        {
            return CanOpenMergeEditor ? new MergeConflictEditor(_repo, _head, _change.Path) : null;
        }

        public async Task MergeExternalAsync()
        {
            if (_state == Models.ConflictFileState.UnmergedText)
                await _wc.UseExternalMergeToolAsync(_change);
        }

        private Repository _repo = null;
        private WorkingCopy _wc = null;
        private Models.Change _change = null;
        private Models.Commit _head = null;
        private Models.ConflictFileState _state = Models.ConflictFileState.Unknown;
        private bool _canEditStructuralConflict;
        private object _mine = null;
        private object _theirs = null;
        private bool _isRebase;
        private int _refreshVersion;
    }
}
