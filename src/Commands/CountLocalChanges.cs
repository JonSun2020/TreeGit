using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class CountLocalChanges : Command
    {
        public bool Succeeded { get; private set; }
        public CountLocalChanges(string repo, bool includeUntracked)
        {
            var option = includeUntracked ? "-uall" : "-uno";
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--no-optional-locks status {option} --ignore-submodules=all --porcelain";
        }

        public async Task<int> GetResultAsync()
        {
            Succeeded = false;
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                Succeeded = true;
                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                return lines.Length;
            }

            return 0;
        }
    }
}
