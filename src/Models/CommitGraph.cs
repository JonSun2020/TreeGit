using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Media;

namespace SourceGit.Models
{
    public record CommitGraphLayout(double StartY, double ClipWidth, double RowHeight);

    public enum CommitGraphHighlighting
    {
        All = 0,
        CurrentBranchOnly,
        SelectedCommitsOnly,
        CurrentBranchAndSelectedCommits,
        SelectedCommitsOnlyFirstParent,
    }

    public class CommitGraph
    {
        public static List<Pen> Pens { get; } = [];

        public static void SetDefaultPens(double thickness = 2)
        {
            SetPens(s_defaultPenColors, thickness);
        }

        public static void SetPens(List<Color> colors, double thickness)
        {
            Pens.Clear();

            foreach (var c in colors)
                Pens.Add(new Pen(c.ToUInt32(), thickness));

            s_penCount = colors.Count;
        }

        public class Path(int color, bool isHighlighted)
        {
            public List<Point> Points { get; } = [];
            public int Color { get; } = color;
            public bool IsHighlighted { get; } = isHighlighted;
        }

        public class Link
        {
            public Point Start;
            public Point Control;
            public Point End;
            public int Color;
            public bool IsHighlighted;
        }

        public enum DotType
        {
            Default,
            Head,
            Merge,
        }

        public class Dot
        {
            public DotType Type;
            public Point Center;
            public int Color;
            public bool IsHighlighted;
        }

        public List<Path> Paths { get; } = [];
        public List<Link> Links { get; } = [];
        public List<Dot> Dots { get; } = [];

