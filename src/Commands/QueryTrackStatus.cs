using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryTrackStatus : Command
    {
        public bool Succeeded { get; private set; }
        public QueryTrackStatus(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task GetResultAsync(Models.Branch local, Models.Branch remote)
        {
            Succeeded = false;
            Args = $"rev-list --left-right {local.Head}...{remote.Head}";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return;
            Succeeded = true;

            local.AheadCount = local.BehindCount = 0;
            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line[0] == '>')
                {
                    local.BehindCount++;
                    if (local.IsCurrent) (local.BehindCommits ??= []).Add(line[1..]);
                }
                else
                {
                    local.AheadCount++;
                    if (local.IsCurrent) (local.AheadCommits ??= []).Add(line[1..]);
                }
            }
        }
    }
}
