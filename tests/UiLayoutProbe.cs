using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
            return new { stageButtonsOutsidePanel = overflow, toolbarLabelCenters = positions };
        }
    }
}