        public static CommitGraph Generate(List<Commit> commits, bool firstParentOnlyEnabled, CommitGraphHighlighting highlighting, HashSet<string> highlightExtraCommits, string preferredHead = null)
        {
            const double unitWidth = 16;
            const double halfWidth = 8;
            const double unitHeight = 1;
            const double halfHeight = 0.5;

            var spine = new HashSet<string>(StringComparer.Ordinal);
            var lookup = new Dictionary<string, Commit>(StringComparer.Ordinal);
            foreach (var c in commits)
                lookup.TryAdd(c.SHA, c);
            var cursor = preferredHead;
            while (!string.IsNullOrEmpty(cursor) && lookup.TryGetValue(cursor, out var next) && spine.Add(cursor))
                cursor = next.Parents.Count > 0 ? next.Parents[0] : null;

            var temp = new CommitGraph();
            var unsolved = new List<PathHelper>();
            var ended = new List<PathHelper>();
            var offsetY = -halfHeight;
            var colorPicker = new ColorPicker(spine.Count > 0);
            var defHighlighting = highlighting == CommitGraphHighlighting.All;

            foreach (var commit in commits)
            {
                PathHelper major = null;
                var onSpine = spine.Contains(commit.SHA);
                if (onSpine)
                {
                    var preferred = unsolved.Find(x => x.Next == commit.SHA && x.IsPreferred) ?? unsolved.Find(x => x.Next == commit.SHA);
                    if (preferred != null)
                    {
                        preferred.IsPreferred = true;
                        unsolved.Remove(preferred);
                        unsolved.Insert(0, preferred);
                    }
                }

                // Update current y offset
                offsetY += unitHeight;

                // Find first curves that links to this commit and marks others that links to this commit ended.
                var offsetX = 4 - halfWidth;
                // Reserve column zero even when newer commits from other branches
                // precede the selected head. Do not draw a fabricated line above it.
                if (spine.Count > 0 && !unsolved.Exists(x => x.IsPreferred))
                    offsetX += unitWidth;
                var maxOffsetOld = unsolved.Count > 0 ? unsolved[^1].LastX : offsetX + unitWidth;
                var isHighlighted = defHighlighting;
                foreach (var l in unsolved)
                {
                    if (l.Next.Equals(commit.SHA, StringComparison.Ordinal))
                    {
                        if (major == null)
                        {
                            offsetX += unitWidth;
                            major = l;
                            isHighlighted = major.IsHighlighted;

                            if (commit.Parents.Count > 0)
                            {
                                major.Next = commit.Parents[0];
                                major.Goto(offsetX, offsetY, halfHeight);
                            }
                            else
                            {
                                major.End(offsetX, offsetY, halfHeight);
                                ended.Add(l);
                            }
                        }
                        else
                        {
                            l.End(major.LastX, offsetY, halfHeight);
                            ended.Add(l);

                            if (!isHighlighted && l.IsHighlighted)
                                isHighlighted = true;
                        }
                    }
                    else
                    {
                        offsetX += unitWidth;
                        l.Pass(offsetX, offsetY, halfHeight);
                    }
                }

                // Remove ended curves from unsolved
                foreach (var l in ended)
                {
                    if (spine.Count == 0 || l.Path.Color != 0)
                        colorPicker.Recycle(l.Path.Color);
                    unsolved.Remove(l);
                }
                ended.Clear();

                // Calculate highlighted state
                if (!isHighlighted)
                {
                    switch (highlighting)
                    {
                        case CommitGraphHighlighting.CurrentBranchOnly:
                            isHighlighted = commit.IsMerged;
                            break;
                        case CommitGraphHighlighting.SelectedCommitsOnly:
                        case CommitGraphHighlighting.SelectedCommitsOnlyFirstParent:
                            if (highlightExtraCommits.Remove(commit.SHA))
                            {
                                isHighlighted = true;
                                // Highlight first parent, other parents are dealt with later
                                if (commit.Parents.Count > 0)
                                    highlightExtraCommits.Add(commit.Parents[0]);
                            }
                            break;
                        default: // CommitGraphHighlighting.CurrentBranchAndSelectedCommits
                            if (commit.IsMerged)
                            {
                                isHighlighted = true;
                            }
                            else if (highlightExtraCommits.Remove(commit.SHA))
                            {
                                isHighlighted = true;
                                // Highlight first parent, other parents are dealt with later
                                if (commit.Parents.Count > 0)
                                    highlightExtraCommits.Add(commit.Parents[0]);
                            }
                            break;
                    }
                }
                commit.IsHighlightedInGraph = isHighlighted;

                // If no path found, create new curve for branch head
                // Otherwise, create new curve for new merged commit
                if (major == null)
                {
                    var nodeX = onSpine ? 4 + halfWidth : offsetX + unitWidth;
                    offsetX = Math.Max(offsetX, nodeX);

                    if (commit.Parents.Count > 0)
                    {
                        major = new PathHelper(commit.Parents[0], isHighlighted, onSpine ? 0 : colorPicker.Next(), new Point(nodeX, offsetY)) { IsPreferred = onSpine };
                        if (onSpine)
                            unsolved.Insert(0, major);
                        else
                            unsolved.Add(major);
                        temp.Paths.Add(major.Path);
                    }
                    // Root commits do not create a continuing path.
                    if (onSpine && major == null)
                        offsetX = nodeX;
                }
                else if (isHighlighted && !major.IsHighlighted && commit.Parents.Count > 0)
                {
                    major.Highlight();
                    temp.Paths.Add(major.Path);
                }

                // Calculate link position of this commit.
                if (onSpine && major != null && major.Path.Color != 0)
                {
                    colorPicker.Recycle(major.Path.Color);
                    major.SwitchColor(0);
                    temp.Paths.Add(major.Path);
                }
                var position = new Point(major?.LastX ?? offsetX, offsetY);
                var dotColor = major?.Path.Color ?? 0;
                var anchor = new Dot() { Center = position, Color = dotColor, IsHighlighted = isHighlighted };
                if (commit.IsCurrentHead)
                    anchor.Type = DotType.Head;
                else if (commit.Parents.Count > 1)
                    anchor.Type = DotType.Merge;
                else
                    anchor.Type = DotType.Default;
                temp.Dots.Add(anchor);

                // Deal with other parents (the first parent has been processed)
                if (!firstParentOnlyEnabled)
                {
                    if (highlighting == CommitGraphHighlighting.SelectedCommitsOnlyFirstParent)
                        isHighlighted = false;

                    for (int j = 1; j < commit.Parents.Count; j++)
                    {
                        var parentHash = commit.Parents[j];
                        var parent = unsolved.Find(x => x.Next.Equals(parentHash, StringComparison.Ordinal));
                        if (parent != null)
                        {
                            if (isHighlighted && !parent.IsHighlighted)
                            {
                                parent.Goto(parent.LastX, offsetY + halfHeight, halfHeight);
                                parent.Highlight();
                                temp.Paths.Add(parent.Path);
                            }

                            temp.Links.Add(new Link
                            {
                                Start = position,
                                End = new Point(parent.LastX, offsetY + halfHeight),
                                Control = new Point(parent.LastX, position.Y),
                                Color = parent.Path.Color,
                                IsHighlighted = isHighlighted,
                            });
                        }
                        else
                        {
                            offsetX += unitWidth;

                            // Create new curve for parent commit that not includes before
                            var l = new PathHelper(parentHash, isHighlighted, colorPicker.Next(), position, new Point(offsetX, position.Y + halfHeight));
                            unsolved.Add(l);
                            temp.Paths.Add(l.Path);
                        }
                    }
                }

                // Margins & colors (used by Views.Histories).
                commit.Color = dotColor;
                commit.LeftMargin = Math.Max(offsetX, maxOffsetOld) + halfWidth + 2;
            }

            // Deal with curves haven't ended yet.
            for (var i = 0; i < unsolved.Count; i++)
            {
                var path = unsolved[i];
                var endY = (commits.Count - 0.5) * unitHeight;

                if (path.Path.Points.Count == 1 && Math.Abs(path.Path.Points[0].Y - endY) < 0.0001)
                    continue;

                path.End(path.LastX, endY + halfHeight, halfHeight);
            }
            unsolved.Clear();

            // A fixed graph area keeps references and subjects aligned across rows.
            double subjectLeft = 24;
            foreach (var c in commits)
                subjectLeft = Math.Max(subjectLeft, c.LeftMargin);
            foreach (var c in commits)
                c.LeftMargin = subjectLeft;

            return temp;
        }

