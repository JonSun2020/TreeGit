using System;
using System.Collections.Generic;

namespace SourceGit.Commands
{
    public class QueryStagedChangesWithAmend : Command
    {
        public QueryStagedChangesWithAmend(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public List<Models.Change> GetResult()
        {
            Args = "show --no-show-signature --format=\"%H %P\" -s HEAD";
            var rs = ReadToEnd();
            if (!rs.IsSuccess)
                return [];

            var shas = rs.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (shas.Length == 0)
                return [];

            var parent = shas.Length > 1 ? shas[1] : Models.EmptyTreeHash.Guess(shas[0]);
            Args = $"diff-index --cached -M -z {parent}";
            rs = ReadToEnd();
            if (!rs.IsSuccess)
                return [];

            var changes = new List<Models.Change>();
            var fields = rs.StdOut.Split('\0');
            for (var index = 0; index < fields.Length;)
            {
                var header = fields[index++];
                if (header.Length == 0) continue;
                if (header[0] != ':') return [];
                var details = header[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (details.Length != 5 || details[4].Length == 0 || index >= fields.Length) return [];
                var type = details[4][0];
                var firstPath = fields[index++];
                var originalPath = string.Empty;
                var path = firstPath;
                if (type is 'R' or 'C')
                {
                    if (index >= fields.Length) return [];
                    originalPath = firstPath;
                    path = fields[index++];
                }
                var state = type switch
                {
                    'A' => Models.ChangeState.Added,
                    'D' => Models.ChangeState.Deleted,
                    'M' => Models.ChangeState.Modified,
                    'T' => Models.ChangeState.TypeChanged,
                    'R' => Models.ChangeState.Renamed,
                    'C' => Models.ChangeState.Copied,
                    _ => Models.ChangeState.None,
                };
                if (state == Models.ChangeState.None) continue;
                changes.Add(new Models.Change
                {
                    Path = path,
                    OriginalPath = originalPath,
                    Index = state,
                    DataForAmend = new Models.ChangeDataForAmend
                    {
                        FileMode = details[0],
                        ObjectHash = details[2],
                        ParentSHA = parent,
                    },
                });
            }

            return changes;
        }
    }
}
