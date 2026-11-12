using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IsAncestor : Command
    {
        public bool Succeeded { get; private set; }

        public IsAncestor(string repo, string checkPoint, string endPoint)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = $"merge-base --is-ancestor {checkPoint} {endPoint}";
        }

        public async Task<bool> GetResultAsync()
        {
            Succeeded = false;
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            // Exit 1 is a valid negative answer; other exits are query failures.
            Succeeded = !CancellationToken.IsCancellationRequested && (rs.ExitCode is 0 or 1);
            return rs.IsSuccess;
        }
    }
}