        private class ColorPicker(bool reservePrimary)
        {
            public int Next()
            {
                if (_colorsQueue.Count == 0)
                {
                    for (var i = reservePrimary && s_penCount > 1 ? 1 : 0; i < s_penCount; i++)
                        _colorsQueue.Enqueue(i);
                }

                return _colorsQueue.Dequeue();
            }

            public void Recycle(int idx)
            {
                if (!_colorsQueue.Contains(idx))
                    _colorsQueue.Enqueue(idx);
            }

            private Queue<int> _colorsQueue = new Queue<int>();
        }

        private class PathHelper
        {
            public Path Path { get; private set; }
            public string Next { get; set; }
            public double LastX { get; private set; }
            public bool IsHighlighted { get => Path.IsHighlighted; }
            public bool IsPreferred { get; set; }

            public PathHelper(string next, bool IsHighlighted, int color, Point start)
            {
                Next = next;
                LastX = start.X;
                _lastY = start.Y;

                Path = new Path(color, IsHighlighted);
                Path.Points.Add(start);
            }

            public PathHelper(string next, bool IsHighlighted, int color, Point start, Point to)
            {
                Next = next;
                LastX = to.X;
                _lastY = to.Y;

                Path = new Path(color, IsHighlighted);
                Path.Points.Add(start);
                Path.Points.Add(to);
            }

            /// <summary>
            ///     A path that just passed this row.
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void Pass(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    Add(LastX, y - halfHeight);
                    y += halfHeight;
                    Add(x, y);
                }

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     A path that has commit in this row but not ended
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void Goto(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    var minY = y - halfHeight;
                    if (minY > _lastY)
                        minY -= halfHeight;

                    Add(LastX, minY);
                    Add(x, y);
                }

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     A path that has commit in this row and end.
            /// </summary>
            /// <param name="x"></param>
            /// <param name="y"></param>
            /// <param name="halfHeight"></param>
            public void End(double x, double y, double halfHeight)
            {
                if (x > LastX)
                {
                    Add(LastX, _lastY);
                    Add(x, y - halfHeight);
                }
                else if (x < LastX)
                {
                    Add(LastX, y - halfHeight);
                }

                Add(x, y);

                LastX = x;
                _lastY = y;
            }

            /// <summary>
            ///     End the current path and create a new highlighted from the end.
            /// </summary>
            public void Highlight()
            {
                var color = Path.Color;
                Add(LastX, _lastY);

                Path = new Path(color, true);
                Path.Points.Add(new Point(LastX, _lastY));
                _endY = 0;
            }

            public void SwitchColor(int color)
            {
                Add(LastX, _lastY);
                Path = new Path(color, IsHighlighted);
                Path.Points.Add(new Point(LastX, _lastY));
                _endY = 0;
            }

            private void Add(double x, double y)
            {
                if (_endY < y)
                {
                    Path.Points.Add(new Point(x, y));
                    _endY = y;
                }
            }

            private double _lastY = 0;
            private double _endY = 0;
        }

        private static int s_penCount = 0;
        private static readonly List<Color> s_defaultPenColors = [
            Color.Parse("#2684FF"),
            Color.Parse("#36B37E"),
            Color.Parse("#6554C0"),
            Color.Parse("#FF8B00"),
            Color.Parse("#00B8D9"),
            Color.Parse("#FF5630"),
            Color.Parse("#8777D9"),
            Color.Parse("#57D9A3"),
            Color.Parse("#FFC400"),
            Color.Parse("#008DA6"),
        ];
    }
}
