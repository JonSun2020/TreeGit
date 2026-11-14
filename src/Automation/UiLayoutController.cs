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
                if (args.TryGetProperty("scrollbar", out var barIndex))
                {
                    var bars = window.GetVisualDescendants().OfType<ScrollBar>().ToArray();
                    var selectedBar = barIndex.GetInt32();
                    if (selectedBar < 0 || selectedBar >= bars.Length) throw new ArgumentException("List the scroll bars first and use an existing index.");
                    var bar = bars[selectedBar];
                    if (!bar.IsEffectivelyVisible || !bar.IsEnabled) throw new InvalidOperationException("This scroll bar is hidden or unavailable.");
                    var value = args.GetProperty("value").GetDouble();
                    if (!double.IsFinite(value)) throw new ArgumentException("Scroll value must be finite.");
                    Views.DataGridScrolling.SetOffset(bar, value);
                    return Inspect(window);
                }
                var scrollers = window.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
                var index = args.TryGetProperty("scroller", out var selected) ? selected.GetInt32() : -1;
                if (index < 0 || index >= scrollers.Length) throw new ArgumentException("List the scroll areas first and use an existing index.");
                var scroller = scrollers[index];
                if (!scroller.IsEffectivelyVisible) throw new InvalidOperationException("This scroll area is hidden.");
                scroller.Offset = new Vector(args.TryGetProperty("dx", out _) ? Change(args, "dx") : scroller.Offset.X,
                    args.TryGetProperty("dy", out _) ? Change(args, "dy") : scroller.Offset.Y);
            }
#if INTEGRATION_TESTS
            else if (action == "test_menu")
                return MenuToggleProbe.Execute(window, args);
            else if (action == "test_metadata")
            {
                if (Environment.GetEnvironmentVariable("CI") != "true") throw new InvalidOperationException("An owned CI fixture is required.");
                var document = window.GetVisualDescendants().OfType<Views.CommitMetadataText>().Single(d => d.IsEffectivelyVisible);
                var index = args.GetProperty("value").GetInt32();
                if (index < 0 || index > document.PlainText.Length) throw new ArgumentOutOfRangeException(nameof(index));
                var rect = document.TextLayout.HitTestTextPosition(index);
                var local = new Point(rect.X + document.Padding.Left + .5, rect.Y + document.Padding.Top + rect.Height / 2);
                if (args.TryGetProperty("text", out var mode) && mode.GetString() == "reveal")
                    document.BringIntoView(new Rect(rect.X + document.Padding.Left, rect.Y + document.Padding.Top, Math.Max(1, rect.Width), rect.Height));
                var point = document.PointToScreen(local);
                var pixelHit = document.TextLayout.HitTestPoint(document.PointToClient(point) - new Point(document.Padding.Left, document.Padding.Top)).TextPosition;
                // A rounded physical pixel at a formatting boundary can be on
                // the previous character. Target a real hit for this caret.
                for (var step = 1; pixelHit != index && step <= 24; step++)
                {
                    var delta = (step % 2 == 1 ? 1 : -1) * ((step + 1) / 2);
                    point = document.PointToScreen(new Point(local.X + delta, local.Y));
                    pixelHit = document.TextLayout.HitTestPoint(document.PointToClient(point) - new Point(document.Padding.Left, document.Padding.Top)).TextPosition;
                }
                local = document.PointToClient(point);
                var visible = document.GetVisualAncestors().OfType<ScrollViewer>().All(scroll =>
                    document.TranslatePoint(local, scroll) is { } relative && relative.X >= 0 && relative.Y >= 0 && relative.X < scroll.Bounds.Width && relative.Y < scroll.Bounds.Height);
                return new { index, hit = pixelHit, screen = new { x = point.X, y = point.Y }, visible };
            }
            else if (action == "test_reveal")
            {
                if (Environment.GetEnvironmentVariable("CI") != "true") throw new InvalidOperationException("An owned CI fixture is required.");
                var name = args.GetProperty("button").GetString();
                var expected = args.TryGetProperty("text", out var match) ? match.GetString() : null;
                var target = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name && c.IsEffectivelyVisible && (expected == null || c is TextBlock t && t.Text == expected));
                target.BringIntoView();
            }
            else if (action == "test_click")
            {
                UiLayoutProbe.ClickButton(window, args.GetProperty("button").GetString(), args.TryGetProperty("tempDirectory", out var blockedTemp) ? blockedTemp.GetString() : null);
                if (!window.IsVisible) return new { closed = true };
            }
            else if (action == "test_set")
            {
                if (Environment.GetEnvironmentVariable("CI") != "true")
                    throw new InvalidOperationException("Input fixtures are restricted to the isolated CI application.");
                var target = window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == args.GetProperty("button").GetString());
                if (!target.IsEffectivelyVisible || !target.IsEnabled) throw new InvalidOperationException("The fixture control is unavailable.");
                if (target is TextBox text) text.SetCurrentValue(TextBox.TextProperty, args.GetProperty("text").GetString());
                else if (target is Expander expander) expander.SetCurrentValue(Expander.IsExpandedProperty, args.GetProperty("value").GetDouble() != 0);
                else if (target is ToggleButton toggle) toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, args.GetProperty("value").GetDouble() != 0);
                else if (target is ComboBox combo) combo.SetCurrentValue(ComboBox.SelectedIndexProperty, args.GetProperty("value").GetInt32());
                else throw new ArgumentException("Unsupported fixture control.");
            }
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
            object ScreenPoint(Control control, bool grip = false)
            {
                var point = control.PointToScreen(new Point(grip ? Math.Max(1, control.Bounds.Width - 2) : control.Bounds.Width / 2, control.Bounds.Height / 2));
                return new { x = point.X, y = point.Y };
            }
