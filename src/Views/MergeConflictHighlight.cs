using System.Globalization;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Utils;

namespace SourceGit.Views
{
    // One fill is used for the text, number column and separator. No margin gaps
    // are left between them, so the active conflict is identifiable at any width.
    internal static class MergeConflictHighlight
    {
        public static readonly IBrush Background = new SolidColorBrush(Color.FromArgb(36, 255, 0, 0));

        public static void Draw(TextEditor editor, bool source, DrawingContext context, double width)
        {
            var view = editor.TextArea.TextView;
            if (!view.VisualLinesValid || editor.DataContext is not ViewModels.MergeConflictEditor vm) return;
            using var clip = context.PushClip(new Rect(0, 0, width, view.Bounds.Height));
            foreach (var line in view.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted) continue;
                var number = line.FirstDocumentLine.LineNumber;
                if (!(source ? vm.IsActiveSourceLine(number) : vm.IsActiveResultLine(number))) continue;
                var top = line.VisualTop - view.VerticalOffset;
                context.DrawRectangle(Background, null, new Rect(0, top, width, line.Height));
            }
        }
    }

    internal sealed class MergeConflictNumberMargin(TextEditor editor, bool source) : LineNumberMargin
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var size = base.MeasureOverride(availableSize);
            return new Size(size.Width + 16, size.Height);
        }

        public override void Render(DrawingContext context)
        {
            var view = TextView;
            if (view is not { VisualLinesValid: true }) return;
            MergeConflictHighlight.Draw(editor, source, context, Bounds.Width);
            foreach (var line in view.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted) continue;
                var number = line.FirstDocumentLine.LineNumber;
                var label = source && editor is MergeConflictTextPresenter comparison
                    ? (number <= comparison.Lines.Count ? comparison.Lines[number - 1].LineNumber : string.Empty)
                    : number.ToString(CultureInfo.CurrentCulture);
                if (string.IsNullOrEmpty(label)) continue;
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    view.CreateTypeface(), editor.FontSize, editor.Foreground);
                var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.VerticalOffset;
                context.DrawText(text, new Point(Bounds.Width - 8 - text.Width, y - text.Height * 0.5));
            }
        }
    }

    internal sealed class MergeConflictSeparator(TextEditor editor, bool source) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(5, 0);
        public override void Render(DrawingContext context)
        {
            MergeConflictHighlight.Draw(editor, source, context, Bounds.Width);
            context.DrawLine(new Pen(Brushes.DarkGray), new Point(0.5, 0), new Point(0.5, Bounds.Height));
        }
    }

    internal sealed class MergeResultBackground(MergeResultEditor editor) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext context) => MergeConflictHighlight.Draw(editor, false, context, view.Bounds.Width);
    }
}
