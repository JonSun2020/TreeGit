using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    // Paint the seam after both adjacent regions, including the fractional-DPI
    // raster edge. Only the selected tab joins the toolbar; other borders remain.
    public sealed class SelectedTabJoin : Control
    {
        public static readonly StyledProperty<LauncherTabBar> TabsProperty = AvaloniaProperty.Register<SelectedTabJoin, LauncherTabBar>(nameof(Tabs));
        public LauncherTabBar Tabs { get => GetValue(TabsProperty); set => SetValue(TabsProperty, value); }
        public static readonly StyledProperty<IBrush> BackgroundProperty = AvaloniaProperty.Register<SelectedTabJoin, IBrush>(nameof(Background));
        public IBrush Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

        public SelectedTabJoin()
        {
            IsHitTestVisible = false;
            LayoutUpdated += (_, _) => InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var list = Tabs?.FindDescendantOfType<ListBox>();
            var item = list?.ContainerFromIndex(list.SelectedIndex);
            var tab = item?.FindDescendantOfType<LauncherTabSizeBox>();
            if (tab == null || !tab.IsEffectivelyVisible || Background == null) return;
            var origin = tab.TranslatePoint(default, this);
            if (!origin.HasValue) return;
            var bottom = origin.Value.Y + tab.Bounds.Height;
            context.DrawRectangle(Background, null, new Rect(origin.Value.X + 1, Math.Floor(bottom - 1), Math.Max(0, tab.Bounds.Width - 2), 3));
        }
    }
}
