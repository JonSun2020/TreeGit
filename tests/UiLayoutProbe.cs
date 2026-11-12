using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    internal static class UiLayoutProbe
    {
        public static void TraceHistory(string kind, object state)
        {
            var path = Environment.GetEnvironmentVariable("TREEGIT_TEST_POINTER_TRACE");
            if (Environment.GetEnvironmentVariable("CI") == "true" && !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path))
                File.AppendAllText(path, JsonSerializer.Serialize(new { kind, state }) + "\n");
        }

        public static void AttachPointerTrace(Window window)
        {
            var path = Environment.GetEnvironmentVariable("TREEGIT_TEST_POINTER_TRACE");
            if (Environment.GetEnvironmentVariable("CI") != "true" || string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return;
            void Record(PointerEventArgs e, string kind)
            {
                var source = e.Source as Control;
                var row = source as DataGridRow ?? source?.FindAncestorOfType<DataGridRow>();
                var position = e.GetPosition(window);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind,
                    source = source?.GetType().Name, name = source?.Name,
                    sourceContext = source?.DataContext?.GetType().Name,
                    grid = source?.FindAncestorOfType<DataGrid>()?.Name,
                    sourceAncestors = source?.GetVisualAncestors().OfType<Control>().Take(12).Select(p => new { type = p.GetType().Name, name = p.Name, context = p.DataContext?.GetType().Name, visible = p.IsVisible }).ToArray(),
                    commit = (source?.DataContext as Models.Commit)?.SHA,
                    rowCommit = (row?.DataContext as Models.Commit)?.SHA,
                    captured = e.Pointer.Captured?.GetType().Name,
                    x = position.X, y = position.Y,
                    screenX = window.PointToScreen(position).X, screenY = window.PointToScreen(position).Y,
                    rows = source is Avalonia.Controls.Primitives.DataGridRowsPresenter ? window.GetVisualDescendants().OfType<DataGridRow>()
                        .Where(r => r.IsEffectivelyVisible).Select(r => new
                        {
                            sha = (r.DataContext as Models.Commit)?.SHA, r.Index, bounds = r.Bounds.ToString(), r.IsHitTestVisible, r.Opacity,
                            position = r.TranslatePoint(default, window)?.ToString(),
                            surfaces = r.GetVisualDescendants().OfType<Control>().Where(p => p.Name is "PART_Root" or "RowBorder").Select(p => new { name = p.Name, bounds = p.Bounds.ToString(), clip = (p.Clip as RectangleGeometry)?.Rect.ToString(), p.ClipToBounds, p.IsHitTestVisible, p.IsEnabled, p.Opacity }).ToArray(),
                            clip = (r.Clip as RectangleGeometry)?.Rect.ToString(), r.IsEnabled,
                            ancestors = r.GetVisualAncestors().OfType<Control>().Take(5).Select(p => new { type = p.GetType().Name, bounds = p.Bounds.ToString(), clip = (p.Clip as RectangleGeometry)?.Rect.ToString(), p.ClipToBounds, p.IsHitTestVisible, p.IsEnabled, p.Opacity }).ToArray(),
                            presenters = r.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.DataGridCellsPresenter>().Select(p => new { bounds = p.Bounds.ToString(), position = p.TranslatePoint(default, window)?.ToString(), clip = (p.Clip as RectangleGeometry)?.Rect.ToString(), p.ClipToBounds, p.IsHitTestVisible, p.IsEnabled, p.Opacity }).ToArray(),
                            cells = r.GetVisualDescendants().OfType<DataGridCell>().Select(c => new
                            {
                                bounds = c.Bounds.ToString(), c.IsHitTestVisible, c.IsEffectivelyVisible, c.Opacity,
                                clip = (c.Clip as RectangleGeometry)?.Rect.ToString(),
                                position = c.TranslatePoint(default, window)?.ToString(),
                                children = c.GetVisualDescendants().OfType<Border>().Select(b => new { bounds = b.Bounds.ToString(), background = b.Background?.ToString(), b.IsHitTestVisible, b.IsEffectivelyVisible }).ToArray(),
                            }).ToArray(),
                        }).ToArray() : null,
                }) + "\n");
            }
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => Record(e, "down"), RoutingStrategies.Tunnel, true);
            window.AddHandler(InputElement.PointerReleasedEvent, (_, e) => Record(e, "up"), RoutingStrategies.Tunnel, true);
        }

        public static void ClickButton(Window window, string name, string tempDirectory = null)
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Name == name && b.IsEffectivelyVisible);
            if (!button.IsEnabled) throw new InvalidOperationException("The selected button is disabled.");
            button.BringIntoView();
            if (tempDirectory == null)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || !Path.IsPathFullyQualified(tempDirectory) || !File.Exists(tempDirectory))
                throw new InvalidOperationException("Temporary-directory fault injection is restricted to a CI fixture file.");
            var originalTemp = Environment.GetEnvironmentVariable("TEMP");
            var originalTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TEMP", tempDirectory);
                Environment.SetEnvironmentVariable("TMP", tempDirectory);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TEMP", originalTemp);
                Environment.SetEnvironmentVariable("TMP", originalTmp);
            }
        }

        public static object Inspect(Window window)
        {
            var views = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Where(v => v.IsEffectivelyVisible);
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
                        graphColor = ((v.UseGraphColor ? Models.CommitGraph.Pens[commit.Color].Brush : Brushes.Gray) as ISolidColorBrush)?.Color.ToString(),
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
