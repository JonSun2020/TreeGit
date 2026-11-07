using System.Collections.Generic;

namespace SourceGit.Models
{
    // A snapshot of actual containing refs; upstream configuration alone is not containment.
    public class SearchBranchCoverage
    {
        public static readonly SearchBranchCoverage Empty = new([]);
        public List<Branch> Branches { get; }
        public int Count => Branches.Count;
        public string PrimaryName { get; }
        public string MoreLabel => Count > 1 ? $"＋{Count - 1}" : string.Empty;
        public string Description => Count > 0 ? $"查看全部 {Count} 个包含此提交的分支" : "已知本地和远程分支均不包含此提交";

        public SearchBranchCoverage(List<Branch> branches)
        {
            Branches = branches;
            PrimaryName = branches.Find(b => b.IsCurrent)?.FriendlyName ?? (branches.Count > 0 ? branches[0].FriendlyName : "无分支");
        }
    }
}
