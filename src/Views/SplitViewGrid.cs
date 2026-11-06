using System;
using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    // A remembered pixel width must not push the other pane outside its viewport
    // after a window shrinks, the history orientation changes, or zoom increases.
    public class SplitViewGrid : Grid
    {
        protected override Type StyleKeyOverride => typeof(Grid);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (double.IsFinite(availableSize.Width) && ColumnDefinitions.Count == 3)
            {
                var first = ColumnDefinitions[0];
                var last = ColumnDefinitions[2];
                var divider = ColumnDefinitions[1].Width;
                var available = availableSize.Width - (divider.IsAbsolute ? divider.Value : 4);
                if (available >= first.MinWidth + last.MinWidth)
                {
                    if (first.Width.IsAbsolute && first.Width.Value > available - last.MinWidth)
                        first.SetCurrentValue(ColumnDefinition.WidthProperty, new GridLength(available - last.MinWidth));
                    if (last.Width.IsAbsolute && last.Width.Value > available - first.MinWidth)
                        last.SetCurrentValue(ColumnDefinition.WidthProperty, new GridLength(available - first.MinWidth));
                }
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
