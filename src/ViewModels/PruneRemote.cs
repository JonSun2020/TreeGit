using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class PruneRemote : Popup
    {
        public Models.Remote Remote
        {
            get;
        }

        public PruneRemote(Repository repo, Models.Remote remote)
        {
            _repo = repo;
            Remote = remote;
        }

        public override Task<bool> VerifyCredentialsAsync() => VerifyRemoteAsync(_repo.FullPath, Remote);
        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.PruneRemote");

            var log = _repo.CreateLog($"Prune Remote '{Remote.Name}'");
            Use(log);

            var succ = await new Commands.Remote(_repo.FullPath)
                .Use(log)
                .PruneAsync(Remote);

            log.Complete();
            return succ;
        }

        private readonly Repository _repo = null;
    }
}
