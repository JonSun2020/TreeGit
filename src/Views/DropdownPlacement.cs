using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace SourceGit.Views
{
    public static class DropdownPlacement
    {
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
