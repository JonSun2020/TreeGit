using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    // Every wrapped row ends on the same right edge, so equal-width stage actions
    // remain vertically aligned when the file pane becomes narrow.
    public sealed class RightAlignedWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var child in Children)
                if (child.IsVisible) child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var lines = BuildLines(availableSize.Width);
            double width = 0, height = 0;
            foreach (var line in lines)
            {
                width = Math.Max(width, line.Width);
                height += line.Height;
            }
            return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            foreach (var line in BuildLines(finalSize.Width))
            {
                var x = Math.Max(0, finalSize.Width - line.Width);
                foreach (var child in line.Children)
                {
                    var size = child.DesiredSize;
                    child.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, size.Width, size.Height));
                    x += size.Width;
                }
                y += line.Height;
            }
            return finalSize;
        }

        private List<Line> BuildLines(double width)
        {
            var lines = new List<Line>();
            var line = new Line();
            foreach (var child in Children)
            {
                if (!child.IsVisible) continue;
                if (line.Children.Count > 0 && line.Width + child.DesiredSize.Width > width)
                {
                    lines.Add(line);
                    line = new Line();
                }
                line.Children.Add(child);
                line.Width += child.DesiredSize.Width;
                line.Height = Math.Max(line.Height, child.DesiredSize.Height);
            }
            if (line.Children.Count > 0) lines.Add(line);
            return lines;
        }

        private sealed class Line
        {
            public readonly List<Control> Children = [];
            public double Width;
            public double Height;
        }
    }
}
