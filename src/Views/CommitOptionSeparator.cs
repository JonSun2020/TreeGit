using System.Globalization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SourceGit.Views
{
    public sealed class CommitOptionSeparator : TemplatedControl
    {
        public static readonly StyledProperty<double> GrooveHeightProperty =
            AvaloniaProperty.Register<CommitOptionSeparator, double>(nameof(GrooveHeight), 18);

        public double GrooveHeight
        {
            get => GetValue(GrooveHeightProperty);
            set => SetValue(GrooveHeightProperty, value);
        }

        public static readonly StyledProperty<double> GrooveWidthProperty =
            AvaloniaProperty.Register<CommitOptionSeparator, double>(nameof(GrooveWidth), 3);

        public double GrooveWidth
        {
            get => GetValue(GrooveWidthProperty);
            set => SetValue(GrooveWidthProperty, value);
        }

        private static readonly IBrush Well = new SolidColorBrush(Color.FromArgb(32, 0, 0, 0));
        private static readonly Pen Shadow = new(new SolidColorBrush(Color.FromArgb(102, 0, 0, 0)), 1);
        private static readonly Pen Highlight = new(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        public double SideSpace => (Bounds.Width - GrooveWidth) / 2;

        protected override Size MeasureOverride(Size availableSize)
        {
            var spaces = new FormattedText("     ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily), FontSize, Brushes.Gray);
            return new Size(spaces.WidthIncludingTrailingWhitespace * 2 + GrooveWidth, GrooveHeight);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == GrooveHeightProperty || change.Property == GrooveWidthProperty) InvalidateMeasure();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var x = SideSpace;
            var height = System.Math.Min(GrooveHeight, Bounds.Height);
            var y = (Bounds.Height - height) / 2;
            context.DrawRectangle(Well, null, new Rect(x, y, GrooveWidth, height), 1, 1);
            context.DrawLine(Shadow, new Point(x + .5, y + .5), new Point(x + .5, y + height - .5));
            context.DrawLine(Shadow, new Point(x + .5, y + .5), new Point(x + GrooveWidth - .5, y + .5));
            context.DrawLine(Highlight, new Point(x + GrooveWidth - .5, y + .5), new Point(x + GrooveWidth - .5, y + height - .5));
            context.DrawLine(Highlight, new Point(x + .5, y + height - .5), new Point(x + GrooveWidth - .5, y + height - .5));
        }
    }
}
