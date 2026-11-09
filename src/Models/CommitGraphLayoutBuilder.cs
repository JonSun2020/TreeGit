using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;

namespace SourceGit.Models
{
    public partial class CommitGraph
    {
        public const double ColumnWidth = 11;
        public const double FirstColumnX = 6;
        public const double DefaultRowHeight = 19;

        // Each row has an incoming half and an outgoing half. Lane compaction
        // happens above the node; newly merged ancestry fans out below it.
        // The focused branch keeps a reserved first lane throughout the history.
        public static CommitGraph Generate(List<Commit> commits, bool firstParentOnlyEnabled,
            CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, string preferredHead = null, IReadOnlyList<string> pendingParents = null)
        {
            if (pendingParents is { Count: > 0 })
                return GenerateWithWorkingCopy(commits, firstParentOnlyEnabled, highlighting, highlightExtraCommits, pendingParents);
            if (highlighting != CommitGraphHighlighting.All)
                return GenerateLegacy(commits, firstParentOnlyEnabled, highlighting, highlightExtraCommits, preferredHead);

            var graph = new CommitGraph();
            var byHash = new Dictionary<string, Commit>(StringComparer.Ordinal);
            foreach (var commit in commits) byHash.TryAdd(commit.SHA, commit);
            var focused = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(preferredHead) && byHash.TryGetValue(preferredHead, out var head) && focused.Add(preferredHead))
                preferredHead = head.Parents.Count > 0 ? head.Parents[0] : null;

            var reserveFirst = focused.Count > 0;
            var incoming = new List<RowLane>();
            var previousColors = new HashSet<int>();
            var maxColumn = 0;

            int ChooseColor(HashSet<int> occupied)
            {
                var begin = reserveFirst && Pens.Count > 1 ? 1 : 0;
                for (var color = begin; color < Pens.Count; color++)
                    if (!occupied.Contains(color) && !previousColors.Contains(color)) return color;
                for (var color = begin; color < Pens.Count; color++)
                    if (!occupied.Contains(color)) return color;
                return begin;
            }

            RowLane NewLane(string next, int column, int color, bool primary, Point start)
            {
                var path = new Path(color, true);
                path.Points.Add(start);
                graph.Paths.Add(path);
                return new RowLane { Next = next, Column = column, Color = color, Primary = primary, Path = path };
            }

