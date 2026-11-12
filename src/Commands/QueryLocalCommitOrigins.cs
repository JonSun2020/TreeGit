using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Reflogs are local evidence, not metadata carried by the commit object.
    // Bound both the number and formatted size of records; missing evidence is inferred in the UI.
    public sealed class QueryLocalCommitOrigins : Command
    {
        public QueryLocalCommitOrigins(string repo)
        {
            WorkingDirectory = repo;
            RaiseError = false;
            Args = "-c log.showSignature=false log -g --branches --no-decorate --no-notes --no-color -n 2048 " +
                "--format=\"%H%x00%<(256,trunc)%gD%x00%<(80,trunc)%gs\"";
        }

        public async Task<Dictionary<string, string>> GetResultAsync(HashSet<string> localRefs)
        {
            var result = await ReadToEndAsync().ConfigureAwait(false);
            var origins = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!result.IsSuccess) return origins;
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\0');
                if (fields.Length != 3) continue;
                var action = fields[2].Trim();
                if (!(action.StartsWith("commit:", StringComparison.Ordinal) || action.StartsWith("commit (", StringComparison.Ordinal) ||
                      action.StartsWith("am:", StringComparison.Ordinal) || action.StartsWith("cherry-pick:", StringComparison.Ordinal) || action.StartsWith("revert:", StringComparison.Ordinal))) continue;
                var selector = fields[1].Trim();
                var offset = selector.LastIndexOf("@{", StringComparison.Ordinal);
                if (offset <= 0 || ambiguous.Contains(fields[0])) continue;
                var reference = selector[..offset];
                if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) reference = "refs/heads/" + reference;
                if (!localRefs.Contains(reference)) continue;
                if (origins.TryGetValue(fields[0], out var previous) && previous != reference)
                {
                    origins.Remove(fields[0]); ambiguous.Add(fields[0]);
                }
                else origins[fields[0]] = reference;
            }
            return origins;
        }
    }
}
