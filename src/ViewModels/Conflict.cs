using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Conflict : ObservableObject
    {
        public WorkingCopy WorkingCopy => _wc;

        public string Marker
        {
            get => _change.ConflictMarker;
        }

        public string Description
        {
            get => _change.ConflictDesc;
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
            _change = change;
            _isRebase = InProgressContext.IsRebasing(repo.GitDir);

            Task.Run(async () =>
            {
                _head = new Commands.QuerySingleCommit(repo.FullPath, "HEAD").GetResult();

                var (mine, theirs) = wc.InProgressContext switch
                {
                    CherryPickInProgress cherryPick => (_head, cherryPick.Head),
                    RebaseInProgress rebase => (rebase.StoppedAt, _head),
                    RevertInProgress revert => (_head, revert.Head),
                    MergeInProgress merge => (_head, merge.Source),
                    _ => (_head, (object)"Stash or Patch"),
                };

                var state = Models.ConflictFileState.Unknown;
                if ((_change.ConflictReason is Models.ConflictReason.BothAdded or Models.ConflictReason.BothModified) && !Directory.Exists(Path.Combine(_repo.FullPath, _change.Path)))
                    state = await new Commands.QueryConflictFileState(repo.FullPath, change)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                var canEditStructural = false;
                if (state == Models.ConflictFileState.Unknown)
                {
                    try
                    {
                        var path = Path.Combine(repo.FullPath, change.Path);
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
        private readonly bool _isRebase;
    }
}
