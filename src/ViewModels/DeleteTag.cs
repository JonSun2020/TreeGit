using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteTag : Popup
    {
        public Models.Tag Target
        {
            get;
            private set;
        }

        public bool PushToRemotes
        {
            get => _repo.UIStates.PushToRemoteWhenDeleteTag;
            set => _repo.UIStates.PushToRemoteWhenDeleteTag = value;
        }

        public DeleteTag(Repository repo, Models.Tag tag)
        {
            _repo = repo;
            Target = tag;
        }

        public override Task<bool> VerifyCredentialsAsync() => PushToRemotes ? VerifyRemotesAsync(_repo.FullPath, _repo.Remotes, true) : Task.FromResult(true);
        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.DeleteTag", Target.Name);

            var log = _repo.CreateLog("Delete Tag");
            Use(log);

            var succ = await new Commands.Tag(_repo.FullPath, Target.Name)
                .Use(log)
                .DeleteAsync();

            if (succ && PushToRemotes && _repo.Remotes is { Count: > 0 } remotes)
            {
                var fullname = $"refs/tags/{Target.Name}";
                var index = 0;
                foreach (var r in remotes)
                {
                    BeginOperationPart(++index, remotes.Count + 1);
                    succ = await new Commands.Push(_repo.FullPath, r, fullname, true)
                        .Use(log)
                        .ExecAsync();
                    if (!succ) break;
                }
            }

            log.Complete();
            _repo.UIStates.RemoveHistoryFilter(Target.Name, Models.FilterType.Tag);
            _repo.MarkTagsDirtyManually();
            return succ;
        }

        private readonly Repository _repo = null;
    }
}
