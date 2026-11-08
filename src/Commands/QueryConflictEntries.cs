using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QueryConflictEntries : Command
    {
        public QueryConflictEntries(string repo, string file)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--literal-pathspecs ls-files --unmerged --stage -- {file.Quoted()}";
        }

        public string GetResult()
        {
            var result = ReadToEnd();
            return result.IsSuccess ? result.StdOut : null;
        }

        public async Task<string> GetResultAsync()
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            return result.IsSuccess ? result.StdOut : null;
        }
    }
}
