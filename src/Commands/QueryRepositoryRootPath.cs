using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRepositoryRootPath : Command
    {
        public QueryRepositoryRootPath(string path)
        {
            WorkingDirectory = path;
            Args = "rev-parse --show-toplevel";
        }

        public Result GetResult()
        {
            return ReadToEnd(retrySilentFailure: true);
        }

        public async Task<Result> GetResultAsync()
        {
            return await ReadToEndAsync(retrySilentFailure: true).ConfigureAwait(false);
        }
    }
}
