using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QueryRecentCommitMessages : Command
    {
        public bool Succeeded { get; private set; }
        public QueryRecentCommitMessages(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            Args = "log --date-order -10 --no-color --no-show-signature --no-decorate --format=%ct%x00%B%x00 HEAD --";
        }

        public async Task<List<Models.RecentCommitMessage>> GetResultAsync()
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            Succeeded = result.IsSuccess;
            if (!Succeeded)
            {
                // Confirm an unborn branch; failures must not become empty history.
                var head = await new MetadataQuery(WorkingDirectory, "symbolic-ref --quiet HEAD", CancellationToken).GetAsync();
                if (head.ok && head.text.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    var branch = await new MetadataQuery(WorkingDirectory, "for-each-ref --format=%(objectname) " + head.text.Quoted(), CancellationToken).GetAsync();
                    Succeeded = branch.ok && string.IsNullOrWhiteSpace(branch.text);
                }
                return [];
            }
            var fields = result.StdOut.Split('\0');
            var messages = new List<Models.RecentCommitMessage>();
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                if (!long.TryParse(fields[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var time))
                { Succeeded = false; return []; }
                messages.Add(new(fields[i + 1].TrimEnd('\r', '\n'), time));
            }
            return messages.OrderByDescending(message => message.CommitTime).Take(10).ToList();
        }

        private sealed class MetadataQuery : Command
        {
            public MetadataQuery(string repo, string args, System.Threading.CancellationToken token)
            { WorkingDirectory = repo; Args = args; RaiseError = false; CancellationToken = token; }
            public async Task<(bool ok, string text)> GetAsync()
            {
                var result = await ReadToEndAsync().ConfigureAwait(false);
                return (result.IsSuccess, result.StdOut.Trim());
            }
        }
    }
}
