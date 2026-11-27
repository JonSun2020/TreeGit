using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SourceGit.Models
{
    public sealed class CommitBranchDetails
    {
        public Commit Commit { get; }
        public SearchBranchCoverage Coverage { get; }
        public Branch Primary { get; }
        public bool IsInferred { get; }
        public int ExtraCount => Coverage.Count - (Primary != null && Coverage.Branches.Exists(b => b.FullName == Primary.FullName) ? 1 : 0);
        public bool HasMore => ExtraCount > 0;
        public bool HasPrimary => Primary != null;
        public string Description => Primary == null ? App.Text("CommitBranches.Unknown") :
            App.Text(IsInferred ? "CommitBranches.Estimated" : "CommitBranches.LocalRecord", Primary.FriendlyName);
        public Commit DisplayCommit { get; }

        internal static CommitBranchDetails FromSnapshot(Commit commit, SearchBranchCoverage coverage, List<Commit> graph,
            List<Branch> locals, IReadOnlyDictionary<string, string> origins, Dictionary<string, int> graphPositions = null)
        {
            var origin = origins.TryGetValue(commit.SHA, out var reference) ? locals.Find(branch => branch.FullName == reference) : null;
            return new CommitBranchDetails(commit, coverage, graph, origin, graphPositions);
        }

        public CommitBranchDetails(Commit commit, SearchBranchCoverage coverage, List<Commit> graph, Branch recordedOrigin,
            Dictionary<string, int> graphPositions = null)
        {
            graphPositions ??= IndexGraph(graph);
            var graphCommit = graphPositions.TryGetValue(commit.SHA, out var position) ? graph[position] : commit;
            Primary = recordedOrigin ?? ChooseGraphBranch(graphCommit, graph, coverage.Branches, graphPositions);
            IsInferred = recordedOrigin == null;
            Coverage = new SearchBranchCoverage(coverage.Branches, Primary, IsInferred);
            Commit = new Commit { SHA = commit.SHA, Subject = commit.Subject, SearchBranches = Coverage };
            DisplayCommit = new Commit { SHA = commit.SHA, Color = graphCommit.Color };
            if (Primary != null)
                DisplayCommit.Decorators.Add(new Decorator
                {
                    Type = Primary.IsCurrent && Primary.Head == commit.SHA && commit.IsCurrentHead ? DecoratorType.CurrentBranchHead :
                        Primary.IsLocal ? DecoratorType.LocalBranchHead : DecoratorType.RemoteBranchHead,
                    Name = Primary.FriendlyName,
                });
        }

        internal static Dictionary<string, int> IndexGraph(List<Commit> graph) => _graphPositions.GetValue(graph, static commits =>
        {
            var positions = new Dictionary<string, int>(commits.Count, StringComparer.Ordinal);
            for (var i = 0; i < commits.Count; i++) positions[commits[i].SHA] = i;
            return positions;
        });

        private static readonly ConditionalWeakTable<List<Commit>, Dictionary<string, int>> _graphPositions = new();

        internal static Branch ChooseGraphBranch(Commit commit, List<Commit> graph, List<Branch> containing, Dictionary<string, int> positions)
        {
            if (containing.Count <= 1) return containing.Count == 0 ? null : containing[0];
            var firstHead = containing[0].Head;
            var competingHeads = containing.Exists(branch => branch.Head != firstHead);

            bool SameLine(Branch branch)
            {
                if (!positions.TryGetValue(branch.Head, out var position) || graph[position].Color != commit.Color) return false;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var sha = branch.Head;
                while (visited.Add(sha))
                {
                    if (sha == commit.SHA) return true;
                    if (!positions.TryGetValue(sha, out var index) || graph[index].Parents.Count == 0) return false;
                    sha = graph[index].Parents[0];
                }
                return false;
            }
            Branch selected = null;
            (int Match, int Local, int Current, int Position) best = (-1, -1, -1, int.MinValue);
            foreach (var branch in containing)
            {
                // Refs at the same tip have identical ancestry and graph position.
                var rank = (branch.Head == commit.SHA ? 3 : competingHeads && SameLine(branch) ? 2 : 0,
                    branch.IsLocal ? 1 : 0, branch.IsCurrent ? 1 : 0,
                    positions.TryGetValue(branch.Head, out var index) ? -index : int.MinValue);
                if (selected == null || rank.CompareTo(best) > 0) { selected = branch; best = rank; }
            }
            if (selected is { IsLocal: false })
            {
                var tracking = containing.FindAll(b => b.IsLocal && !b.IsDetachedHead && b.Upstream == selected.FullName);
                return tracking.Find(b => b.IsCurrent) ?? tracking.Find(b => b.Head == selected.Head) ??
                    (tracking.Count > 0 ? tracking[0] : selected);
            }
            return selected;
        }
    }
}
