using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    internal static class UiLayoutController
    {
        public static object Execute(JsonElement args)
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
            var window = args.TryGetProperty("window", out var scope) && scope.GetString() == "active"
                ? lifetime.Windows.LastOrDefault(w => w.IsVisible) : lifetime.MainWindow;
            if (window == null) throw new InvalidOperationException("The TreeGit window is not ready.");
            if (args.TryGetProperty("twoColumnHistory", out var orientation))
                ViewModels.Preferences.Instance.UseTwoColumnsLayoutInHistories = orientation.GetBoolean();
            if (args.TryGetProperty("twoColumnDiff", out var diffOrientation))
                ViewModels.Preferences.Instance.UseSideBySideDiff = diffOrientation.GetBoolean();
            var splitters = window.GetVisualDescendants().OfType<GridSplitter>().ToArray();
            var action = args.TryGetProperty("action", out var operation) ? operation.GetString() : "list";
            if (action == "resize")
            {
                if (!window.CanResize) throw new InvalidOperationException("This dialog has a fixed size.");
                window.WindowState = WindowState.Normal;
                if (args.TryGetProperty("width", out var width)) window.Width = Dimension(width);
                if (args.TryGetProperty("height", out var height)) window.Height = Dimension(height);
            }
            else if (action == "scroll")
            {
                var scrollers = window.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
                var index = args.TryGetProperty("scroller", out var selected) ? selected.GetInt32() : -1;
                if (index < 0 || index >= scrollers.Length) throw new ArgumentException("List the scroll areas first and use an existing index.");
                var scroller = scrollers[index];
                if (!scroller.IsEffectivelyVisible) throw new InvalidOperationException("This scroll area is hidden.");
                scroller.Offset = new Vector(args.TryGetProperty("dx", out _) ? Change(args, "dx") : scroller.Offset.X,
                    args.TryGetProperty("dy", out _) ? Change(args, "dy") : scroller.Offset.Y);
            }
#if INTEGRATION_TESTS
            else if (action == "test_click")
                UiLayoutProbe.ClickButton(window, args.GetProperty("button").GetString(), args.TryGetProperty("tempDirectory", out var blockedTemp) ? blockedTemp.GetString() : null);
#endif
            else if (action is "drag" or "restore")
            {
                var index = args.TryGetProperty("splitter", out var selected) ? selected.GetInt32() : -1;
                if (index < 0 || index >= splitters.Length) throw new ArgumentException("List the splitters first and use an existing index.");
                var splitter = splitters[index];
                if (action == "drag")
                {
                    if (!splitter.IsEffectivelyVisible || !splitter.IsEnabled || splitter.Bounds.Width <= 0 || splitter.Bounds.Height <= 0)
                        throw new InvalidOperationException("This splitter is not currently available to drag.");
                    var delta = new Vector(Change(args, "dx"), Change(args, "dy"));
                    splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent, Vector = new Vector() });
                    try { splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = delta }); }
                    finally { splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = delta }); }
                }
                else
                {
                    var grid = ParentGrid(splitter) ?? throw new InvalidOperationException("This splitter has no owning grid.");
                    if (args.TryGetProperty("columns", out var columns))
                    {
                        if (columns.GetArrayLength() != grid.ColumnDefinitions.Count) throw new ArgumentException("Column count changed; inspect the current layout again.");
                        for (var i = 0; i < columns.GetArrayLength(); i++) grid.ColumnDefinitions[i].SetCurrentValue(ColumnDefinition.WidthProperty, GridLength.Parse(columns[i].GetString()));
                    }
                    if (args.TryGetProperty("rows", out var rows))
                    {
                        if (rows.GetArrayLength() != grid.RowDefinitions.Count) throw new ArgumentException("Row count changed; inspect the current layout again.");
                        for (var i = 0; i < rows.GetArrayLength(); i++) grid.RowDefinitions[i].SetCurrentValue(RowDefinition.HeightProperty, GridLength.Parse(rows[i].GetString()));
                    }
                }
            }
            else if (action != "list") throw new ArgumentException("Unknown layout action.");
            return Inspect(window);
        }

        private static double Dimension(JsonElement value)
        {
            var size = value.GetDouble();
            if (!double.IsFinite(size) || size < 200 || size > 8192) throw new ArgumentException("Window dimensions must be between 200 and 8192.");
            return size;
        }

        private static double Change(JsonElement args, string name)
        {
            var value = args.TryGetProperty(name, out var property) ? property.GetDouble() : 0;
            if (!double.IsFinite(value) || Math.Abs(value) > 10000) throw new ArgumentException("Drag changes must be finite and within 10000.");
            return value;
        }

        private static Grid ParentGrid(GridSplitter splitter) => splitter.Parent as Grid ?? splitter.FindAncestorOfType<Grid>();

        public static object Inspect(Window window)
        {
            var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
            object Bounds(Control control)
            {
                var start = control.TranslatePoint(default, window);
                var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
                return new { x = start?.X, y = start?.Y, width = end?.X - start?.X, height = end?.Y - start?.Y,
                    localWidth = control.Bounds.Width, localHeight = control.Bounds.Height };
            }
#if INTEGRATION_TESTS
            object ScreenPoint(Control control, bool grip = false)
            {
                var point = control.PointToScreen(new Point(grip ? Math.Max(1, control.Bounds.Width - 2) : control.Bounds.Width / 2, control.Bounds.Height / 2));
                return new { x = point.X, y = point.Y };
            }
#endif
            return new
            {
                window = new { title = window.Title, width = window.Bounds.Width, height = window.Bounds.Height, minWidth = window.MinWidth, minHeight = window.MinHeight,
#if INTEGRATION_TESTS
                    nativeHandle = window.TryGetPlatformHandle()?.Handle.ToInt64(),
#endif
                    canResize = window.CanResize, state = window.WindowState.ToString(), scaling = window.RenderScaling },
                preferredSidebarWidth = ViewModels.Preferences.Instance.Layout.RepositorySidebarWidth,
                splitters = controls.OfType<GridSplitter>().Select((splitter, index) =>
                {
                    var grid = ParentGrid(splitter);
                    return new { index, name = splitter.Name, owner = splitter.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name,
#if INTEGRATION_TESTS
                        screen = ScreenPoint(splitter),
#endif
                        column = Grid.GetColumn(splitter), row = Grid.GetRow(splitter),
                        visible = splitter.IsEffectivelyVisible && splitter.Bounds.Width > 0 && splitter.Bounds.Height > 0, enabled = splitter.IsEnabled, bounds = Bounds(splitter),
                        columns = grid?.ColumnDefinitions.Select(d => d.Width.ToString()).ToArray(), rows = grid?.RowDefinitions.Select(d => d.Height.ToString()).ToArray(),
                        actualColumns = grid?.ColumnDefinitions.Select(d => d.ActualWidth).ToArray(), actualRows = grid?.RowDefinitions.Select(d => d.ActualHeight).ToArray(),
                        minColumns = grid?.ColumnDefinitions.Select(d => d.MinWidth).ToArray(), minRows = grid?.RowDefinitions.Select(d => d.MinHeight).ToArray() };
                }).ToArray(),
#if INTEGRATION_TESTS
                columnHeaders = controls.OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0)
                    .Select(h => new { text = string.Join("", h.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)),
                        bounds = Bounds(h), screen = ScreenPoint(h, true) }).ToArray(),
