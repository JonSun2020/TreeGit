using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    internal static class UiLayoutProbe
    {
        public static object Inspect(Window window)
        {
            var views = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Where(v => v.IsEffectivelyVisible && v.IsOverview);
            var overflow = views.SelectMany(view => view.GetVisualDescendants().OfType<Button>())
                .Where(b => b.Classes.Contains("treegit_stage_action"))
                .Where(b =>
                {
                    var border = b.FindAncestorOfType<Border>();
                    var point = b.TranslatePoint(default, border);
                    return point == null || point.Value.X < -.5 || point.Value.X + b.Bounds.Width > border.Bounds.Width + .5;
                }).Select(b => b.Content?.ToString()).ToArray();
            var toolbar = window.GetVisualDescendants().OfType<Views.RepositoryToolbar>().FirstOrDefault(v => v.IsEffectivelyVisible);
            var positions = toolbar?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("toolbar_label") && t.IsEffectivelyVisible)
                .Select(t => new { text = t.Text, y = t.TranslatePoint(new Point(0, t.Bounds.Height / 2), toolbar)?.Y }).ToArray();
            var taggedRefs = window.GetVisualDescendants().OfType<Views.CommitRefsPresenter>()
                .Where(v => v.IsEffectivelyVisible && v.ShowTags && v.DataContext is Models.Commit c && c.Decorators.Any(r => r.IsTag))
                .SelectMany(v =>
                {
                    var commit = (Models.Commit)v.DataContext;
                    var items = (System.Collections.Generic.List<Views.CommitRefsPresenter.RenderItem>)typeof(Views.CommitRefsPresenter)
                        .GetField("_items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(v);
                    return items.Where(item => item.Decorator.IsTag).Select(item => new
                    {
                        color = (item.Brush as ISolidColorBrush)?.Color.ToString(),
                        graphColor = (Models.CommitGraph.Pens[commit.Color].Brush as ISolidColorBrush)?.Color.ToString(),
                        refs = new[] { item.Decorator.Name },
                    });
                }).ToArray();
            var popups = window.GetVisualDescendants().OfType<Control>().Where(v => v.Name == "PopupPanel" && v.IsEffectivelyVisible)
                .Select(v => Bounds(v, window)).ToArray();
            var dialogs = ((IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows
                .Where(w => w != window && w.IsVisible).Select(w => new
                {
                    type = w.GetType().Name,
                    tabs = w.GetVisualDescendants().OfType<TabControl>().Select(t => new { selected = t.SelectedIndex, headers = t.Items.OfType<TabItem>().Select(i => (i.Header as TextBlock)?.Text ?? i.Header?.ToString()).ToArray() }).ToArray(),
                    content = w.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible).Select(v => new { viewport = v.Viewport.Height, extent = v.Extent.Height }).ToArray(),
                }).ToArray();
            var actions = window.GetVisualDescendants().OfType<MenuItem>().Where(m => m.Name?.StartsWith("GitAction_", StringComparison.Ordinal) == true)
                .Select(m => new { id = m.Name, shortcut = m.Tag?.ToString() }).ToArray();
            return new { stageButtonsOutsidePanel = overflow, toolbarLabelCenters = positions, taggedRefs, popups, dialogs, actions };
        }

        private static object Bounds(Control control, Window window)
        {
            var point = control.TranslatePoint(default, window);
            var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
            return new { x = point?.X, y = point?.Y, right = end?.X, bottom = end?.Y, withinWindow = point.HasValue && end.HasValue && point.Value.X >= -.5 && point.Value.Y >= -.5 && end.Value.X <= window.Bounds.Width + .5 && end.Value.Y <= window.Bounds.Height + .5 };
        }
    }
}