#if INTEGRATION_TESTS
            double VisibleHeight(Control control)
            {
                var top = control.TranslatePoint(default, window)?.Y ?? 0;
                var bottom = control.TranslatePoint(new Point(0, control.Bounds.Height), window)?.Y ?? top;
                foreach (var scroll in control.GetVisualAncestors().OfType<ScrollViewer>())
                {
                    top = Math.Max(top, scroll.TranslatePoint(default, window)?.Y ?? top);
                    bottom = Math.Min(bottom, scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)?.Y ?? bottom);
                }
                return Math.Max(0, bottom - top);
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
                historySelectionText = controls.Where(c => c.Classes.Contains("history_commit_text") && c.IsEffectivelyVisible && c.DataContext is Models.Commit && c.FindAncestorOfType<DataGridRow>() != null)
                    .Select(c => new { sha = ((Models.Commit)c.DataContext).SHA, merged = ((Models.Commit)c.DataContext).IsMerged,
                        selected = c.FindAncestorOfType<DataGridRow>().IsSelected, opacity = c.Opacity, type = c.GetType().Name,
                        foreground = c switch { Views.CommitSubjectPresenter subject => subject.Foreground?.ToString(), TextBlock text => text.Foreground?.ToString(), _ => null } }).ToArray(),
                metadataDocuments = controls.OfType<Views.CommitMetadataText>().Where(t => t.IsEffectivelyVisible)
                    .Select(t => new { text = t.PlainText, selected = t.SelectedText, start = t.SelectionStart, end = t.SelectionEnd,
                        tooltip = ToolTip.GetTip(t)?.ToString(), tooltipOpen = ToolTip.GetIsOpen(t), cursor = t.Cursor?.ToString(), focused = t.IsFocused,
                        fontSize = t.FontSize, runFontSizes = t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.FontSize).Distinct().ToArray(),
                        parentColors = t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Where(r => r.TextDecorations?.Count > 0).Select(r => r.Foreground?.ToString()).Distinct().ToArray(),
                        maxRenderedParentLength = t.MaxRenderedParentLength,
                        bounds = Bounds(t), visibleHeight = VisibleHeight(t), spans = t.Spans.Select(s => new { name = s.Name, text = s.Text, start = s.Start, length = s.Length, sha = s.SHA, parent = s.IsParent, label = s.IsLabel }).ToArray(),
                        viewport = t.FindAncestorOfType<ScrollViewer>() is { } scroll ? new { bounds = Bounds(scroll), screen = ScreenPoint(scroll) } : null,
                        menu = t.ContextMenu is { IsOpen: true } menu ? menu.Items.OfType<MenuItem>().Select(i => new { name = i.Name, text = i.Header?.ToString(), enabled = i.IsEnabled, screen = ScreenPoint(i) }).ToArray() : null }).ToArray(),
                labels = controls.OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Take(500)
                    .Select(t => new { text = t.Text, bounds = Bounds(t), visibleHeight = VisibleHeight(t) }).ToArray(),
                sidebarCommitTints = controls.OfType<Border>().Where(b => b.Name == "CommitCoverageBackground" && b.IsEffectivelyVisible && b.DataContext is ViewModels.BranchTreeNode { Backend: Models.Branch })
                    .Select(b => new { reference = ((Models.Branch)((ViewModels.BranchTreeNode)b.DataContext).Backend).FullName,
                        level = ((ViewModels.BranchTreeNode)b.DataContext).CommitHighlightLevel, opacity = b.Opacity,
                        bounds = Bounds(b), color = b.Background?.ToString() }).ToArray(),
                sidebarCommitShades = controls.OfType<Border>().Where(b => b.Name == "CommitPrimaryShade" && b.IsEffectivelyVisible && b.DataContext is ViewModels.BranchTreeNode { Backend: Models.Branch })
                    .Select(b => new { reference = ((Models.Branch)((ViewModels.BranchTreeNode)b.DataContext).Backend).FullName, opacity = b.Opacity, color = b.Background?.ToString() }).ToArray(),
                sidebarManualSelections = controls.OfType<Grid>().Where(g => g.Name == "CommitBranchSelectedBackground" && g.IsEffectivelyVisible && g.DataContext is ViewModels.BranchTreeNode { Backend: Models.Branch })
                    .Select(g => new { reference = ((Models.Branch)((ViewModels.BranchTreeNode)g.DataContext).Backend).FullName, opacity = g.Opacity,
                        selected = g.FindAncestorOfType<ListBoxItem>()?.IsSelected, colors = g.Children.OfType<Border>().Select(b => b.Background?.ToString()).ToArray() }).ToArray(),
                searchRows = controls.OfType<DataGridRow>().Where(row => row.IsEffectivelyVisible && row.DataContext is Models.Commit && row.FindAncestorOfType<Views.CommitSearchView>() != null)
                    .Select(row => new { sha = ((Models.Commit)row.DataContext).SHA, screen = ScreenPoint(row), bounds = Bounds(row) }).ToArray(),
                selectedBranchBackground = window.FindResource("Brush.TreeGit.SelectionSecondary")?.ToString(),
                primaryBranchBackground = window.FindResource("Brush.TreeGit.Selection")?.ToString(),
                commitBranchHeaders = controls.OfType<Views.CommitBranchHeader>().Where(h => h.IsEffectivelyVisible &&
                    h.GetVisualDescendants().Any(c => c.Name == "OtherCommitBranchesBadge") &&
                    h.GetVisualDescendants().Any(c => c.Name == "MoreCommitBranches") &&
                    h.GetVisualDescendants().Any(c => c.Name == "CommitPrimaryBranch") &&
                    h.GetVisualDescendants().Any(c => c.Name == "PrimaryBranchScroll")).Select(h =>
                {
                    var badge = h.GetVisualDescendants().OfType<Border>().First(b => b.Name == "OtherCommitBranchesBadge");
                    var button = h.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MoreCommitBranches");
                    var primary = h.GetVisualDescendants().OfType<Views.CommitRefsPresenter>().First(p => p.Name == "CommitPrimaryBranch");
                    var viewport = h.GetVisualDescendants().OfType<ScrollViewer>().First(p => p.Name == "PrimaryBranchScroll");
                    return new { bounds = Bounds(h), badge = Bounds(badge), badgeVisible = badge.IsEffectivelyVisible,
                        badgeVisibleHeight = VisibleHeight(badge), buttonVisible = button.IsEffectivelyVisible,
                        buttonHasBorder = button.BorderThickness != default,
                        primary = Bounds(primary), primaryDesiredWidth = primary.DesiredSize.Width,
                        primaryDesiredHeight = primary.DesiredSize.Height, primaryViewport = Bounds(viewport) };
                }).ToArray(),
                columnHeaders = controls.OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0)
                    .Select(h => new { text = string.Join("", h.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)),
                        bounds = Bounds(h), screen = ScreenPoint(h, true) }).ToArray(),
#endif
                buttons = controls.OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0).Select(b =>
                {
                    var border = b.FindAncestorOfType<Border>();
                    var point = border == null ? null : b.TranslatePoint(default, border);
                    return new { name = b.Name, text = b.Content as string ?? string.Join("", b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), classes = b.Classes.ToArray(), enabled = b.IsEnabled, bounds = Bounds(b), screen = ScreenPoint(b),
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
                scrollBars = controls.OfType<ScrollBar>().Select((b, index) => new { index, name = b.Name,
                    visible = b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0, enabled = b.IsEnabled,
                    orientation = b.Orientation.ToString(), value = b.Value, minimum = b.Minimum, maximum = b.Maximum,
                    owner = b.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name, bounds = Bounds(b) }).ToArray(),
                bottomTabs = controls.OfType<Button>().Where(b => b.Classes.Contains("treegit_bottom_tab") && b.IsEffectivelyVisible)
                    .Select(b => new { text = b.Content as string, active = b.Classes.Contains("active"), bounds = Bounds(b),
                        content = b.GetVisualDescendants().OfType<TextBlock>().Select(t => Bounds(t)).ToArray() }).ToArray()
            };
        }
    }
}
