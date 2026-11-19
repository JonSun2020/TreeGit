using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;

namespace SourceGit.Views
{
    // Keep paired logical rows aligned when either side wraps. Padding is an
    // inline visual object: document text, line numbers and patch indexes stay
    // untouched, including when selecting/copying or staging a hunk.
    internal sealed class SideBySideWrapAlignment : IDisposable
    {
        private sealed class PaddingGenerator : VisualLineElementGenerator
        {
            public readonly Dictionary<int, double> Padding = [];
            public readonly Dictionary<int, double> Applied = [];
            private int _line;
            private int _end;
            private bool _inserted;

            public override void StartGeneration(ITextRunConstructionContext context)
            {
                base.StartGeneration(context);
                _line = context.VisualLine.FirstDocumentLine.LineNumber;
                _end = context.VisualLine.FirstDocumentLine.EndOffset;
                _inserted = false;
                Applied[_line] = 0;
            }

            public override int GetFirstInterestedOffset(int startOffset)
                => !_inserted && startOffset <= _end && Padding.TryGetValue(_line, out var extra) && extra > .1 ? _end : -1;

            public override VisualLineElement ConstructElement(int offset)
            {
                _inserted = true;
                var extra = Padding[_line];
                var view = CurrentContext.TextView;
                var spacer = new Border { Width = 0, Height = view.DefaultLineHeight + extra, UseLayoutRounding = false, IsHitTestVisible = false, Focusable = false };
                TextBlock.SetBaselineOffset(spacer, view.DefaultBaseline);
                Applied[_line] = extra;
                return new InlineObjectElement(0, spacer);
            }
        }

        private readonly SingleSideTextDiffPresenter _left;
        private readonly SingleSideTextDiffPresenter _right;
        private readonly PaddingGenerator _leftPadding = new();
        private readonly PaddingGenerator _rightPadding = new();
        private bool _pending;
        private bool _updating;
        private bool _disposed;

        public SideBySideWrapAlignment(SingleSideTextDiffPresenter left, SingleSideTextDiffPresenter right)
        {
            _left = left; _right = right;
            left.TextArea.TextView.ElementGenerators.Add(_leftPadding);
            right.TextArea.TextView.ElementGenerators.Add(_rightPadding);
            left.TextArea.TextView.VisualLinesChanged += Changed;
            right.TextArea.TextView.VisualLinesChanged += Changed;
            left.SizeChanged += SizeChanged;
            right.SizeChanged += SizeChanged;
            left.PropertyChanged += PropertyChanged;
            right.PropertyChanged += PropertyChanged;
            Queue();
        }

        public bool Matches(SingleSideTextDiffPresenter left, SingleSideTextDiffPresenter right) => _left == left && _right == right;
        private void Changed(object sender, EventArgs args) => Queue();
        private void SizeChanged(object sender, SizeChangedEventArgs args) => Queue();
        private void PropertyChanged(object sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property.Name is "WordWrap" or "FontSize" or "FontFamily" or "TabWidth" or "Document") Queue();
        }

        private void Queue()
        {
            if (_disposed || _pending || _updating) return;
            _pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _pending = false;
                if (_disposed) return;
                _updating = true;
                try { Update(); }
                finally { _updating = false; }
            }, DispatcherPriority.Loaded);
        }

        private void Update()
        {
            var left = _left.TextArea.TextView;
            var right = _right.TextArea.TextView;
            if (!_left.WordWrap || !_right.WordWrap)
            {
                if (_leftPadding.Padding.Count == 0 && _rightPadding.Padding.Count == 0) return;
                _leftPadding.Padding.Clear(); _leftPadding.Applied.Clear();
                _rightPadding.Padding.Clear(); _rightPadding.Applied.Clear();
                left.Redraw(); right.Redraw();
                return;
            }
            if (left.Document == null || right.Document == null || !left.VisualLinesValid || !right.VisualLinesValid) return;
            var rows = left.VisualLines.Concat(right.VisualLines).Select(line => line.FirstDocumentLine.LineNumber).Distinct().ToArray();
            var changedLeft = false; var changedRight = false;
            foreach (var row in rows)
            {
                if (row > left.Document.LineCount || row > right.Document.LineCount) continue;
                var a = left.GetOrConstructVisualLine(left.Document.GetLineByNumber(row));
                var b = right.GetOrConstructVisualLine(right.Document.GetLineByNumber(row));
                var ah = a.Height - _leftPadding.Applied.GetValueOrDefault(row);
                var bh = b.Height - _rightPadding.Applied.GetValueOrDefault(row);
                var height = Math.Max(ah, bh);
                changedLeft |= SetPadding(_leftPadding, row, height - ah);
                changedRight |= SetPadding(_rightPadding, row, height - bh);
            }
            if (changedLeft) left.Redraw();
            if (changedRight) right.Redraw();
        }

        private static bool SetPadding(PaddingGenerator generator, int row, double extra)
        {
            if (Math.Abs(generator.Padding.GetValueOrDefault(row) - extra) < .1) return false;
            if (extra > .1) generator.Padding[row] = extra;
            else generator.Padding.Remove(row);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _left.TextArea.TextView.VisualLinesChanged -= Changed;
            _right.TextArea.TextView.VisualLinesChanged -= Changed;
            _left.SizeChanged -= SizeChanged; _right.SizeChanged -= SizeChanged;
            _left.PropertyChanged -= PropertyChanged; _right.PropertyChanged -= PropertyChanged;
            _left.TextArea.TextView.ElementGenerators.Remove(_leftPadding);
            _right.TextArea.TextView.ElementGenerators.Remove(_rightPadding);
        }
    }
}
