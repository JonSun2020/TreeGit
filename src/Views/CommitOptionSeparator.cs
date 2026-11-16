using System.Globalization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SourceGit.Views
{
    public sealed class CommitOptionSeparator : TemplatedControl
    {
        private static readonly IBrush Well = new SolidColorBrush(Color.FromArgb(32, 0, 0, 0));
        private static readonly Pen Shadow = new(new SolidColorBrush(Color.FromArgb(102, 0, 0, 0)), 1);
        private static readonly Pen Highlight = new(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        public double SideSpace => (Bounds.Width - 3) / 2;

        protected override Size MeasureOverride(Size availableSize)
        {
            var spaces = new FormattedText("     ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily), FontSize, Brushes.Gray);
            return new Size(spaces.WidthIncludingTrailingWhitespace * 2 + 3, 18);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty) InvalidateMeasure();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var x = SideSpace;
            var y = (Bounds.Height - 18) / 2;
            context.DrawRectangle(Well, null, new Rect(x, y, 3, 18), 1, 1);
            context.DrawLine(Shadow, new Point(x + .5, y + .5), new Point(x + .5, y + 17.5));
            context.DrawLine(Shadow, new Point(x + .5, y + .5), new Point(x + 2.5, y + .5));
            context.DrawLine(Highlight, new Point(x + 2.5, y + .5), new Point(x + 2.5, y + 17.5));
            context.DrawLine(Highlight, new Point(x + .5, y + 17.5), new Point(x + 2.5, y + 17.5));
        }
    }
}
