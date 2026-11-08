using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CherryPick : CommitAction
    {
        public List<Models.Commit> Targets
        {
            get;
            private set;
        }

        public bool IsMergeCommit
        {
            get;
            private set;
        }

        public List<Models.Commit> ParentsForMergeCommit
        {
            get;
            private set;
        }

        public int MainlineForMergeCommit
        {
            get;
            set;
        }

        public bool AppendSourceToMessage
        {
            get;
            set;
        }

        public bool AutoCommit
        {
            get;
            set;
        }

        public CherryPick(Repository repo, List<Models.Commit> targets) : base(repo)
        {
            _repo = repo;
            Targets = targets;
            IsMergeCommit = false;
            ParentsForMergeCommit = [];
            MainlineForMergeCommit = 0;
            AppendSourceToMessage = true;
            AutoCommit = true;
        }

        public CherryPick(Repository repo, Models.Commit merge, List<Models.Commit> parents) : base(repo)
        {
            _repo = repo;
            Targets = [merge];
            IsMergeCommit = true;
            ParentsForMergeCommit = parents;
            MainlineForMergeCommit = 0;
            AppendSourceToMessage = true;
            AutoCommit = true;
        }

        public override async Task<bool> Sure()
        {
            ProgressDescription = "Cherry-Pick commit(s) ...";
            return await RunGitActionAsync("Cherry-Pick", RunAsync);
        }

        private async Task<bool> RunAsync(CommandLog log)
        {
            bool success;
            if (IsMergeCommit)
            {
                success = await new Commands.CherryPick(
                    _repo.FullPath,
                    Targets[0].SHA,
                    !AutoCommit,
                    AppendSourceToMessage,
                    $"-m {MainlineForMergeCommit + 1}")
                    .Use(log)
                    .ExecAsync();
            }
            else
            {
                success = await new Commands.CherryPick(
                    _repo.FullPath,
                    string.Join(' ', Targets.ConvertAll(c => c.SHA)),
                    !AutoCommit,
                    AppendSourceToMessage,
                    string.Empty)
                    .Use(log)
                    .ExecAsync();

                if (success && !AutoCommit && Targets.Count > 1)
                {
                    var builder = new StringBuilder();
                    foreach (var t in Targets)
                        builder.Append(t.Subject).Append("\n");

                    builder.Append("\n");
                    foreach (var t in Targets)
                        builder.Append("(cherry picked from commit ").Append(t.SHA).Append(")\n");

                    File.WriteAllText(Path.Combine(_repo.GitDir, "MERGE_MSG"), builder.ToString());
                }
            }

            return success;
        }

        private readonly Repository _repo = null;
    }
}