#endif
                buttons = controls.OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0).Select(b =>
                {
                    var border = b.FindAncestorOfType<Border>();
                    var point = border == null ? null : b.TranslatePoint(default, border);
                    return new { name = b.Name, text = b.Content as string ?? string.Join("", b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), classes = b.Classes.ToArray(), enabled = b.IsEnabled, bounds = Bounds(b),
                        inScrollArea = b.FindAncestorOfType<ScrollViewer>() != null,
                        parentBorderWidth = border?.Bounds.Width, parentBorderHeight = border?.Bounds.Height,
                        outsideParentBorder = point.HasValue && (point.Value.X < -.5 || point.Value.Y < -.5 || point.Value.X + b.Bounds.Width > border.Bounds.Width + .5 || point.Value.Y + b.Bounds.Height > border.Bounds.Height + .5) };
                }).ToArray(),
                panes = controls.OfType<UserControl>().Where(v => v.IsEffectivelyVisible && v.Bounds.Width > 0 && v.Bounds.Height > 0)
                    .Select(v => new { type = v.GetType().Name, name = v.Name, bounds = Bounds(v) }).ToArray(),
                diffs = controls.OfType<Views.DiffView>().Where(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.DiffContext)
                    .Select(v => new { file = ((ViewModels.DiffContext)v.DataContext).Title, type = ((ViewModels.DiffContext)v.DataContext).Content?.GetType().Name }).ToArray(),
                scrolls = controls.OfType<ScrollViewer>().Select((s, index) => new { index, name = s.Name, visible = s.IsEffectivelyVisible,
                    adaptive = s is Views.ContentViewport,
                    bounds = Bounds(s), viewport = new { width = s.Viewport.Width, height = s.Viewport.Height },
                    extent = new { width = s.Extent.Width, height = s.Extent.Height }, offset = new { x = s.Offset.X, y = s.Offset.Y },
                    horizontal = s.HorizontalScrollBarVisibility.ToString(), vertical = s.VerticalScrollBarVisibility.ToString() }).ToArray(),
                bottomTabs = controls.OfType<Button>().Where(b => b.Classes.Contains("treegit_bottom_tab") && b.IsEffectivelyVisible)
                    .Select(b => new { text = b.Content as string, active = b.Classes.Contains("active"), bounds = Bounds(b),
                        content = b.GetVisualDescendants().OfType<TextBlock>().Select(t => Bounds(t)).ToArray() }).ToArray()
            };
        }
    }
}
