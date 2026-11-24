using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public class CompactComboBox : ComboBox
    {
        private const double ArrowColumnWidth = 18;

        protected override Type StyleKeyOverride => typeof(ComboBox);

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            if (e.NameScope.Find("DropDownGlyph") is not PathIcon glyph ||
                glyph.GetVisualParent() is not Grid grid)
                return;

            grid.ColumnDefinitions[Grid.GetColumn(glyph)].Width = new GridLength(ArrowColumnWidth);
            glyph.Margin = default;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    public static class DropdownPlacement
    {
        public static readonly IMultiValueConverter OptionPadding = new ComboBoxOptionPadding();

        private sealed class ComboBoxOptionPadding : IMultiValueConverter
        {
            public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values[0] is not Thickness padding)
                    return new Thickness(6, 4);

                // Compact selectors use the arrow space for their left-aligned
                // popup captions.
                const double popupBorder = 1;
                var arrowColumn = values[3] is CompactComboBox ? 0 : 32;
                var left = Math.Max(0, padding.Left - popupBorder);
                var rightInset = values[1] is double boxWidth && values[2] is double itemWidth && itemWidth > 0
                    ? Math.Max(popupBorder, boxWidth - itemWidth - popupBorder)
                    : popupBorder;
                return new Thickness(left, 4, Math.Max(0, arrowColumn + padding.Right - rightInset), 4);
            }
        }

        public static readonly CustomPopupPlacementCallback Align = placement =>
        {
            // Overlay popups are arranged in the layer's coordinates, inside the window frame.
            if (placement.Target is not Control target || OverlayLayer.GetOverlayLayer(target) is not { } layer ||
                target.TranslatePoint(default, layer) is not { } topLeft ||
                target.TranslatePoint(new Point(target.Bounds.Width, target.Bounds.Height), layer) is not { } bottomRight)
                return;

            placement.AnchorRectangle = new Rect(topLeft, bottomRight);
            placement.Anchor = PopupAnchor.BottomLeft;
            placement.Gravity = PopupGravity.BottomRight;
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY |
                PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.ResizeY;
        };
    }
}
