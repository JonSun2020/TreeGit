using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QueryCommitterIdentity : Command
    {
        public QueryCommitterIdentity(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            Args = "var GIT_COMMITTER_IDENT";
        }

        public async Task<string> GetResultAsync()
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            return result.IsSuccess ? Regex.Replace(result.StdOut.Trim(), @"\s+\d+\s+[+-]\d{4}$", string.Empty) : string.Empty;
        }
    }
}
