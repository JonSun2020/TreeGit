using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    // Keep star-sized panes finite while allowing a small window to scroll the
    // minimum usable surface. Binding content size back to Viewport size causes
    // horizontal/vertical scrollbar feedback at their visibility boundaries.
    public class ContentViewport : ScrollViewer
    {
        private ScrollBar _horizontalBar;
        private ScrollBar _verticalBar;
        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        public ContentViewport() => AllowAutoHide = false;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            var bars = this.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.FindAncestorOfType<ScrollViewer>() == this).ToArray();
            _horizontalBar = bars.FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            _verticalBar = bars.FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Content is Control content && double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height))
            {
                var width = Math.Max(0, availableSize.Width);
                var height = Math.Max(0, availableSize.Height);
                var barWidth = Thickness(_verticalBar?.Width, _verticalBar?.Bounds.Width);
                var barHeight = Thickness(_horizontalBar?.Height, _horizontalBar?.Bounds.Height);
                var horizontal = content.MinWidth > width;
                var vertical = content.MinHeight > height;
                for (var i = 0; i < 3; i++)
                {
                    horizontal = content.MinWidth > width - (vertical ? barWidth : 0) + .01;
                    vertical = content.MinHeight > height - (horizontal ? barHeight : 0) + .01;
                }
                SetCurrentValue(HorizontalScrollBarVisibilityProperty, horizontal ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled);
                SetCurrentValue(VerticalScrollBarVisibilityProperty, vertical ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled);
                content.SetCurrentValue(WidthProperty, Math.Max(content.MinWidth, width - (vertical ? barWidth : 0)));
                content.SetCurrentValue(HeightProperty, Math.Max(content.MinHeight, height - (horizontal ? barHeight : 0)));
            }
            return base.MeasureOverride(availableSize);
        }

        private static double Thickness(double? specified, double? measured)
            => specified is > 0 && double.IsFinite(specified.Value) ? specified.Value : measured is > 0 ? measured.Value : 12;
    }
}
