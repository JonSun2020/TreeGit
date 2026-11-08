using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QueryStashHead : Command
    {
        public QueryStashHead(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "rev-parse --verify --quiet refs/stash";
        }

        public Task<Result> GetResultAsync() => ReadToEndAsync();
    }
}
