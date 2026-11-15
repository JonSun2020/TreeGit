using System.Collections.Generic;

namespace SourceGit.Models
{
    // A snapshot of actual containing refs; upstream configuration alone is not containment.
    public class SearchBranchCoverage : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public static readonly SearchBranchCoverage Empty = new([]);
        public List<Branch> Branches { get; }
        public Branch Primary { get; }
        public bool IsInferred { get; }
        public int Count => Branches.Count;
        public string PrimaryName => Primary?.FriendlyName ?? App.Text("TreeGit.Branches.None");
        public string MoreLabel => Count > 1 ? $"＋{Count - 1}" : string.Empty;
        public string Description => (Primary == null ? string.Empty :
            App.Text(IsInferred ? "CommitBranches.Estimated" : "CommitBranches.LocalRecord", Primary.FriendlyName) + "\n") +
            (Count > 0 ? App.Text("TreeGit.Branches.ShowAll", Count) : App.Text("TreeGit.Branches.NotContained"));

        public SearchBranchCoverage(List<Branch> branches, Branch primary = null, bool isInferred = true)
        {
            Branches = branches;
            Primary = primary;
            IsInferred = isInferred;
        }

        // Only actual containing refs are painted. Tracking identifies the local/remote pair,
        // but must never invent containment for a diverged or rewound branch.
        public bool IsPrimaryBranch(Branch branch) => Primary != null && branch != null &&
            (branch.FullName == Primary.FullName ||
             (Primary.IsLocal && !branch.IsLocal && Primary.Upstream == branch.FullName) ||
             (!Primary.IsLocal && branch.IsLocal && branch.Upstream == Primary.FullName));

        public void RefreshLocalizedText()
        {
            OnPropertyChanged(nameof(PrimaryName));
            OnPropertyChanged(nameof(Description));
        }
    }
}
