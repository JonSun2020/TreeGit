using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteMultipleTags : Popup
    {
        public List<Models.Tag> Tags
        {
            get;
        }

        public bool DeleteFromRemote
        {
            get;
            set;
        } = false;

        public DeleteMultipleTags(Repository repo, List<Models.Tag> tags)
        {
            _repo = repo;
            Tags = tags;
        }

        public override Task<bool> VerifyCredentialsAsync() => DeleteFromRemote ? VerifyRemotesAsync(_repo.FullPath, _repo.Remotes, true) : Task.FromResult(true);
        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.DeleteMultipleTags");

            var log = _repo.CreateLog("Delete Multiple Tags");
            Use(log);

            var success = true;
            var remoteCount = DeleteFromRemote ? _repo.Remotes.Count : 0;
            var steps = Tags.Count * (1 + remoteCount);
            var index = 0;
            foreach (var tag in Tags)
            {
                BeginOperationPart(index++, steps);
                var succ = await new Commands.Tag(_repo.FullPath, tag.Name)
                .Use(log)
                .DeleteAsync();

                if (succ && DeleteFromRemote && _repo.Remotes is { Count: > 0 } remotes)
                {
                    var fullname = $"refs/tags/{tag.Name}";
                    foreach (var r in remotes)
                    {
                        BeginOperationPart(index++, steps);
                        succ = await new Commands.Push(_repo.FullPath, r, fullname, true)
                            .Use(log)
                            .ExecAsync();
                        if (!succ) break;
                    }
                }
                success &= succ;
                if (!success) break;
            }

            log.Complete();
            _repo.MarkTagsDirtyManually();
            return success;
        }

        private readonly Repository _repo;
    }
}
