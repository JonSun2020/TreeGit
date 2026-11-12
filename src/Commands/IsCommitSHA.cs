using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsCommitSHA : Command
    {
        public bool Succeeded { get; private set; }
        public IsCommitSHA(string repo, string hash)
        {
            WorkingDirectory = repo;
            Args = $"cat-file -t -- {hash.Quoted()}";
        }

        public async Task<bool> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            Succeeded = rs.IsSuccess;
            return rs.IsSuccess && rs.StdOut.Trim().Equals("commit");
        }
    }
}
