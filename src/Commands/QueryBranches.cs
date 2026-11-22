using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryBranches : Command
    {
        public bool Succeeded { get; private set; }
        protected override bool RequiresInvariantOutput => true;
        private const string PREFIX_LOCAL = "refs/heads/";
        private const string PREFIX_REMOTE = "refs/remotes/";
        private const string PREFIX_DETACHED_AT = "(HEAD detached at";
        private const string PREFIX_DETACHED_FROM = "(HEAD detached from";

        public QueryBranches(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "branch -l --all -v --format=\"%(refname)%00%(committerdate:unix)%00%(objectname)%00%(HEAD)%00%(upstream)%00%(upstream:track,nobracket)%00%(worktreepath)\"";
        }

        public async Task<List<Models.Branch>> GetResultAsync()
        {
            Succeeded = false;
            var branches = new List<Models.Branch>();
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return branches;

            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var needsTrackingQuery = new HashSet<Models.Branch>();
            var remotes = new Dictionary<string, Models.Branch>();
            foreach (var line in lines)
            {
                var b = ParseLine(line, needsTrackingQuery);
                if (b != null)
                {
                    branches.Add(b);
                    if (!b.IsLocal)
                        remotes.Add(b.FullName, b);
                }
            }

            foreach (var b in branches)
            {
                if (b.IsLocal && !string.IsNullOrEmpty(b.Upstream))
                {
                    if (remotes.TryGetValue(b.Upstream, out var upstream))
                    {
                        b.IsUpstreamGone = false;

                        // Only the current branch needs commit IDs for the graph and commit actions.
                        if (needsTrackingQuery.Contains(b) || b.IsCurrent && b.IsTrackStatusVisible)
                        {
                            var tracking = new QueryTrackStatus(WorkingDirectory) { CancellationToken = CancellationToken };
                            await tracking.GetResultAsync(b, upstream).ConfigureAwait(false);
                            if (!tracking.Succeeded) return branches;
                        }
                    }
                    else
                    {
                        b.IsUpstreamGone = true;
                        b.AheadCount = b.BehindCount = 0;
                    }
                }
            }

            Succeeded = true;
            return branches;
        }

        private static Models.Branch ParseLine(string line, HashSet<Models.Branch> needsTrackingQuery)
        {
            var parts = line.Split('\0');
            if (parts.Length != 7)
                return null;

            var branch = new Models.Branch();
            var refName = parts[0];
            if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                return null;

            if (refName.StartsWith(PREFIX_LOCAL, StringComparison.Ordinal))
            {
                branch.Name = refName.Substring(PREFIX_LOCAL.Length);
                branch.IsLocal = true;
                branch.IsDetachedHead = false;
            }
            else if (refName.StartsWith(PREFIX_REMOTE, StringComparison.Ordinal))
            {
                var name = refName.Substring(PREFIX_REMOTE.Length);
                var nameParts = name.Split('/', 2);
                if (nameParts.Length != 2)
                    return null;

                branch.Remote = nameParts[0];
                branch.Name = nameParts[1];
                branch.IsLocal = false;
                branch.IsDetachedHead = false;
            }
            else if (refName.StartsWith(PREFIX_DETACHED_AT, StringComparison.Ordinal) ||
                     refName.StartsWith(PREFIX_DETACHED_FROM, StringComparison.Ordinal))
            {
                branch.Name = "HEAD";
                branch.IsLocal = true;
                branch.IsDetachedHead = true;
            }
            else
            {
                return null;
            }

            ulong.TryParse(parts[1], out var committerDate);

            branch.FullName = refName;
            branch.CommitterDate = committerDate;
            branch.Head = parts[2];
            branch.IsCurrent = parts[3] == "*";
            branch.Upstream = parts[4];
            branch.IsUpstreamGone = false;

            if (branch.IsLocal && !string.IsNullOrEmpty(branch.Upstream) &&
                !TryParseTrackingCounts(parts[5], branch))
                needsTrackingQuery.Add(branch);

            branch.WorktreePath = parts[6];
            return branch;
        }

        private static bool TryParseTrackingCounts(ReadOnlySpan<char> status, Models.Branch branch)
        {
            if (status.IsEmpty)
                return true;

            if (status.StartsWith("ahead ", StringComparison.Ordinal))
            {
                var separator = status.IndexOf(", ", StringComparison.Ordinal);
                var count = separator < 0 ? status[6..] : status[6..separator];
                if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var ahead))
                    return false;
                branch.AheadCount = ahead;
                if (separator < 0)
                    return true;
                status = status[(separator + 2)..];
            }

            if (!status.StartsWith("behind ", StringComparison.Ordinal) ||
                !int.TryParse(status[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var behind))
                return false;
            branch.BehindCount = behind;
            return true;
        }
    }
}
