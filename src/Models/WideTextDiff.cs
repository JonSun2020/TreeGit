using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SourceGit.Models
{
    public partial class TextDiff
    {
        public bool IsDisplayOnly { get; private set; }

        internal static TextDiff FromDecodedText(string before, string after, int context, bool ignoreSpace, bool ignoreCR, DiffResult original)
        {
            var oldLines = LinesOf(before);
            var newLines = LinesOf(after);
            var oldKeys = Keys(oldLines);
            var newKeys = Keys(newLines);
            var pairs = MergeConflictComparison.AlignLines(oldKeys, newKeys);
            var result = new TextDiff
            {
                IsDisplayOnly = true, MaxLineNumber = Math.Max(oldLines.Length, newLines.Length),
                OldHash = original.OldHash, NewHash = original.NewHash, OldMode = original.OldMode, NewMode = original.NewMode,
            };
            var hunks = new List<(int Start, int End)>();
            for (var i = 0; i < pairs.Count; i++)
            {
                var (old, current) = pairs[i];
                if (old >= 0 && current >= 0 && oldKeys[old] == newKeys[current]) continue;
                var start = Math.Max(0, i - context);
                var end = Math.Min(pairs.Count, i + context + 1);
                if (hunks.Count > 0 && start <= hunks[^1].End) hunks[^1] = (hunks[^1].Start, end);
                else hunks.Add((start, end));
            }
            foreach (var (start, end) in hunks)
            {
                var slice = pairs.GetRange(start, end - start);
                var oldCount = slice.Count(p => p.Old >= 0);
                var newCount = slice.Count(p => p.New >= 0);
                var oldStart = oldCount == 0 ? 0 : slice.First(p => p.Old >= 0).Old + 1;
                var newStart = newCount == 0 ? 0 : slice.First(p => p.New >= 0).New + 1;
                result.Lines.Add(new(TextDiffLineType.Indicator, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@", [], 0, 0));
                var added = new List<TextDiffLine>();
                foreach (var (old, current) in slice)
                {
                    if (old >= 0 && current >= 0 && oldKeys[old] == newKeys[current])
                    {
                        result.Lines.AddRange(added); added.Clear();
                        result.Lines.Add(new(TextDiffLineType.Normal, newLines[current].TrimEnd('\r'), [], old + 1, current + 1));
                        continue;
                    }
                    if (old >= 0) { result.Lines.Add(new(TextDiffLineType.Deleted, oldLines[old].TrimEnd('\r'), [], old + 1, 0)); result.DeletedLines++; }
                    if (current >= 0) { added.Add(new(TextDiffLineType.Added, newLines[current].TrimEnd('\r'), [], 0, current + 1)); result.AddedLines++; }
                }
                result.Lines.AddRange(added);
            }
            return result;

            static string[] LinesOf(string text) => text.Length == 0 ? [] : text.Split('\n');
            string[] Keys(string[] lines) => ignoreSpace ? lines.Select(l => Regex.Replace(l.TrimEnd(), @"\s+", " ")).ToArray() :
                ignoreCR ? lines.Select(l => l.TrimEnd('\r')).ToArray() : lines;
        }
    }
}
