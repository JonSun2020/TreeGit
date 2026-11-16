using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace SourceGit.Views
{
    public sealed class ScreenBoundFlyout : Flyout
    {
        public ScreenBoundFlyout()
        {
            Opening += (_, _) => Popup?.SetCurrentValue(Avalonia.Controls.Primitives.Popup.ShouldUseOverlayLayerProperty, true);
            Placement = PlacementMode.Custom;
            CustomPopupPlacementCallback = placement =>
            {
                if (placement.Target is not Control target || TopLevel.GetTopLevel(target) is not Window owner ||
                    (owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary) is not { } screen) return;
                var area = screen.WorkingArea;
                if (Popup.IsUsingOverlayLayer)
                {
                    var start = owner.PointToScreen(default);
                    var end = owner.PointToScreen(new Point(owner.ClientSize.Width, owner.ClientSize.Height));
                    var left = Math.Max(area.X, start.X);
                    var top = Math.Max(area.Y, start.Y);
                    area = new PixelRect(left, top, Math.Max(1, Math.Min(area.Right, end.X) - left), Math.Max(1, Math.Min(area.Bottom, end.Y) - top));
                }
                var scaling = owner.RenderScaling;
                var width = placement.PopupSize.Width * scaling;
                var height = placement.PopupSize.Height * scaling;
                var below = target.PointToScreen(new Point(0, target.Bounds.Height));
                var above = target.PointToScreen(default);
                var x = Math.Clamp(below.X, area.X + 8, Math.Max(area.X + 8, area.Right - width - 8));
                var y = below.Y + height <= area.Bottom - 8 ? below.Y : above.Y - height;
                y = Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height - 8));
                placement.AnchorRectangle = new Rect(owner.PointToClient(new PixelPoint((int)x, (int)y)), new Size());
                placement.Anchor = PopupAnchor.TopLeft;
                placement.Gravity = PopupGravity.BottomRight;
                placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY |
                    PopupPositionerConstraintAdjustment.ResizeX | PopupPositionerConstraintAdjustment.ResizeY;
            };
        }
    }
}
