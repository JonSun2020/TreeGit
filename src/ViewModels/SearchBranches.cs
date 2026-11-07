using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SearchBranchRow
    {
        public Models.Branch Branch { get; set; }
        public string Group { get; set; }
        public bool HasGroup => !string.IsNullOrEmpty(Group);
        public bool IsBranch => Branch != null;
    }

    public class SearchBranches : ObservableObject
    {
        public Models.Commit Commit { get; }
        public string Title => $"包含此提交的分支（{Commit.SearchBranches.Count}）";
        public string Filter
        {
            get => _filter;
            set { if (SetProperty(ref _filter, value ?? string.Empty)) UpdateRows(); }
        }
        public List<SearchBranchRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }
        public bool IsEmpty => Rows.Count == 0;
        public string EmptyText => Commit.SearchBranches.Count == 0 ? "没有已知分支包含此提交" : "没有匹配的分支";
        public event Action<Models.Branch> BranchChosen;

        public SearchBranches(Models.Commit commit)
        {
            Commit = commit;
            UpdateRows();
        }

        public void Choose(Models.Branch branch)
        {
            if (branch != null) BranchChosen?.Invoke(branch);
        }

        private void UpdateRows()
        {
            var matches = Commit.SearchBranches.Branches.FindAll(b => b.FriendlyName.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase));
            var rows = new List<SearchBranchRow>();
            foreach (var local in new[] { true, false })
            {
                var group = matches.FindAll(b => b.IsLocal == local);
                if (group.Count == 0) continue;
                rows.Add(new SearchBranchRow { Group = $"{(local ? "本地" : "远程")}（{group.Count}）" });
                for (var i = 0; i < group.Count; i++)
                    rows.Add(new SearchBranchRow { Branch = group[i] });
            }
            Rows = rows;
            OnPropertyChanged(nameof(IsEmpty));
        }

        private string _filter = string.Empty;
        private List<SearchBranchRow> _rows = [];
    }
}
