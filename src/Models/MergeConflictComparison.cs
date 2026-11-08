using System;
using System.Collections.Generic;
using System.Linq;

using AvaloniaEdit.Document;

namespace SourceGit.Models
{
    public readonly record struct MergeConflictRange(int Start, int End);

    // Display-only alignment: never change the authoritative editable document.
    public sealed class MergeConflictComparison
    {
        public List<ConflictLine> Local { get; } = [];
        public List<ConflictLine> Base { get; } = [];
        public List<ConflictLine> Remote { get; } = [];
        public MergeConflictRange[] Ranges { get; }
        public int RegionAtLine(int line) => line > 0 && line <= _regions.Count ? _regions[line - 1] : -1;

        public MergeConflictComparison(MergeConflictDocument document, string baseText)
        {
            var local = new List<ConflictLine>();
            var remote = new List<ConflictLine>();
            var regions = new List<int>();
            var localNumber = 1;
            var remoteNumber = 1;
            var source = 0;
            for (var i = 0; i < document.Regions.Count; i++)
            {
                var region = document.Regions[i];
                CommonUntil(region.StartLineInOriginal);
                var count = Math.Max(1, Math.Max(region.OursContent.Count, region.TheirsContent.Count));
                for (var j = 0; j < count; j++)
                {
                    local.Add(j < region.OursContent.Count ? new(ConflictLineType.Ours, region.OursContent[j], localNumber++) : new());
                    remote.Add(j < region.TheirsContent.Count ? new(ConflictLineType.Theirs, region.TheirsContent[j], remoteNumber++) : new());
                    regions.Add(i);
                }
                source = region.EndLineInOriginal + 1;
            }
            CommonUntil(document.OursLines.Count);

            // Keep line text and numbering, including a real final empty line.
            var baseLines = baseText == null ? [] : SplitLines(baseText);
            var pairs = new List<(int Old, int New)>();
            Align(0, baseLines.Length, 0, local.Count, 0);
            var next = new int[pairs.Count];
            var nextRegion = -1;
            for (var i = pairs.Count - 1; i >= 0; i--)
            {
                if (pairs[i].New >= 0) nextRegion = regions[pairs[i].New];
                next[i] = nextRegion;
            }
            var previous = -1;
            for (var i = 0; i < pairs.Count; i++)
            {
                var (old, current) = pairs[i];
                var region = current >= 0 ? regions[current] : previous >= 0 ? previous : next[i];
                Local.Add(current >= 0 ? local[current] : new());
                Remote.Add(current >= 0 ? remote[current] : new());
                Base.Add(old >= 0 ? new(ConflictLineType.Common, baseLines[old], old + 1) : new());
                _regions.Add(region);
                if (current >= 0) previous = regions[current];
            }
            Ranges = new MergeConflictRange[document.Regions.Count];
            for (var i = 0; i < Ranges.Length; i++) Ranges[i] = new(-1, -1);
            for (var i = 0; i < _regions.Count; i++)
            {
                var r = _regions[i];
                if (r >= 0) Ranges[r] = new(Ranges[r].Start < 0 ? i : Ranges[r].Start, i);
            }

            void CommonUntil(int end)
            {
                for (; source < end; source++)
                {
                    var text = document.OursLines[source].Content;
                    local.Add(new(ConflictLineType.Common, text, localNumber++));
                    remote.Add(new(ConflictLineType.Common, text, remoteNumber++));
                    regions.Add(-1);
                }
            }

            // Patience anchors bound memory and retain line order. Unrelated gaps
            // pair positionally; all lines from every version remain visible once.
            void Align(int a, int ae, int b, int be, int depth)
            {
                while (a < ae && b < be && baseLines[a] == local[b].Content) pairs.Add((a++, b++));
                var suffix = 0;
                while (a < ae - suffix && b < be - suffix && baseLines[ae - suffix - 1] == local[be - suffix - 1].Content) suffix++;
                var endA = ae - suffix;
                var endB = be - suffix;
                var anchors = new List<(int Old, int New)>();
                if (a < endA && b < endB && depth < 32)
                {
                    var oldCounts = Unique(baseLines, a, endA);
                    var newCounts = new Dictionary<string, (int Count, int Index)>(StringComparer.Ordinal);
                    for (var j = b; j < endB; j++)
                        newCounts[local[j].Content] = newCounts.TryGetValue(local[j].Content, out var n) ? (n.Count + 1, j) : (1, j);
                    for (var j = a; j < endA; j++)
                        if (oldCounts[baseLines[j]].Count == 1 && newCounts.TryGetValue(baseLines[j], out var n) && n.Count == 1)
                            anchors.Add((j, n.Index));
                    anchors = Increasing(anchors);
                }
                if (anchors.Count == 0)
                {
                    while (a < endA || b < endB) pairs.Add((a < endA ? a++ : -1, b < endB ? b++ : -1));
                }
                else
                {
                    foreach (var anchor in anchors)
                    {
                        Align(a, anchor.Old, b, anchor.New, depth + 1);
                        pairs.Add(anchor); a = anchor.Old + 1; b = anchor.New + 1;
                    }
                    Align(a, endA, b, endB, depth + 1);
                }
                for (var j = 0; j < suffix; j++) pairs.Add((endA + j, endB + j));
            }
        }

        private static string[] SplitLines(string text)
        {
            var document = new TextDocument(text);
            return document.Lines.Select(document.GetText).ToArray();
        }

        private static Dictionary<string, (int Count, int Index)> Unique(string[] lines, int start, int end)
        {
            var counts = new Dictionary<string, (int Count, int Index)>(StringComparer.Ordinal);
            for (var i = start; i < end; i++) counts[lines[i]] = counts.TryGetValue(lines[i], out var n) ? (n.Count + 1, i) : (1, i);
            return counts;
        }

        private static List<(int Old, int New)> Increasing(List<(int Old, int New)> anchors)
        {
            var tails = new List<int>();
            var previous = new int[anchors.Count];
            for (var i = 0; i < anchors.Count; i++)
            {
                var lo = 0; var hi = tails.Count;
                while (lo < hi) { var mid = (lo + hi) / 2; if (anchors[tails[mid]].New < anchors[i].New) lo = mid + 1; else hi = mid; }
                previous[i] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
            }
            var result = new List<(int Old, int New)>();
            for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i]) result.Add(anchors[i]);
            result.Reverse(); return result;
        }

        private readonly List<int> _regions = [];
    }
}
