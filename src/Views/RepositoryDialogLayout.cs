using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace SourceGit.Views
{
    internal static class RepositoryDialogLayout
    {
        public static void Apply(ChromelessWindow window, StackPanel body, Control footer, double width)
        {
            var margin = body.Margin;
            body.Margin = new Thickness(margin.Left, margin.Top, margin.Right, body.Spacing);
            footer.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
            var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            root.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Grid.SetRow(footer, 1); root.Children.Add(footer); window.Content = root;
            if (body.Children.Count > 0 && body.Children[0] is TextBlock title)
                title.PointerPressed += (sender, e) =>
                {
                    if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveWindow(sender, e);
                };

            var pending = false;
            var centerPending = false;
            void Fit(bool center)
            {
                if (!window.IsVisible) return;
                var screen = center && window.Owner is Window owner ? owner.Screens.ScreenFromWindow(owner) : window.Screens.ScreenFromWindow(window);
                screen ??= window.Screens.Primary;
                if (screen == null) return;
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                var centerX = window.Position.X + window.Bounds.Width * scale / 2;
                var centerY = window.Position.Y + window.Bounds.Height * scale / 2;
                if (center && window.Owner is Window parent)
                {
                    centerX = parent.Position.X + parent.Bounds.Width * scale / 2;
                    centerY = parent.Position.Y + parent.Bounds.Height * scale / 2;
                }
                window.MaxWidth = Math.Max(1, area.Width / scale - 32);
                window.MaxHeight = Math.Max(1, area.Height / scale - 32);
                var targetWidth = Math.Min(width * ViewModels.Preferences.Instance.Zoom, window.MaxWidth);
                if (Math.Abs(window.Width - targetWidth) > .1) window.Width = targetWidth;
                var height = Math.Min(window.Bounds.Height, window.MaxHeight);
                window.Position = new PixelPoint(
                    (int)Math.Clamp(centerX - targetWidth * scale / 2, area.X + 8, Math.Max(area.X + 8, area.Right - targetWidth * scale - 8)),
                    (int)Math.Clamp(centerY - height * scale / 2, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height * scale - 8)));
            }
            void Schedule(bool center)
            {
                centerPending |= center;
                if (pending) return;
                pending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    pending = false;
                    var shouldCenter = centerPending; centerPending = false;
                    Fit(shouldCenter);
                }, DispatcherPriority.Loaded);
            }
            void PreferencesChanged(object _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ViewModels.Preferences.Zoom)) Schedule(false);
            }
            window.Opened += (_, _) => { Fit(true); Schedule(true); };
            window.SizeChanged += (_, _) => Schedule(false);
            ViewModels.Preferences.Instance.PropertyChanged += PreferencesChanged;
            window.Closed += (_, _) => ViewModels.Preferences.Instance.PropertyChanged -= PreferencesChanged;
        }
    }
}
