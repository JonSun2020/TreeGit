using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class SearchBranches : UserControl
    {
        public SearchBranches()
        {
            InitializeComponent();
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => BranchFilter.Focus(), DispatcherPriority.Loaded);
            AttachedToVisualTree += (_, _) => App.LocaleChanged += OnLocaleChanged;
            DetachedFromVisualTree += (_, _) => App.LocaleChanged -= OnLocaleChanged;
        }

        public static Flyout CreateFlyout(Control target, SearchBranches content)
        {
            // Resolve placement in the owner's screen coordinates. A target
            // inside a zoomed and scrolled grid can otherwise be flipped above
            // the working area by the default placement calculation.
            return new Flyout
            {
                Content = content,
                Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = placement =>
                {
                    if (TopLevel.GetTopLevel(target) is not Window owner ||
                        (owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary) is not { } screen)
                        return;
                    var area = screen.WorkingArea;
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
                    placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX |
                        PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.ResizeX |
                        PopupPositionerConstraintAdjustment.ResizeY;
                },
            };
        }

        private void OnLocaleChanged()
        {
            if (DataContext is ViewModels.SearchBranches context) context.RefreshLocalizedText();
        }

        private void OnBranchClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ViewModels.SearchBranchRow row } && DataContext is ViewModels.SearchBranches context)
                context.Choose(row.Branch);
            e.Handled = true;
        }

        private void OnBranchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BranchList.SelectedItem is ViewModels.SearchBranchRow row && DataContext is ViewModels.SearchBranches context)
            {
                context.Choose(row.Branch);
                e.Handled = true;
            }
        }
    }
}
