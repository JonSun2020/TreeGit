using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    // Routed input only: this probe never moves the OS pointer. Repository
    // actions run only through the real buttons in an isolated fixture.
    // It is absent from normal and portable builds.
    internal static class MenuToggleProbe
    {
        private static readonly Avalonia.Input.Pointer Pointer = new(7, PointerType.Mouse, true);
        private static readonly List<object> Events = [];
        private static Menu _menu;
        private static string _phase;
        private static Control _tooltipTarget;
        private static (int Type, string Executable, string Merge, string Diff)? _externalSettings;

        public static object Execute(Window window, JsonElement args)
        {
            if (Environment.GetEnvironmentVariable("TREEGIT_TEST_MENU_PROBE") != "1")
                throw new InvalidOperationException("An isolated menu fixture is required.");
            var menu = window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "PrimaryMenu");
            var headers = menu.Items.OfType<MenuItem>().ToArray();
            var language = headers[1].Items.OfType<MenuItem>().Single(i => i.Name == "LanguageMenu");
            var languageOptions = language.Items.OfType<MenuItem>().ToArray();
            if (_menu != menu)
            {
                _menu = menu;
                foreach (var item in headers)
                {
                    var header = item;
                    item.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == MenuItem.IsSubMenuOpenProperty)
                            Events.Add(new { phase = _phase, header = header.Header?.ToString(), open = header.IsSubMenuOpen });
                    };
                }
            }
            var action = args.TryGetProperty("text", out var text) ? text.GetString() : "inspect";
            var native = action.StartsWith("native_", StringComparison.Ordinal);
            if (native) action = action[7..];
            // Keep the real desktop pointer from racing synthetic header-entry
            // events when popup layout causes Avalonia to refresh hit testing.
            // Routed events below still exercise the normal menu handlers.
            window.IsHitTestVisible = true;
            _phase = action;
            var index = args.TryGetProperty("value", out var value) ? value.GetInt32() : 0;
            if (index < 0 || index >= headers.Length && !action.StartsWith("recent_", StringComparison.Ordinal)) throw new ArgumentOutOfRangeException(nameof(index));
            var selected = headers[Math.Min(index, headers.Length - 1)];
            object Bounds(Control control)
            {
                if (control == null) return null;
                var position = control.TranslatePoint(default, window);
                return new { x = position?.X, y = position?.Y, right = position?.X + control.Bounds.Width, width = control.Bounds.Width, height = control.Bounds.Height };
            }
            object PopupBounds(Control control, Visual popup)
            {
                var start = control.TranslatePoint(default, popup);
                var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), popup);
                return new { x = start?.X, y = start?.Y, right = end?.X, width = end?.X - start?.X, height = end?.Y - start?.Y };
            }
            var source = selected.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_HeaderPresenter") ?? selected;
            var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window) ?? default;
            object searchClick = null;
            if (action == "fit_desktop")
            {
                var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
                if (screen != null)
                {
                    var width = screen.WorkingArea.Width / screen.Scaling - 32;
                    var height = screen.WorkingArea.Height / screen.Scaling - 64;
                    window.MinWidth = Math.Min(window.MinWidth, width);
                    window.MinHeight = Math.Min(window.MinHeight, height);
                    window.Width = Math.Min(window.Width, width);
                    window.Height = Math.Min(window.Height, height);
                    window.Position = screen.WorkingArea.Position + new PixelPoint(8, 8);
                    window.InvalidateVisual();
                }
            }
            else if (action.StartsWith("sidebar_", StringComparison.Ordinal))
            {
                var view = window.GetVisualDescendants().OfType<Views.Repository>().First(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.Repository);
                var repo = (ViewModels.Repository)view.DataContext;
                var open = action != "sidebar_groups_close";
                repo.IsLocalBranchGroupExpanded = open;
                repo.IsRemoteGroupExpanded = open;
                foreach (var tree in new[] { view.LocalBranchTree, view.RemoteBranchTree })
                {
                    void Expand(List<ViewModels.BranchTreeNode> nodes)
                    {
                        foreach (var node in nodes)
                        {
                            if (node.Children.Count == 0) continue;
                            if (!node.IsExpanded) tree.ToggleNodeIsExpanded(node);
                            Expand(node.Children);
                        }
                    }
                    if (action == "sidebar_folders_open") Expand(tree.Nodes);
                    else if (action == "sidebar_folders_close")
                        foreach (var node in tree.Nodes.Where(n => n.Children.Count > 0 && n.IsExpanded).ToArray()) tree.ToggleNodeIsExpanded(node);
                }
            }
            else if (action == "external_unconfigured")
            {
                if (_externalSettings != null) throw new InvalidOperationException("Restore the previous tool fixture first.");
                _externalSettings = (Native.OS.ExternalMergerType, Native.OS.ExternalMergerExecFile, Native.OS.ExternalMergeArgs, Native.OS.ExternalDiffArgs);
                Native.OS.ExternalMergerType = 1;
                Native.OS.ExternalMergerExecFile = string.Empty;
            }
            else if (action == "external_restore")
            {
                if (_externalSettings is { } original)
                {
                    Native.OS.ExternalMergerType = original.Type;
                    Native.OS.ExternalMergerExecFile = original.Executable;
                    Native.OS.ExternalMergeArgs = original.Merge;
                    Native.OS.ExternalDiffArgs = original.Diff;
                    _externalSettings = null;
                }
            }
            else if (action == "merge_reopen")
            {
                var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var editor = desktop.Windows.OfType<Views.MergeConflictEditor>().Single(w => w.IsVisible);
                Views.ControlExtensions.ShowWindow(window, editor.DataContext);
            }
            else if (action == "reset") { menu.Close(); Events.Clear(); }
            else if (action == "popup_combo")
                window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible).IsDropDownOpen = true;
            else if (action == "popup_tooltip")
            {
                _tooltipTarget = window.GetVisualDescendants().OfType<Control>().First(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && ToolTip.GetTip(c) != null);
                ToolTip.SetIsOpen(_tooltipTarget, true);
            }
            else if (action == "popup_close")
            {
                foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>().ToArray()) combo.IsDropDownOpen = false;
                if (_tooltipTarget != null) ToolTip.SetIsOpen(_tooltipTarget, false);
                menu.Close();
            }
            else if (action is "header_hover" or "header_press" or "header_reset")
            {
                var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsEffectivelyVisible);
                foreach (var header in grid.GetVisualDescendants().OfType<DataGridColumnHeader>())
                {
                    var state = (Avalonia.Controls.IPseudoClasses)header.Classes;
                    state.Set(":pointerover", action == "header_hover");
                    state.Set(":pressed", action == "header_press");
                }
            }
            else if (action == "search_popup_snapshot")
            {
                var popup = window.GetVisualDescendants().OfType<Views.CommitSearchView>().Select(v => v.ContainingBranchesSurface).First(v => v != null);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Bounds.Width), (int)Math.Ceiling(popup.Bounds.Height)), new Vector(96, 96));
                bitmap.Render(popup);
                using var stream = new MemoryStream();
                bitmap.Save(stream);
                return new { png = Convert.ToBase64String(stream.ToArray()) };
            }
            else if (action is "search_click_blank" or "search_doubletap_blank" or "history_click_blank")
            {
                var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.IsEffectivelyVisible && (action == "history_click_blank" ? g.Name == "CommitListContainer" : g.Name == "SearchResults"));
                grid.UpdateLayout();
                var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.IsEffectivelyVisible && r.DataContext is Models.Commit && r.DataContext != grid.SelectedItem);
                grid.ScrollIntoView(row.DataContext, grid.Columns[index]);
                grid.UpdateLayout();
                var cells = row.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 1).OrderBy(c => c.TranslatePoint(default, row)?.X).ToArray();
                var cell = cells[index];
                var cellStart = cell.TranslatePoint(default, window) ?? throw new InvalidOperationException("Cell has no window coordinates.");
                var gridStart = grid.TranslatePoint(default, window) ?? throw new InvalidOperationException("Grid has no window coordinates.");
                var visible = new Rect(cellStart, cell.Bounds.Size).Intersect(new Rect(gridStart, grid.Bounds.Size)).Intersect(new Rect(window.ClientSize));
                foreach (var bar in grid.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.IsEffectivelyVisible && b.Orientation == Avalonia.Layout.Orientation.Vertical))
                {
                    var start = bar.TranslatePoint(default, window);
                    if (start.HasValue && start.Value.X > visible.Left && start.Value.X < visible.Right)
                        visible = new Rect(visible.TopLeft, new Size(start.Value.X - visible.Left, visible.Height));
                }
                if (visible.Width < 4 || visible.Height < 4) throw new InvalidOperationException("Blank cell space did not receive input.");
                var position = new Point(visible.Right - 2, visible.Center.Y);
                var hit = window.InputHitTest(position) as Control ?? throw new InvalidOperationException("Blank cell space did not receive input.");
                if (hit != grid && hit.FindAncestorOfType<DataGrid>() != grid)
                    throw new InvalidOperationException("The rendered hit-test scene has not caught up with the table layout.");
                var hitCell = hit as DataGridCell ?? hit.FindAncestorOfType<DataGridCell>();
                var pressed = new PointerPressedEventArgs(hit, Pointer, window, position, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1);
                hit.RaiseEvent(pressed);
                hit.RaiseEvent(new PointerReleasedEventArgs(hit, Pointer, window, position, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                Pointer.Capture(null);
                if (action == "search_doubletap_blank") hit.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, pressed));
                searchClick = new { expected = ((Models.Commit)row.DataContext).SHA, selected = (grid.SelectedItem as Models.Commit)?.SHA,
                    windowSize = window.ClientSize.ToString(),
                    hit = hit.GetType().Name, hitCell = hitCell == cell, position = position.ToString(), cell = Bounds(cell),
                    ancestors = hit.GetVisualAncestors().Select(v => v.GetType().Name + ":" + (v as Control)?.Name).ToArray() };
            }
            else if (action is "search_focus" or "search_down")
            {
                var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SearchResults" && g.IsEffectivelyVisible);
                grid.CurrentColumn = grid.Columns[1];
                grid.Focus(NavigationMethod.Pointer);
                if (action == "search_down") grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            }
            else if (action == "column_drag")
            {
                var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsEffectivelyVisible);
                var columnHeader = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                    .Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0 && h.Content != null)
                    .OrderBy(h => h.TranslatePoint(default, grid)?.X).ElementAt(index);
                var start = columnHeader.TranslatePoint(new Point(columnHeader.Bounds.Width - 1, columnHeader.Bounds.Height / 2), window) ?? default;
                var distance = args.GetProperty("dx").GetDouble();
                columnHeader.RaiseEvent(new PointerPressedEventArgs(columnHeader, Pointer, window, start, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                columnHeader.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, columnHeader, Pointer, window, start.WithX(start.X + distance), (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                columnHeader.RaiseEvent(new PointerReleasedEventArgs(columnHeader, Pointer, window, start.WithX(start.X + distance), (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            }
            else if (action == "language_open")
            {
                menu.Open(); headers[1].IsSubMenuOpen = true;
                window.UpdateLayout();
                language.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, language, Pointer, window,
                    default, (ulong)Environment.TickCount64, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
            }
            else if (action == "language_hover")
            {
                if (index >= languageOptions.Length) throw new ArgumentOutOfRangeException(nameof(index));
                var option = languageOptions[index];
                option.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, option, Pointer, window,
                    default, (ulong)Environment.TickCount64, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
            }
            else if (action == "language_choose") languageOptions[index].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (action == "language_leave") language.IsSubMenuOpen = false;
            else if (action == "language_exit")
            {
                var popup = typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(language) as Popup;
                var root = popup?.Child ?? throw new InvalidOperationException("Language popup is not open.");
                root.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, root, Pointer, window,
                    default, (ulong)Environment.TickCount64, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
            }
            else if (action == "press")
                source.RaiseEvent(new PointerPressedEventArgs(source, Pointer, window, point, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            else if (action == "release")
                source.RaiseEvent(new PointerReleasedEventArgs(source, Pointer, window, point, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            else if (action == "enter") selected.RaiseEvent(new RoutedEventArgs(MenuItem.PointerEnteredItemEvent));
            else if (action is "escape" or "down" or "right") selected.RaiseEvent(new KeyEventArgs
                { RoutedEvent = InputElement.KeyDownEvent, Key = action == "escape" ? Key.Escape : action == "down" ? Key.Down : Key.Right });
            else if (action == "outside") window.RaiseEvent(new PointerPressedEventArgs(window, Pointer, window,
                new Point(window.Bounds.Width / 2, window.Bounds.Height - 40), (ulong)Environment.TickCount64,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            else if (action == "page_tab")
            {
                var tabs = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("treegit_bottom_tab") && b.IsEffectivelyVisible).ToArray();
                if (tabs.Length != 3 || index >= tabs.Length) throw new InvalidOperationException("Expected search, history and commit tabs.");
                if (!tabs[index].IsEffectivelyEnabled) throw new InvalidOperationException("The requested page is disabled.");
                tabs[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (action == "history_pending")
            {
                var pending = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WorkingCopyRow" && b.IsEffectivelyVisible);
                pending.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (action == "history_collapse")
            {
                var collapse = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "CollapseDetailsButton" && b.IsEffectivelyVisible);
                collapse.SetCurrentValue(ToggleButton.IsCheckedProperty, index != 0);
            }
            else if (action == "commit_toolbar")
            {
                var toolbar = window.GetVisualDescendants().OfType<Views.RepositoryToolbar>().Single(t => t.IsEffectivelyVisible);
                var commit = toolbar.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible);
                commit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (action == "commit_push_after")
            {
                var checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "PushImmediatelyCheckBox" && c.IsEffectivelyVisible);
                if (!checkbox.IsEffectivelyEnabled) throw new InvalidOperationException("Push-after-commit is disabled.");
                checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, index != 0);
            }
            else if (action == "commit_hidden")
            {
                var hidden = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single(v => !v.IsOverview && !v.IsEffectivelyVisible);
                var commit = hidden.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CommitStagedButton");
                commit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (action == "commit_staged")
            {
                var commit = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CommitStagedButton" && b.IsEffectivelyVisible);
                if (!commit.IsEffectivelyEnabled) throw new InvalidOperationException("The staged commit button is disabled.");
                commit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (action == "commit_signoff")
            {
                var signoff = window.GetVisualDescendants().OfType<CheckBox>().Single(button => button.Name == "SignOffCheckBox" && button.IsEffectivelyVisible);
                signoff.SetCurrentValue(ToggleButton.IsCheckedProperty, index != 0);
            }
            else if (action.StartsWith("recent_", StringComparison.Ordinal))
            {
                var working = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Single(view => !view.IsOverview && view.IsEffectivelyVisible);
                if (action == "recent_toggle")
                    working.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RecentCommitMessagesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (action == "recent_caret")
                {
                    var editor = working.GetVisualDescendants().OfType<Views.CommitMessageTextBox>().Single();
                    if (index > (editor.Text?.Length ?? 0)) throw new ArgumentOutOfRangeException(nameof(index));
                    editor.CaretIndex = index; editor.SelectionStart = editor.SelectionEnd = index;
                }
                else if (action == "recent_outside")
                {
                    var overlay = window.GetVisualDescendants().OfType<Control>().Last(control => control.GetType().Name == "LightDismissOverlayLayer");
                    overlay.RaiseEvent(new PointerPressedEventArgs(overlay, Pointer, window, new Point(1, 1), (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                }
                else
                {
                    var view = working.RecentMessagesView ?? throw new InvalidOperationException("Recent commit messages are closed.");
                    var rows = view.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("commit_message_choice")).ToArray();
                    if (index >= rows.Length) throw new ArgumentOutOfRangeException(nameof(index));
                    if (action == "recent_choose") rows[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (action == "recent_hover") ((IPseudoClasses)rows[index].Classes).Set(":pointerover", true);
                    else throw new ArgumentException("Unknown recent-message fixture action.");
                }
            }
            else if (action is not ("inspect" or "finish" or "catalog")) throw new ArgumentException("Unsupported menu fixture action.");
            window.UpdateLayout();
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            return new
            {
                loadingHistoryPopupVisible = window.GetVisualDescendants().OfType<Views.WorkingCopy>().Any(view => view.IsLoadingHistoryPopupVisible),
                resources = new { workingSet = currentProcess.WorkingSet64, privateBytes = currentProcess.PrivateMemorySize64,
                    handles = currentProcess.HandleCount, threads = currentProcess.Threads.Count },
                menuOpen = menu.IsOpen,
                toolbarLabels = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("toolbar_label"))
                    .Select(t => new { text = t.Text, width = t.Bounds.Width,
                        textWidth = new Avalonia.Media.FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, t.FlowDirection,
                            new Avalonia.Media.Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, t.Foreground).WidthIncludingTrailingWhitespace }).ToArray(),
                hitTestEnabled = window.IsHitTestVisible,
                tableHeaders = window.GetVisualDescendants().OfType<DataGridColumnHeader>().Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0)
                    .Select(h => new
                    {
                        text = string.Join("", h.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)),
                        bounds = Bounds(h),
                        sharedTheme = ReferenceEquals(h.Theme, window.FindResource("TreeGit.CommitTableHeader")),
                        backgrounds = h.GetVisualDescendants().OfType<Border>().Select(b => b.Background?.ToString()).ToArray(),
                        textCenters = h.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                            .Select(t => t.TranslatePoint(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2), h))
                            .Select(p => new { x = p?.X, y = p?.Y }).ToArray(),
                        separators = h.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Where(r => r.Name == "VerticalSeparator" && r.IsEffectivelyVisible)
                            .Select(r => new { y = r.TranslatePoint(default, h)?.Y, height = r.Bounds.Height }).ToArray(),
                    }).ToArray(),
                searchClick,
                searchSessions = window.GetVisualDescendants().OfType<Views.Repository>().Where(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.Repository)
                    .Select(v => (ViewModels.Repository)v.DataContext).Select(r => new {
                        repo = r.FullPath, runs = r.Histories.SearchCommitContext.QueryCount,
                        results = r.Histories.SearchCommitContext.Results is { } list ? RuntimeHelpers.GetHashCode(list) : 0,
                        selected = r.Histories.SearchCommitContext.Selected?.SHA,
                        detail = r.Histories.DetailContext is ViewModels.CommitDetail d ? RuntimeHelpers.GetHashCode(d) : 0,
                        width = r.ChangesPanelWidth.Value,
                        localExpanded = r.IsLocalBranchGroupExpanded, remoteExpanded = r.IsRemoteGroupExpanded,
                    }).ToArray(),
                searchRows = window.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible && r.DataContext is Models.Commit && r.FindAncestorOfType<DataGrid>()?.Name == "SearchResults")
                    .Select(r => new {
                        sha = ((Models.Commit)r.DataContext).SHA,
                        branchForeground = r.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "SearchBranchName").Foreground?.ToString(),
                        descriptionForeground = r.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 1).OrderBy(c => c.TranslatePoint(default, r)?.X).ElementAt(1).GetVisualDescendants().OfType<TextBlock>().First().Foreground?.ToString(),
                        branchOpacity = r.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SearchBranchButton").GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").Opacity,
                    }).ToArray(),
                sidebarBranches = window.GetVisualDescendants().OfType<ListBoxItem>().Where(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.BranchTreeNode { Backend: Models.Branch })
                    .Select(v => new {
                        reference = ((Models.Branch)((ViewModels.BranchTreeNode)v.DataContext).Backend).FullName,
                        level = ((ViewModels.BranchTreeNode)v.DataContext).CommitHighlightLevel,
                        weights = v.GetVisualDescendants().OfType<TextBlock>().Where(t => t.DataContext == v.DataContext).Select(t => t.FontWeight.ToString()).ToArray(),
                        bold = v.GetVisualDescendants().OfType<TextBlock>().Any(t => t.DataContext == v.DataContext && t.FontWeight == Avalonia.Media.FontWeight.Bold),
                    }).ToArray(),
                sidebarGroups = window.GetVisualDescendants().OfType<ToggleButton>().Where(v => v.IsEffectivelyVisible && v.Name is "LocalBranchGroup" or "RemoteBranchGroup")
                    .Select(v => new { name = v.Name, expanded = v.IsChecked, covered = v.Classes.Contains("commit_covered"), background = v.Background?.ToString(),
                        renderedBackground = v.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "GroupExpanderBackground")?.Background?.ToString() }).ToArray(),
                sidebarFolders = window.GetVisualDescendants().OfType<ListBoxItem>().Where(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.BranchTreeNode { IsBranch: false })
                    .Select(v => new { path = ((ViewModels.BranchTreeNode)v.DataContext).Path, expanded = ((ViewModels.BranchTreeNode)v.DataContext).IsExpanded,
                        level = ((ViewModels.BranchTreeNode)v.DataContext).CommitHighlightLevel }).ToArray(),
                sidebarClearances = window.GetVisualDescendants().OfType<Views.BranchTree>().Where(v => v.IsEffectivelyVisible)
                    .Select(v => new { name = v.Name,
                        rows = v.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "CommitPrimaryShade" && b.IsEffectivelyVisible && b.Bounds.Width > 0).Select(Bounds).ToArray(),
                        bars = v.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.IsEffectivelyVisible && b.Bounds.Width > 0).Select(Bounds).ToArray(),
                    }).ToArray(),
                searchHeaders = window.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault(g => g.Name == "SearchResults")?.Columns.Select(c => c.Header?.ToString()).ToArray(),
                branchPopups = window.GetVisualDescendants().OfType<Views.CommitSearchView>().Select(v => v.ContainingBranchesSurface).OfType<Views.SearchBranches>().Where(v => v.IsEffectivelyVisible)
                    .Select(v => new {
                        rows = v.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "BranchName" && t.IsEffectivelyVisible && t.DataContext is ViewModels.SearchBranchRow { IsBranch: true })
                            .Select(t => new { reference = ((ViewModels.SearchBranchRow)t.DataContext).Branch.FullName, weight = t.FontWeight.ToString() }).ToArray(),
                        labels = v.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "CurrentBranchLabel" && t.IsEffectivelyVisible).Select(t => new { bounds = PopupBounds(t, v), desiredWidth = t.DesiredSize.Width - t.Margin.Left - t.Margin.Right }).ToArray(),
                        bars = v.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.IsEffectivelyVisible && b.Bounds.Width > 0).Select(b => PopupBounds(b, v)).ToArray(),
                    }).ToArray(),
                searchCells = window.GetVisualDescendants().OfType<DataGridCell>().Where(c => c.IsEffectivelyVisible && c.FindAncestorOfType<DataGrid>()?.Name == "SearchResults")
                    .Select(c => new { focusAdorner = c.FocusAdorner?.GetType().Name,
                        focusBorders = c.GetVisualDescendants().OfType<Control>().Where(v => v.Name?.Contains("Focus", StringComparison.OrdinalIgnoreCase) == true && v.IsEffectivelyVisible).Select(v => v.Name).ToArray() }).ToArray(),
                overlayHosts = window.GetVisualDescendants().OfType<OverlayPopupHost>().Count(h => h.IsEffectivelyVisible),
                popups = window.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen)
                    .Select(p => new { name = p.Name, overlay = p.IsUsingOverlayLayer }).ToArray(),
                activeLocale = App.ActiveLocale,
                savedLocale = ViewModels.Preferences.Instance.Locale,
                textCatalog = action == "catalog" && App.Current.Resources[App.ActiveLocale] is ResourceDictionary dictionary
                    ? dictionary.Keys.OfType<string>().Where(key => key.StartsWith("Text.", StringComparison.Ordinal))
                        .ToDictionary(key => key, key => App.Text(key[5..])) : null,
                editorSearchMessages = new[] { AvaloniaEdit.SR.SearchNoMatchesFoundText, AvaloniaEdit.SR.Search1Match,
                        string.Format(AvaloniaEdit.SR.SearchXMatches, 12), string.Format(AvaloniaEdit.SR.SearchXOfY, 3, 12) }
                    .Select(Views.LocalizedSearchMessage.Translate).ToArray(),
                supportedLocales = Models.Locale.Supported.Select(l => l.Key).ToArray(),
                languageMenu = new
                {
                    first = headers[1].Items.OfType<MenuItem>().First().Name,
                    text = language.Header?.ToString(),
                    open = language.IsSubMenuOpen,
                    overlay = (typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(language) as Popup)?.IsUsingOverlayLayer,
                    targetBounds = Bounds(language),
                    popupBounds = Bounds((typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(language) as Popup)?.Child),
                    options = languageOptions.Select(option => new
                    {
                        key = option.Name?["Language_".Length..],
                        name = (option.Header as Grid)?.Children.OfType<TextBlock>().First().Text,
                        selected = (option.Header as Grid)?.Children.OfType<Avalonia.Controls.Shapes.Path>().First().IsVisible,
                        checkColor = (option.Header as Grid)?.Children.OfType<Avalonia.Controls.Shapes.Path>().First().Fill?.ToString(),
                    }).ToArray(),
                },
                headers = headers.Select((h, i) => new { index = i, text = h.Header?.ToString(), open = h.IsSubMenuOpen, selected = h.IsSelected, childSelected = h.SelectedIndex,
                    popup = (typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(h) as Popup)?.IsOpen,
                    outlineVisible = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.IsVisible,
                    outlineBrush = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.BorderBrush?.ToString(),
                    bounds = h.Bounds.ToString(),
                    screenX = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).X,
                    screenY = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).Y }).ToArray(),
                events = Events.ToArray(),
            };
        }
    }
}
