using System;

namespace SourceGit.Models
{
    public sealed class DiffContextOption : LocalizedOption
    {
        public int Lines { get; }
        public string Name => Lines == 0 ? App.Text("Diff.VisualLines.All") : App.Text("Diff.Context.Lines", Lines);

        private DiffContextOption(int lines) => Lines = lines;

        public static readonly DiffContextOption[] Supported = [new(5), new(15), new(50), new(0)];
        public static DiffContextOption LongestNumeric => Supported[^2];

        internal static DiffContextOption Resolve(int lines, bool standalone) =>
            Array.Find(Supported, option => option.Lines == lines) ?? (standalone ? Supported[^1] : Supported[0]);
    }

    public sealed record RepositoryDiffContextOptions(int Main = 5, int Standalone = 0);
}
