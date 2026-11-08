using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class Revert : CommitAction
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

        public Revert(Repository repo, Models.Commit target) : base(repo)
        {
            _repo = repo;
            Target = target;
            AutoCommit = true;
        }

        public override async Task<bool> Sure()
        {
            ProgressDescription = $"Revert commit '{Target.SHA}' ...";
            return await RunGitActionAsync($"Revert '{Target.SHA}'", log => new Commands.Revert(_repo.FullPath, Target.SHA, AutoCommit)
                .Use(log)
                .ExecAsync(), [(Target.SHA, 0)]);
        }

        private readonly Repository _repo = null;
    }
}