            for (var row = 0; row < commits.Count; row++)
            {
                var commit = commits[row];
                var centerY = row + 0.5;
                var primary = focused.Contains(commit.SHA);
                var nodeColumn = primary ? 0 : -1;
                var nextColumn = reserveFirst ? 1 : 0;
                var outgoing = new List<RowLane>();
                var matched = new List<RowLane>();
                var occupied = incoming.Select(l => l.Color).ToHashSet();
                if (reserveFirst) occupied.Add(0);
                var continuingIncoming = commit.Parents.Count == 0 ? null : incoming
                    .OrderBy(l => l.Column)
                    .FirstOrDefault(l => l.Next == commit.SHA && (!primary || l.Primary));

                foreach (var lane in incoming.OrderBy(l => l.Column))
                {
                    var targetNode = lane.Next == commit.SHA;
                    int column;
                    if (targetNode)
                    {
                        if (nodeColumn < 0) nodeColumn = nextColumn++;
                        column = nodeColumn;
                        matched.Add(lane);
                    }
                    else
                    {
                        column = lane.Primary ? 0 : nextColumn++;
                        outgoing.Add(lane);
                    }

                    var x = FirstColumnX + column * ColumnWidth;
                    if (lane.Column != column)
                        AppendPoint(lane.Path, new Point(FirstColumnX + lane.Column * ColumnWidth, row + 2.0 / DefaultRowHeight));
                    var endY = targetNode && lane != continuingIncoming ? centerY + 1.0 / DefaultRowHeight : centerY;
                    AppendPoint(lane.Path, new Point(x, endY));
                    lane.Column = column;
                    maxColumn = Math.Max(maxColumn, column);
                }

                if (nodeColumn < 0) nodeColumn = nextColumn++;
                var nodeColor = primary ? 0 : matched.Count > 0 ? matched[0].Color : ChooseColor(occupied);
                occupied.Add(nodeColor);
                var node = new Point(FirstColumnX + nodeColumn * ColumnWidth, centerY);
                commit.IsHighlightedInGraph = true;
                commit.Color = nodeColor;
                graph.Dots.Add(new Dot
                {
                    Center = node,
                    Color = nodeColor,
                    IsHighlighted = true,
                    Type = commit.IsCurrentHead ? DotType.Head : commit.Parents.Count > 1 ? DotType.Merge : DotType.Default,
                });
                maxColumn = Math.Max(maxColumn, nodeColumn);

                foreach (var lane in outgoing)
                    AppendPoint(lane.Path, new Point(FirstColumnX + lane.Column * ColumnWidth, row + 1));

                if (commit.Parents.Count > 0)
                {
                    var continuation = matched.Find(l => l.Color == nodeColor && (!primary || l.Primary))
                        ?? NewLane(commit.Parents[0], nodeColumn, nodeColor, primary, node);
                    continuation.Next = commit.Parents[0];
                    continuation.Primary = primary;
                    continuation.Column = nodeColumn;
                    AppendPoint(continuation.Path, new Point(node.X, row + 1));
                    outgoing.Add(continuation);

                    if (!firstParentOnlyEnabled)
                    {
                        foreach (var parent in commit.Parents.Skip(1).Distinct())
                        {
                            if (parent == commit.Parents[0]) continue;
                            var shared = outgoing.Find(l => l.Next == parent);
                            if (shared != null)
                            {
                                if (shared.Column == nodeColumn) continue;
                                var link = new Path(shared.Color, true);
                                link.Points.Add(new Point(node.X, centerY - 0.5 / DefaultRowHeight));
                                graph.Paths.Add(link);
                                AppendPoint(link, new Point(FirstColumnX + shared.Column * ColumnWidth, row + 1 - 2.0 / DefaultRowHeight));
                                AppendPoint(link, new Point(FirstColumnX + shared.Column * ColumnWidth, row + 1));
                            }
                            else
                            {
                                var color = ChooseColor(occupied);
                                occupied.Add(color);
                                var lane = NewLane(parent, nextColumn++, color, false, new Point(node.X, centerY - 0.5 / DefaultRowHeight));
                                var x = FirstColumnX + lane.Column * ColumnWidth;
                                AppendPoint(lane.Path, new Point(x, row + 1 - 2.0 / DefaultRowHeight));
                                AppendPoint(lane.Path, new Point(x, row + 1));
                                outgoing.Add(lane);
                                maxColumn = Math.Max(maxColumn, lane.Column);
                            }
                        }
                    }
                }

                previousColors = occupied;
                incoming = outgoing;
            }

            var descriptionLeft = Math.Max(20, FirstColumnX + maxColumn * ColumnWidth + 8);
            foreach (var commit in commits) commit.LeftMargin = descriptionLeft;
            return graph;
        }

        public bool HasWorkingCopyNode { get; private set; }

        private static CommitGraph GenerateWithWorkingCopy(List<Commit> commits, bool firstParentOnly,
            CommitGraphHighlighting highlighting, HashSet<string> extraHeads, IReadOnlyList<string> parents)
        {
            // Pending merges use the normal lane allocator, so incoming edges
            // cannot run straight through unrelated branch nodes. This temporary
            // layout node never enters the repository's commit list or APIs.
            var pending = new Commit { SHA = "treegit-working-copy", Parents = parents.Distinct().ToList(), IsMerged = true };
            var layoutCommits = new List<Commit>(commits.Count + 1) { pending };
            layoutCommits.AddRange(commits);
            var graph = Generate(layoutCommits, firstParentOnly, highlighting, extraHeads, pending.SHA);
            graph.HasWorkingCopyNode = true;
            graph.Dots.RemoveAt(0);
            foreach (var dot in graph.Dots) dot.Center = new Point(dot.Center.X, dot.Center.Y - 1);
            foreach (var path in graph.Paths)
                for (var i = 0; i < path.Points.Count; i++)
                    path.Points[i] = new Point(path.Points[i].X, path.Points[i].Y - 1);
            foreach (var link in graph.Links)
            {
                link.Start = new Point(link.Start.X, link.Start.Y - 1);
                link.Control = new Point(link.Control.X, link.Control.Y - 1);
                link.End = new Point(link.End.X, link.End.Y - 1);
            }
            return graph;
        }

        private static void AppendPoint(Path path, Point point)
        {
            var points = path.Points;
            if (points.Count > 0 && points[^1] == point) return;
            if (points.Count > 1 && points[^1].X == point.X && points[^2].X == point.X)
                points[^1] = point;
            else
                points.Add(point);
        }

        private sealed class RowLane
        {
            public string Next;
            public int Column;
            public int Color;
            public bool Primary;
            public Path Path;
        }
    }
}
