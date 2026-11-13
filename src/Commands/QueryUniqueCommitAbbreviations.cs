using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public sealed class QueryUniqueCommitAbbreviations : Command
    {
        public QueryUniqueCommitAbbreviations(string repo, IEnumerable<string> revisions)
        {
            WorkingDirectory = repo;
            Context = repo;
            _revisions = revisions.Distinct(StringComparer.Ordinal).ToArray();
            if (_revisions.Any(sha => sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit)))
                throw new ArgumentException("Expected complete commit IDs.");
            // Git checks uniqueness against the complete object database,
            // including unreachable objects, and extends ten characters as needed.
            Args = "--no-replace-objects log --no-walk=unsorted --no-show-signature --no-notes --no-decorate --format=\"%H %h\" --abbrev=10 --end-of-options " + string.Join(' ', _revisions);
        }

        public async Task<Dictionary<string, string>> GetResultAsync()
        {
            var abbreviations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_revisions.Length == 0) return abbreviations;
            var result = await ReadToEndAsync().ConfigureAwait(false);
            if (!result.IsSuccess) throw new IOException("Could not determine unique commit abbreviations.");
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !_revisions.Contains(parts[0], StringComparer.Ordinal) ||
                    parts[1].Length < 10 || parts[1].Length > parts[0].Length || !parts[0].StartsWith(parts[1], StringComparison.Ordinal) ||
                    !abbreviations.TryAdd(parts[0], parts[1]))
                    throw new IOException("Unexpected commit abbreviation result.");
            }
            if (abbreviations.Count != _revisions.Length) throw new IOException("Incomplete commit abbreviation result.");
            return abbreviations;
        }

        private readonly string[] _revisions;
    }
}
