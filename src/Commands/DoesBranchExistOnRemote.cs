using System.Threading.Tasks;

using System;
using System.Linq;

namespace SourceGit.Commands
{
    public class DoesBranchExistOnRemote : Command
    {
        protected override bool SupportsAuthenticationRetry => true;
        public bool Succeeded { get; private set; }
        public DoesBranchExistOnRemote(string repo, Models.Remote remote, Models.Branch branch)
        {
            WorkingDirectory = repo;
            SSHKey = remote.PrivateSSHKey;
            RaiseError = false;
            _reference = $"refs/heads/{branch.Name}";
            Args = $"ls-remote --heads -- {remote.Name.Quoted()} {_reference.Quoted()}";
        }

        public async Task<bool> GetResultAsync()
        {
            Succeeded = false;
            Log?.AppendLine(Native.RepositoryCredentials.RedactLog($"$ git {Args}\n"));
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            Succeeded = rs.IsSuccess;
            if (!string.IsNullOrWhiteSpace(rs.StdOut)) Log?.AppendLine(Native.RepositoryCredentials.RedactLog(rs.StdOut));
            if (!string.IsNullOrWhiteSpace(rs.StdErr)) Log?.AppendLine(Native.RepositoryCredentials.RedactLog(rs.StdErr));
            if (!rs.IsSuccess && !rs.AuthenticationCancelled)
                Log?.MarkFailed(Native.RepositoryCredentials.RedactLog(string.IsNullOrWhiteSpace(rs.StdErr) ? $"git {Args}" : rs.StdErr));
            return rs.IsSuccess && rs.StdOut.Split('\n').Any(line => line.TrimEnd('\r').EndsWith("\t" + _reference, StringComparison.Ordinal));
        }

        private readonly string _reference;
    }
}
