using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Data.Converters;

namespace SourceGit.Views
{
    public static class DropdownPlacement
    {
        public static readonly IMultiValueConverter OptionPadding = new ComboBoxOptionPadding();

        private sealed class ComboBoxOptionPadding : IMultiValueConverter
        {
            public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values[0] is not Thickness padding)
                    return new Thickness(6, 4);

                // Fluent measures the closed caption before its 32 DIP arrow column.
                // The list can also lose width to a permanently visible scrollbar.
                const double popupBorder = 1;
                const double arrowColumn = 32;
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
