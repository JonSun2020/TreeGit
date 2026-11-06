using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryBranchesContainingCommit : Command
    {
        public QueryBranchesContainingCommit(string repo, string sha)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"for-each-ref --contains={sha.Quoted()} --format=%(refname) refs/heads refs/remotes";
        }

        public async Task<HashSet<string>> GetResultAsync()
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            return result.IsSuccess
                ? new HashSet<string>(result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
