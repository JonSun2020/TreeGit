using System;
using System.Collections.Generic;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public record TextDiffSelectedChunk(double Y, double Height, int StartIdx, int EndIdx, bool Combined, bool IsOldSide)
    {
        public static bool IsChanged(TextDiffSelectedChunk oldValue, TextDiffSelectedChunk newValue)
        {
            if (newValue == null)
                return oldValue != null;

            if (oldValue == null)
                return true;

            return Math.Abs(newValue.Y - oldValue.Y) > 0.001 ||
                Math.Abs(newValue.Height - oldValue.Height) > 0.001 ||
                newValue.StartIdx != oldValue.StartIdx ||
                newValue.EndIdx != oldValue.EndIdx ||
                newValue.Combined != oldValue.Combined ||
                newValue.IsOldSide != oldValue.IsOldSide;
        }
    }

    public class TextDiffContext : ObservableObject
    {
        public Models.DiffOption Option => _option;
        public Models.TextDiff Data => _data;

        public Vector ScrollOffset
        {
            get => _scrollOffset;
            set => SetProperty(ref _scrollOffset, value);
        }

        public BlockNavigation BlockNavigation
        {
            get => _blockNavigation;
            set => SetProperty(ref _blockNavigation, value);
        }

        public TextLineRange DisplayRange
        {
            get => _displayRange;
            set => SetProperty(ref _displayRange, value);
        }

        public TextDiffSelectedChunk SelectedChunk
        {
            get => _selectedChunk;
            set => SetProperty(ref _selectedChunk, value);
        }

        internal static (int Start, int End) FindChangeRange(List<Models.TextDiffLine> lines, int index)
        {
            static bool IsChangeOrAlignmentRow(Models.TextDiffLine line) =>
                line.Type is Models.TextDiffLineType.Added or Models.TextDiffLineType.Deleted or Models.TextDiffLineType.None;

            if ((uint)index >= lines.Count || !IsChangeOrAlignmentRow(lines[index]))
                return (-1, -1);

            var start = index;
            while (start > 0 && IsChangeOrAlignmentRow(lines[start - 1]))
                start--;

            var end = index;
            while (end + 1 < lines.Count && IsChangeOrAlignmentRow(lines[end + 1]))
                end++;

            return (start, end);
        }

        public virtual bool IsSideBySide()
        {
            return false;
        }

        public virtual TextDiffContext SwitchMode()
        {
            return null;
        }

        protected void TryKeepPrevState(TextDiffContext prev, List<Models.TextDiffLine> lines)
        {
            var fastTest = prev != null &&
                prev._option.IsUnstaged == _option.IsUnstaged &&
                prev._option.Path.Equals(_option.Path, StringComparison.Ordinal) &&
                prev._option.OrgPath.Equals(_option.OrgPath, StringComparison.Ordinal) &&
                prev._option.Revisions.Count == _option.Revisions.Count;

            if (!fastTest)
            {
                _blockNavigation = new BlockNavigation(lines, 0);
                return;
            }

            for (int i = 0; i < _option.Revisions.Count; i++)
            {
                if (!prev._option.Revisions[i].Equals(_option.Revisions[i], StringComparison.Ordinal))
                {
                    _blockNavigation = new BlockNavigation(lines, 0);
                    return;
                }
            }

            _blockNavigation = new BlockNavigation(lines, prev._blockNavigation.GetCurrentBlockIndex());
        }

        protected Models.DiffOption _option = null;
        protected Models.TextDiff _data = null;
        protected Vector _scrollOffset = Vector.Zero;
        protected BlockNavigation _blockNavigation = null;

        private TextLineRange _displayRange = null;
        private TextDiffSelectedChunk _selectedChunk = null;
    }

    public class CombinedTextDiff : TextDiffContext
    {
        public CombinedTextDiff(Models.DiffOption option, Models.TextDiff diff, TextDiffContext previous = null)
        {
            _option = option;
            _data = diff;

            TryKeepPrevState(previous, _data.Lines);
        }

        public override TextDiffContext SwitchMode()
        {
            return new TwoSideTextDiff(_option, _data, this);
        }
    }

    public class TwoSideTextDiff : TextDiffContext
    {
        public List<Models.TextDiffLine> Old { get; } = [];
        public List<Models.TextDiffLine> New { get; } = [];

        public TwoSideTextDiff(Models.DiffOption option, Models.TextDiff diff, TextDiffContext previous = null)
        {
            _option = option;
            _data = diff;

            foreach (var line in diff.Lines)
            {
                switch (line.Type)
                {
                    case Models.TextDiffLineType.Added:
                        New.Add(line);
                        break;
                    case Models.TextDiffLineType.Deleted:
                        Old.Add(line);
                        break;
                    default:
                        FillEmptyLines();
                        Old.Add(line);
                        New.Add(line);
                        break;
                }
            }

            FillEmptyLines();
            TryKeepPrevState(previous, Old);
        }

        public override bool IsSideBySide()
        {
            return true;
        }

        public override TextDiffContext SwitchMode()
        {
            return new CombinedTextDiff(_option, _data, this);
        }

        public void GetCombinedRangeForSingleSide(ref int startLine, ref int endLine, bool isOldSide)
        {
            endLine = Math.Min(endLine, _data.Lines.Count - 1);

            var oneSide = isOldSide ? Old : New;
            var firstContentLine = -1;
            for (int i = startLine; i <= endLine; i++)
            {
                var line = oneSide[i];
                if (line.Type != Models.TextDiffLineType.None)
                {
                    firstContentLine = i;
                    break;
                }
            }

            if (firstContentLine < 0)
                return;

            var endContentLine = -1;
            for (int i = Math.Min(endLine, oneSide.Count - 1); i >= startLine; i--)
            {
                var line = oneSide[i];
                if (line.Type != Models.TextDiffLineType.None)
                {
                    endContentLine = i;
                    break;
                }
            }

            if (endContentLine < 0)
                return;

            var firstContent = oneSide[firstContentLine];
            var endContent = oneSide[endContentLine];
            startLine = _data.Lines.IndexOf(firstContent);
            endLine = _data.Lines.IndexOf(endContent);
        }

        internal (int Start, int End) GetCombinedChangeRange(int index, bool isOldSide)
        {
            var line = (isOldSide ? Old : New)[index];
            if (line.Type == Models.TextDiffLineType.None)
                line = (isOldSide ? New : Old)[index];

            return FindChangeRange(_data.Lines, _data.Lines.IndexOf(line));
        }

        private void FillEmptyLines()
        {
            if (Old.Count < New.Count)
            {
                int diff = New.Count - Old.Count;
                for (int i = 0; i < diff; i++)
                    Old.Add(new Models.TextDiffLine());
            }
            else if (Old.Count > New.Count)
            {
                int diff = Old.Count - New.Count;
                for (int i = 0; i < diff; i++)
                    New.Add(new Models.TextDiffLine());
            }
        }
    }
}
