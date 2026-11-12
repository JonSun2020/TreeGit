using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public static class BranchSelection
    {
        public static Branch Choose(List<Branch> branches, string sha, HashSet<string> containing, bool preferLocal = false)
        {
            var exactRemotes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var branch in branches)
                if (!branch.IsLocal && branch.Head == sha) exactRemotes.Add(branch.FullName);

            Branch Pick(List<Branch> candidates)
            {
                int Rank(Branch branch) => (branch.Head == sha ? 4 : 0) +
                    (!string.IsNullOrEmpty(branch.Upstream) && exactRemotes.Contains(branch.Upstream) ? 2 : 0) + (branch.IsCurrent ? 1 : 0);
                candidates.Sort((a, b) =>
                {
                    var order = Rank(b).CompareTo(Rank(a));
                    return order != 0 ? order : StringComparer.Ordinal.Compare(a.FullName, b.FullName);
                });
                return candidates.Count > 0 ? candidates[0] : null;
            }

            var tracked = branches.FindAll(b => b.IsLocal && !b.IsDetachedHead && !string.IsNullOrEmpty(b.Upstream) &&
                (containing.Contains(b.FullName) || containing.Contains(b.Upstream)));
            var remotes = branches.FindAll(b => !b.IsLocal && containing.Contains(b.FullName));
            var locals = branches.FindAll(b => b.IsLocal && !b.IsDetachedHead && containing.Contains(b.FullName));
            if (preferLocal)
            {
                foreach (var branch in tracked)
                    if (!containing.Contains(branch.FullName)) locals.Add(branch);
                return Pick(locals) ?? Pick(remotes);
            }
            return Pick(tracked) ?? Pick(remotes) ?? Pick(locals);
        }

    }
}
