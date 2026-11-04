using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Revert : Popup
    {
        public Models.Commit Target
        {
            get;
        }

        public bool AutoCommit
        {
            get;
            set;
        }

        public Revert(Repository repo, Models.Commit target)
        {
            _repo = repo;
            Target = target;
            AutoCommit = true;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Revert commit '{Target.SHA}' ...";

            var log = _repo.CreateLog($"Revert '{Target.SHA}'");
            Use(log);

            var success = await new Commands.Revert(_repo.FullPath, Target.SHA, AutoCommit)
                .Use(log)
                .ExecAsync();

            if (success) _repo.ClearCommitMessage();

            log.Complete();
            return true;
        }

        private readonly Repository _repo = null;
    }
}
