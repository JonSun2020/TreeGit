using System;
using System.Collections.Generic;

namespace SourceGit.Commands
{
    public sealed class QueryBisectRefs : Command
    {
        public QueryBisectRefs(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = "for-each-ref --format=\"%(refname)%00%(objectname)\" refs/bisect/";
        }

        public Dictionary<string, string> GetResult()
        {
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = ReadToEnd();
            if (!result.IsSuccess) return refs;
            foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\0', 2);
                if (parts.Length == 2 && parts[0].StartsWith("refs/bisect/", StringComparison.Ordinal))
                    refs[parts[0][12..]] = parts[1];
            }
            return refs;
        }
    }
}
