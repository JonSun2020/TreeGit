using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QuerySymbolicHead : Command
    {
        public QuerySymbolicHead(string repo) { WorkingDirectory = repo; Args = "symbolic-ref --quiet HEAD"; }
        public Result GetResult() => ReadToEnd();
        public Task<Result> GetResultAsync() => ReadToEndAsync();
    }
}
