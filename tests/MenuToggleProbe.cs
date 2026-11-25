using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        private static bool? _autoHideBeforeDropdownAudit;

        private static Control FindDiffLineDrawing(AvaloniaEdit.Rendering.TextView view, AvaloniaEdit.Rendering.VisualLine line) =>
            line == null ? null : view.GetVisualDescendants().OfType<Control>().FirstOrDefault(visual =>
                visual.GetType().Name == "VisualLineDrawingVisual" &&
                ReferenceEquals(visual.GetType().GetProperty("VisualLine")?.GetValue(visual), line));

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendCaptionMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static object Execute(Window window, JsonElement args)
        {
            if (Environment.GetEnvironmentVariable("TREEGIT_TEST_MENU_PROBE") != "1")
                throw new InvalidOperationException("An isolated menu fixture is required.");
            var menu = window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "PrimaryMenu");
            var headers = menu.Items.OfType<MenuItem>().ToArray();
            var settings = headers.Single(i => i.Name == "SettingsMenu");
            var language = settings.Items.OfType<MenuItem>().Single(i => i.Name == "LanguageMenu");
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
            if (index < 0 || index >= headers.Length && !action.StartsWith("recent_", StringComparison.Ordinal) && !action.StartsWith("welcome_", StringComparison.Ordinal) && !action.StartsWith("diff_", StringComparison.Ordinal) && action is not ("operation_probe" or "tab_guard" or "tag_sidebar" or "dropdown_geometry")) throw new ArgumentOutOfRangeException(nameof(index));
            var selected = headers[Math.Min(index, headers.Length - 1)];
            object Bounds(Control control)
            {
                if (control == null) return null;
                var position = control.TranslatePoint(default, window);
                var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window);
                return new { x = position?.X, y = position?.Y, right = end?.X,
                    width = end?.X - position?.X ?? control.Bounds.Width, height = end?.Y - position?.Y ?? control.Bounds.Height };
            }
            object PopupBounds(Control control, Visual popup)
            {
                var start = control.TranslatePoint(default, popup);
                var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), popup);
                return new { x = start?.X, y = start?.Y, right = end?.X, width = end?.X - start?.X, height = end?.Y - start?.Y };
            }
            var source = selected.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_HeaderPresenter") ?? selected;
            var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window) ?? default;
            static double? TextCenter(TextBlock text, Visual reference)
            {
                if (text == null || reference == null || !text.IsArrangeValid ||
                    text.TextLayout.TextLines.FirstOrDefault() is not { } line)
                    return null;
                return text.TranslatePoint(new Point(text.Padding.Left + line.Start + line.Width / 2, 0), reference)?.X;
            }
            static double? TextStart(TextBlock text, Visual reference)
            {
                if (text == null || !text.IsArrangeValid || text.TextLayout.TextLines.FirstOrDefault() is not { } line)
                    return null;
                return text.TranslatePoint(new Point(text.Padding.Left + line.Start, 0), reference)?.X;
            }
            static bool CaptionFits(TextBlock text, Control area, Thickness inset)
            {
                if (area == null || text == null || !text.IsArrangeValid ||
                    text.TextLayout.TextLines.FirstOrDefault() is not { HasCollapsed: false } line)
                    return false;

                var x = text.TranslatePoint(new Point(text.Padding.Left + line.Start, 0), area)?.X;
                return x.HasValue && x.Value + Math.Min(0, line.OverhangLeading) >= inset.Left + 1 &&
                    x.Value + line.Width - Math.Min(0, line.OverhangTrailing) <= area.Bounds.Width - inset.Right - 1;
            }
            if (action == "content_selection")
            {
                var views = window.GetVisualDescendants().ToArray();
                var metadata = views.OfType<Views.CommitMetadataText>().FirstOrDefault(c => c.IsEffectivelyVisible && c.Spans.Count > 0);
                var diff = views.OfType<Views.ThemedTextDiffPresenter>().FirstOrDefault(c => c.IsEffectivelyVisible);
                var entry = views.OfType<TextBox>().FirstOrDefault(c => c.IsEffectivelyVisible);
                if (metadata == null || diff == null || entry == null)
                    return new { ready = false };

                var anchor = metadata.Spans.First(s => s.IsLabel).Length + 10;
                metadata.ClearSelection();
                var before = metadata.TextLayout.HitTestTextPosition(anchor);
                metadata.SelectionStart = 1;
                metadata.SelectionEnd = anchor;
                window.UpdateLayout();
                var after = metadata.TextLayout.HitTestTextPosition(anchor);
                var stable = after == before && metadata.SelectionForegroundBrush == null;
                var selectedText = metadata.SelectedText;
                if (index == 0) metadata.ClearSelection();
                var files = new Views.PathTextBox { Text = @"C:/demo/sample\file.txt" };
                var addresses = new[] { "C:/demo/repo.git", "//server/share/repo.git", "https://example.invalid/a/b.git", "git@example.invalid:a/b.git", "../other/demo.git" };
                return new
                {
                    ready = true, stable, selectedText, before = before.ToString(), after = after.ToString(),
                    selectionForegroundOverride = metadata.SelectionForegroundBrush?.ToString(),
                    expectedText = metadata.PlainText[1..anchor],
                    primary = window.FindResource("Brush.TreeGit.Selection")?.ToString(),
                    primaryForeground = window.FindResource("Brush.TreeGit.SelectionForeground")?.ToString(),
                    backgrounds = new[] { metadata.SelectionBrush?.ToString(), diff.TextArea.SelectionBrush?.ToString(), entry.SelectionBrush?.ToString() },
                    foregrounds = new[] { metadata.SelectedGlyphBrush?.ToString(), diff.TextArea.SelectionForeground?.ToString(), entry.SelectionForegroundBrush?.ToString() },
                    diffBorder = diff.TextArea.SelectionBorder != null,
                    local = files.Text,
                    remote = addresses.Select(value => new Views.RemoteAddressTextBox { Text = value }.Text).ToArray(),
                };
            }
            if (action == "repository_config_tabs")
            {
                var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var dialog = lifetime.Windows.OfType<Views.RepositorySettings>().Single(w => w.IsVisible);
                var controls = dialog.GetVisualDescendants().OfType<Control>().ToArray();
                var tabs = new[] { "RepositoryRemoteTab", "RepositoryAdvancedTab" }.Select(name => controls.OfType<Button>().Single(c => c.Name == name)).ToArray();
                if (index is 1 or 2)
                    tabs[index - 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var title = controls.OfType<TextBlock>().Single(c => c.Name == "DialogTitleText");
                return new
                {
                    windowTitle = dialog.Title, heading = title.Text, toolbar = App.Text("TreeGit.Toolbar.Settings"),
                    tabs = tabs.Select(button =>
                    {
                        var caption = ((StackPanel)button.Content).Children.OfType<TextBlock>().Single();
                        return new { title = caption.Text, active = button.Classes.Contains("selected"),
                            background = button.Background?.ToString(), foreground = caption.Foreground?.ToString() };
                    }).ToArray(),
                };
            }
            if (action == "stash_captions")
            {
                var box = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "StashModePicker");
                if (args.TryGetProperty("button", out var operation))
                    box.IsDropDownOpen = operation.GetString() == "open";
                var area = box.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ContentPresenter");
                var popup = box.GetVisualDescendants().OfType<Popup>().Single(c => c.Name == "PART_Popup");
                object[] Captions(Visual root) => root?.GetVisualDescendants().OfType<TextBlock>()
                    .Where(c => c.DataContext is Models.DealWithChangesAfterStashing && c.IsEffectivelyVisible).Select(c =>
                        (object)new { text = c.Text, fits = c.IsArrangeValid && c.TextLayout.Height <= c.Bounds.Height + 1 &&
                            c.TextLayout.TextLines.All(line => !line.HasCollapsed && line.Width <= c.Bounds.Width + 1) }).ToArray() ?? [];
                return new { width = box.Bounds.Width, closed = Captions(area), options = Captions(popup.Child) };
            }
            if (action == "commit_file_selection")
            {
                var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(c =>
                    c.Classes.Contains("file_selection") && c.Name != "SearchSuggestionBox" &&
                    c.IsEffectivelyVisible && c.FindAncestorOfType<Views.CommitDetail>() != null);
                if (list == null || list.ItemCount == 0)
                    return new { ready = false };

                list.SelectedIndex = 0;
                list.ScrollIntoView(0);
                window.UpdateLayout();
                var row = list.ContainerFromIndex(0) as ListBoxItem;
                var presenter = row?.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
                return new
                {
                    ready = row?.IsSelected == true && presenter != null,
                    background = presenter?.Background?.ToString(),
                    foreground = presenter?.Foreground?.ToString(),
                    labels = row?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                        .Select(t => t.Foreground?.ToString()).ToArray(),
                    expectedBackground = window.FindResource("Brush.TreeGit.Selection")?.ToString(),
                    expectedForeground = window.FindResource("Brush.TreeGit.SelectionForeground")?.ToString(),
                };
            }
            object searchClick = null;
            if (action is "settings_expanders" or "settings_remote_selection" or "settings_default_remote" or "settings_remote_form")
            {
                var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var dialog = desktop.Windows.Last(w => w.IsVisible && w is Views.RepositorySettings or Views.RepositoryRemoteEditor);
                if (action == "settings_remote_form")
                {
                    var controls = dialog.GetVisualDescendants().OfType<Control>().ToArray();
                    var editors = new[] { "RemoteEditorName", "RemoteEditorPath", "RemoteEditorSshKey" }
                        .Select(name => controls.OfType<TextBox>().Single(c => c.Name == name)).ToArray();
                    var fields = (Grid)editors[0].Parent;
                    var labels = fields.Children.OfType<TextBlock>().OrderBy(Grid.GetRow).ToArray();
                    var browse = controls.OfType<Button>().Single(b => b.Name == "RemoteEditorBrowseSshKey");
                    Point At(Control c, double x = 0, double y = 0) => c.TranslatePoint(new Point(x, y), fields).Value;
                    var gaps = labels.Zip(editors, (label, input) => At(input).X - At(label, label.Bounds.Width).X).ToArray();
                    return new
                    {
                        captions = labels.Select(label => label.Text).ToArray(),
                        rightAligned = labels.All(label => label.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right) && gaps.All(gap => Math.Abs(gap - 16) <= 1),
                        inline = labels.Zip(editors, (label, input) => Math.Abs(At(label, y: label.Bounds.Height / 2).Y - At(input, y: input.Bounds.Height / 2).Y) <= 1).All(value => value),
                        keyVisible = editors[2].IsEffectivelyVisible, keyHint = editors[2].Watermark?.ToString(),
                        hasExpander = controls.OfType<Expander>().Any(),
                        browseStyle = browse.Classes.Contains("flat") && !browse.Classes.Contains("dialog_action") && browse.Padding == new Thickness(10, 0) &&
                            Math.Abs(browse.Bounds.Height - editors[2].Bounds.Height) <= 1 && Math.Abs(At(browse).X - At(editors[2], editors[2].Bounds.Width).X - 6) <= 1 &&
                            Math.Abs(At(browse, y: browse.Bounds.Height / 2).Y - At(editors[2], y: editors[2].Bounds.Height / 2).Y) <= 1,
                    };
                }
                if (action == "settings_default_remote")
                {
                    var picker = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "DefaultRemote");
                    if (index == 1)
                        picker.IsDropDownOpen = true;
                    else if (index > 1 && picker.IsDropDownOpen)
                    {
                        var item = (ComboBoxItem)picker.ContainerFromIndex(index - 2);
                        var host = TopLevel.GetTopLevel(item);
                        var xy = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), host).Value;
                        item.RaiseEvent(new PointerPressedEventArgs(item, Pointer, host, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                        item.RaiseEvent(new PointerReleasedEventArgs(item, Pointer, host, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                        Pointer.Capture(null);
                    }
                    var caption = picker.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(t => t.IsEffectivelyVisible && t.FindAncestorOfType<ComboBoxItem>() == null);
                    var optionLabels = Enumerable.Range(0, picker.ItemCount).Select(i => picker.ContainerFromIndex(i)?
                        .GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible)?.Text).ToArray();
                    return new { open = picker.IsDropDownOpen, selected = picker.SelectedIndex, remote = picker.SelectedItem as string,
                        caption = caption?.Text,
                        optionLabels };
                }
                if (action == "settings_remote_selection")
                {
                    var grid = dialog.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "RepositoryRemotes");
                    static DataGridCell[] RemoteCells(DataGridRow row) => row?.GetVisualDescendants().OfType<DataGridCell>()
                        .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 1 && c.Content is TextBlock)
                        .OrderBy(c => c.TranslatePoint(default, row)?.X).ToArray();
                    bool? focusedFrameVisible = null;
                    if (index == 0)
                        grid.SelectedIndex = 0;
                    else
                    {
                        var firstRow = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.IsEffectivelyVisible && r.DataContext is Models.Remote);
                        var cell = RemoteCells(firstRow)[index - 1];
                        var xy = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), dialog).Value;
                        cell.RaiseEvent(new PointerPressedEventArgs(cell, Pointer, dialog, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                        cell.RaiseEvent(new PointerReleasedEventArgs(cell, Pointer, dialog, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                        Pointer.Capture(null);
                        // A hidden QA window cannot always take desktop focus.
                        var pseudoClasses = (IPseudoClasses)cell.Classes;
                        var wasFocused = pseudoClasses.Contains(":focus");
                        pseudoClasses.Set(":focus", true);
                        focusedFrameVisible = cell.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "FocusVisual").IsEffectivelyVisible;
                        pseudoClasses.Set(":focus", wasFocused);
                    }
                    grid.UpdateLayout();
                    var row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsSelected);
                    var fill = row?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Single(r => r.Name == "BackgroundRectangle");
                    var headings = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                        .Where(h => h.IsEffectivelyVisible && h.Bounds.Width > 0 && h.Content is TextBlock).OrderBy(h => h.TranslatePoint(default, grid)?.X)
                        .Select(h => new { text = ((TextBlock)h.Content).Text, centerOffset = TextCenter((TextBlock)h.Content, h) - h.Bounds.Width / 2 }).ToArray();
                    var cells = RemoteCells(row);
                    var textPlacement = cells?.Select(c =>
                    {
                        var caption = (TextBlock)c.Content;
                        return new { centerOffset = TextCenter(caption, c) - c.Bounds.Width / 2,
                            start = caption?.TranslatePoint(default, c)?.X, alignment = caption?.TextAlignment.ToString() };
                    }).ToArray();
                    var separators = row?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>()
                        .Where(r => r.IsEffectivelyVisible && r.Fill != null).Select(r => r.Name).ToArray();
                    return new { fill = fill?.Fill?.ToString(), opacity = fill?.Opacity, headings, textPlacement,
                        gridLines = separators?.Contains("PART_BottomGridLine") == true && separators.Contains("PART_RightGridLine"),
                        column = grid.Columns.IndexOf(grid.CurrentColumn), focusedFrameVisible,
                        foregrounds = row?.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Foreground?.ToString()).ToArray(),
                        expectedFill = dialog.FindResource("Brush.TreeGit.Selection")?.ToString(),
                        expectedForeground = dialog.FindResource("Brush.TreeGit.SelectionForeground")?.ToString() };
                }

                var requestedExpander = args.TryGetProperty("button", out var expanderName) ? expanderName.GetString() : null;
                var expanders = dialog.GetVisualDescendants().OfType<Expander>()
                    .Where(e => e.Classes.Contains("settings_expander") && (requestedExpander == null || e.Name == requestedExpander)).ToArray();
                var rows = expanders.Select(e =>
                {
                    var header = e.GetVisualDescendants().OfType<ToggleButton>().Single(h => h.Name == "ExpanderHeader");
                    var background = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToggleButtonBackground");
                    var chevron = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ExpandCollapseChevronBorder");
                    var caption = header.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                    static bool Transparent(Avalonia.Media.IBrush brush) => brush is Avalonia.Media.ISolidColorBrush { Color.A: 0 } or null;
                    bool HeaderIsTransparent() => Transparent(background.Background) && Transparent(chevron.Background) && Transparent(caption.Background);
                    bool? pressedTransparent = null;
                    if (index > 0 && e.IsExpanded != (index == 1))
                    {
                        header.BringIntoView();
                        dialog.UpdateLayout();
                        var xy = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), dialog).Value;
                        header.RaiseEvent(new PointerPressedEventArgs(header, Pointer, dialog, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                        pressedTransparent = header.IsPressed && HeaderIsTransparent();
                        header.RaiseEvent(new PointerReleasedEventArgs(header, Pointer, dialog, xy, (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                        Pointer.Capture(null);
                        // Hidden, scrolled dialogs can lag behind the routed release's hit test.
                        if (e.IsExpanded != (index == 1))
                            header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                    }
                    var pseudoClasses = (IPseudoClasses)header.Classes;
                    var wasHovered = pseudoClasses.Contains(":pointerover");
                    pseudoClasses.Set(":pointerover", true);
                    var hoverTransparent = HeaderIsTransparent();
                    pseudoClasses.Set(":pointerover", wasHovered);
                    return new { e.Name, expanded = e.IsExpanded, height = header.Bounds.Height,
                        transparent = HeaderIsTransparent(), hoverTransparent, pressedTransparent };
                }).ToArray();
                return new { rows };
            }
            if (action == "create_private_style")
            {
                var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "CreateRepositoryPrivate" && c.IsEffectivelyVisible);
                var border = box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Border");
                var icon = border.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "Icon");
                var presenter = box.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "ContentPresenter");
                var label = presenter.GetVisualDescendants().OfType<TextBlock>().Single();
                return new { enabled = box.IsEffectivelyEnabled, @checked = box.IsChecked, visible = icon.IsVisible,
                    outline = border.BorderBrush?.ToString(), background = border.Background?.ToString(), icon = icon.Fill?.ToString(), label = label.Foreground?.ToString(),
                    borderOpacity = border.Opacity, iconOpacity = border.Opacity * icon.Opacity, textOpacity = presenter.Opacity * label.Opacity };
            }
            if (action == "preferences_open_from_tools")
            {
                var integrationMenu = window.FindControl<MenuItem>("ThirdPartyIntegrationMenu");
                integrationMenu.Command.Execute(integrationMenu.CommandParameter);
                return new { parameter = integrationMenu.CommandParameter, enabled = integrationMenu.IsEnabled,
                    last = ((MenuItem)integrationMenu.Parent).Items.OfType<MenuItem>().Last() == integrationMenu };
            }
            if (action == "preferences_identity_trap")
            {
                var preferenceWindow = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows.OfType<Views.Preferences>().Single();
                preferenceWindow.GPGUserKey = "Signing settings are outside the integration dialog";
                return new { changed = true };
            }
            if (action == "preferences_diff_tool_layout")
            {
                var preferenceWindow = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows.OfType<Views.Preferences>().Single();
                var picker = preferenceWindow.FindControl<ComboBox>("ExternalToolSelector");
                var path = preferenceWindow.FindControl<TextBox>("ExternalToolPath");
                return new { selected = picker.SelectedIndex, width = picker.Bounds.Width,
                    left = picker.TranslatePoint(default, preferenceWindow)?.X, pathVisible = path.IsEffectivelyVisible,
                    gitIndex = Models.ExternalMerger.Supported.FindIndex(tool => tool.IsGitConfiguration) };
            }
            if (action is "diff_working_hover" or "diff_working_hover_stage" or "diff_standalone_hover" or "diff_standalone_chunk" or "diff_standalone_state")
            {
                var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var host = action.StartsWith("diff_standalone_", StringComparison.Ordinal)
                    ? desktop.Windows.OfType<Views.InternalDiffWindow>().Last(w => w.IsVisible)
                    : window;
                if (action == "diff_standalone_state")
                {
                    var outer = host.GetVisualDescendants().OfType<Views.DiffView>().Single(v => v.IsEffectivelyVisible);
                    var context = (ViewModels.DiffContext)outer.DataContext;
                    var textDiff = context.Content as ViewModels.TextDiffContext;
                    return new { ready = context.LoadCompletion.IsCompleted, context.Title, context.IsTextDiff,
                        canEdit = textDiff?.Option.IsLocalChange == true,
                        lines = textDiff?.Data.Lines.Select(line => new { line.Content, type = line.Type.ToString() }).ToArray() };
                }
                var diff = host.GetVisualDescendants().OfType<Views.TextDiffView>()
                    .Single(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.TextDiffContext { Option.IsLocalChange: true });
                var model = (ViewModels.TextDiffContext)diff.DataContext;
                if (action is "diff_working_hover_stage" or "diff_standalone_chunk")
                {
                    var requested = action == "diff_working_hover_stage" ? "BtnStageChunk" : args.GetProperty("button").GetString();
                    var button = diff.FindControl<Button>(requested);
                    if (!button.IsEffectivelyVisible || model.SelectedChunk == null)
                        throw new InvalidOperationException("No hovered change is available for this operation.");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return new { pressed = button.Name };
                }
                var presenters = diff.GetVisualDescendants().OfType<Views.ThemedTextDiffPresenter>().Where(v => v.IsEffectivelyVisible).ToArray();
                if (index >= presenters.Length) return new { ready = false };
                var presenter = presenters[index];
                var view = presenter.TextArea.TextView;
                var rows = model is ViewModels.TwoSideTextDiff split ? presenter.IsOld ? split.Old : split.New : model.Data.Lines;
                var target = args.GetProperty("button").GetString();
                var rowIndex = rows.FindIndex(row => target == "$alignment" ? row.Type == Models.TextDiffLineType.None :
                    target == "$hunk" ? row.Type == Models.TextDiffLineType.Indicator : row.Content == target);
                if (rowIndex < 0 || !view.VisualLinesValid) return new { ready = false };
                var visual = view.GetVisualLine(rowIndex + 1);
                if (visual == null)
                {
                    presenter.ScrollToLine(rowIndex + 1);
                    return new { ready = false };
                }
                double Y(AvaloniaEdit.Rendering.VisualLine line, bool bottom) => line.GetTextLineVisualYPosition(
                    bottom ? line.TextLines[^1] : line.TextLines[0], bottom ? AvaloniaEdit.Rendering.VisualYPosition.LineBottom : AvaloniaEdit.Rendering.VisualYPosition.LineTop)
                    - view.VerticalOffset;
                var y = Math.Max(0, Y(visual, false));
                var bottom = Math.Min(view.Bounds.Height, Y(visual, true));
                if (bottom <= y) return new { ready = false };
                var hoverPoint = view.TranslatePoint(new Point(Math.Min(24, view.Bounds.Width / 2), (y + bottom) / 2), host) ?? default;
                view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, Pointer, host, hoverPoint, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
                var chunk = model.SelectedChunk;
                return new { ready = true, oldSide = presenter.IsOld, wrap = presenter.WordWrap, selected = chunk != null,
                    changed = chunk == null ? null : model.Data.Lines.Skip(chunk.StartIdx).Take(chunk.EndIdx - chunk.StartIdx + 1)
                        .Select(line => new { line.Content, type = line.Type.ToString() }).ToArray(),
                    visualRows = chunk == null ? null : view.VisualLines.Where(line => !line.IsDisposed && line.FirstDocumentLine.LineNumber <= rows.Count)
                        .Where(line => Math.Min(chunk.Y + chunk.Height, Y(line, true)) - Math.Max(chunk.Y, Y(line, false)) > 1)
                        .Select(line => rows[line.FirstDocumentLine.LineNumber - 1].Type.ToString()).ToArray(),
                    height = chunk?.Height };
            }
            if (action == "sidebar_reveal")
            {
                var repo = window.GetVisualDescendants().OfType<Views.Repository>().Single(v => v.IsEffectivelyVisible);
                var request = args.GetProperty("button").GetString().Split('|', 2);
                var section = request[0];
                var tree = section == "local" ? repo.LocalBranchTree : repo.RemoteBranchTree;
                var tags = repo.FindControl<Views.TagsView>("TagsList");
                var groupName = section switch { "local" => "LocalBranchGroup", "remote" => "RemoteBranchGroup", _ => "TagGroup" };
                var group = repo.FindControl<ToggleButton>(groupName);
                Control panel = section == "tags" ? tags : tree;
                var list = panel.FindDescendantOfType<ListBox>();
                var scroll = list?.FindDescendantOfType<ScrollViewer>();
                if (index is 1 or 2)
                    group.SetCurrentValue(ToggleButton.IsCheckedProperty, index == 2);
                else if (index == 3)
                {
                    if (section == "tags")
                        tags.ToggleNodeIsExpanded(list!.Items.OfType<ViewModels.TagTreeNode>().Single(row => row.IsFolder && row.FullPath == request[1]));
                    else
                        tree.ToggleNodeIsExpanded(tree.Rows.Single(row => !row.IsBranch && row.Path == request[1]));
                }
                else if (index == 4 && scroll != null)
                    scroll.Offset = default;

                string PathOf(object row) => row switch
                {
                    ViewModels.TagTreeNode tag => tag.FullPath,
                    ViewModels.TagListItem tag => tag.Tag.Name,
                    ViewModels.BranchTreeNode branch => branch.Path,
                    _ => null,
                };
                bool InViewport(ListBoxItem row)
                {
                    if (scroll == null || !row.IsEffectivelyVisible || row.Bounds.Height < 1) return false;
                    var origin = row.TranslatePoint(default, scroll);
                    return origin is { Y: >= -1 } && origin.Value.Y + row.Bounds.Height <= scroll.Bounds.Height + 1;
                }
                return new { open = group.IsChecked == true, offset = scroll?.Offset.Y,
                    rows = list?.Items.Select(PathOf).ToArray() ?? [],
                    visible = list?.GetVisualDescendants().OfType<ListBoxItem>().Where(InViewport).Select(row => PathOf(row.DataContext)).ToArray() ?? [] };
            }
            if (action == "tag_sidebar")
            {
                var view = window.GetVisualDescendants().OfType<Views.Repository>().Single(v => v.IsEffectivelyVisible);
                var repo = (ViewModels.Repository)view.DataContext;
                var group = view.FindControl<ToggleButton>("TagGroup");
                var tags = view.FindControl<Views.TagsView>("TagsList");
                var list = tags.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
                var tagName = args.GetProperty("button").GetString();
                string Name(object item) => item switch
                {
                    ViewModels.TagListItem flat => flat.Tag.Name,
                    ViewModels.TagTreeNode tree => tree.FullPath,
                    _ => null,
                };
                if (index == 1)
                    group.SetCurrentValue(ToggleButton.IsCheckedProperty, group.IsChecked != true);
                else if (index == 2)
                    repo.ShowTagsAsTree = !repo.ShowTagsAsTree;
                else if (index == 3)
                {
                    list!.Focus();
                    list.SelectedItem = list.Items.First(item => Name(item) == tagName);
                }
                else if (index == 4)
                    tags.ToggleNodeIsExpanded(list!.Items.OfType<ViewModels.TagTreeNode>().First(node => node.IsFolder && node.FullPath == tagName));
                else if (index == 5)
                    tags.UnselectAll();
                else if (index == 6)
                {
                    var row = tags.GetVisualDescendants().OfType<ListBoxItem>().First(item => Name(item.DataContext) == tagName);
                    var target = row.GetVisualDescendants().OfType<Border>().First(b => b.Height == 24);
                    var hit = target.TranslatePoint(new Point(40, 12), window) ?? default;
                    target.RaiseEvent(new PointerPressedEventArgs(target, Pointer, window, hit, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                    target.RaiseEvent(new PointerReleasedEventArgs(target, Pointer, window, hit, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                }
                return new
                {
                    expanded = group.IsChecked == true,
                    groupColor = group.Background?.ToString(),
                    primary = group.FindResource("Brush.TreeGit.Selection")?.ToString(),
                    secondary = group.FindResource("Brush.TreeGit.SelectionSecondary")?.ToString(),
                    selectionText = group.FindResource("Brush.TreeGit.SelectionForeground")?.ToString(),
                    tree = repo.ShowTagsAsTree,
                    filterButtons = tags.GetVisualDescendants().OfType<Views.FilterModeSwitchButton>().Count(),
                    rows = tags.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.IsEffectivelyVisible).Select(row =>
                    {
                        var background = row.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Background");
                        var textPresenter = row.GetVisualChildren().FirstOrDefault()?.GetVisualChildren().OfType<ContentPresenter>().FirstOrDefault();
                        return new { name = Name(row.DataContext), selected = row.IsSelected,
                            color = background?.Background?.ToString(), opacity = background?.Opacity, text = textPresenter?.Foreground?.ToString() };
                    }).ToArray(),
                };
            }
            if (action == "dropdown_geometry")
            {
                var name = args.GetProperty("button").GetString();
                var desktop = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var dropdown = desktop.Windows.SelectMany(w => w.GetVisualDescendants()).OfType<Control>()
                    .First(c => c.IsEffectivelyVisible && (c.Name == name || name == "PullBranch" && c is Views.BranchSelector && c.FindAncestorOfType<Views.Pull>() != null));
                if (dropdown is ComboBox combo)
                {
                    if (index >= 3)
                        combo.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, index - 3);
                    else if (index != 2)
                        combo.IsDropDownOpen = index == 1;
                }
                else if (dropdown is Views.BranchSelector branch)
                {
                    if (index != 2) branch.IsDropDownOpened = index == 1;
                }
                var popup = dropdown.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");
                var anchor = popup?.PlacementTarget;
                var frame = popup?.Child;
                var left = frame?.TranslatePoint(default, dropdown)?.X;
                var right = frame?.TranslatePoint(new Point(frame.Bounds.Width, 0), dropdown)?.X;
                if (dropdown is Views.CompactComboBox compact && name is "HistoryBranchPicker" or "HistoryOrderPicker" or "SearchMethodPicker")
                {
                    var parts = dropdown.GetVisualDescendants().ToArray();
                    var area = parts.OfType<ContentControl>().FirstOrDefault(c => c.Name == "ContentPresenter");
                    var search = name == "SearchMethodPicker";
                    var captionHost = parts.OfType<Control>().FirstOrDefault(c => c.Name == (search ? "SearchMethodCaption" : "HistoryPickerCaption"));
                    var caption = captionHost?
                        .GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible);
                    var guide = parts.OfType<Panel>().FirstOrDefault(c => c.Name == (search ? "SearchMethodWidthGuide" : "HistoryPickerWidthGuide"));
                    var glyph = parts.OfType<PathIcon>().First(c => c.Name == "DropDownGlyph");
                    var areaStart = area?.TranslatePoint(default, dropdown)?.X;
                    var captionStart = TextStart(caption, dropdown);
                    var popupPositioned = left is double popupX && Math.Abs(popupX) <= 1;
                    var items = (frame?.GetVisualDescendants().OfType<ComboBoxItem>() ?? [])
                        .Select(item => (item, text: item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible)))
                        .Where(entry => entry.text != null);
                    return new { boxed = dropdown.Bounds.Width, left, right, popupOpen = popup?.IsOpen,
                        selected = compact.SelectedIndex, text = caption?.Text, leftOffset = captionStart - areaStart,
                        arrowWidth = ((Grid)glyph.GetVisualParent()).ColumnDefinitions[Grid.GetColumn(glyph)].ActualWidth,
                        fits = CaptionFits(caption, area, default), longest = guide?.DesiredSize.Width,
                        items = items.Select(entry => new { text = entry.text.Text,
                            leftOffset = popupPositioned ? left + TextStart(entry.text, frame) - captionStart : null,
                            fits = CaptionFits(entry.text, entry.item, entry.item.Padding) }).ToArray() };
                }
                var layer = OverlayLayer.GetOverlayLayer(dropdown);
                var root = TopLevel.GetTopLevel(dropdown);
                static IEnumerable<TextBlock> Captions(Visual view) => (view?.GetVisualDescendants().OfType<TextBlock>() ?? []).Where(t =>
                    t.IsEffectivelyVisible && t.Opacity > 0 && !string.IsNullOrEmpty(t.Text) && t.Name != "PlaceholderTextBlock");
                var closedCaptions = Captions(dropdown.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(c => c.Name == "ContentPresenter")).ToArray();
                var closed = dropdown is Views.BranchSelector
                    ? dropdown.GetVisualDescendants().OfType<Views.BranchSelectorChoice>().FirstOrDefault()
                    : closedCaptions.FirstOrDefault();
                var closedStart = TextStart(closed, dropdown);
                var choices = frame?.GetVisualDescendants().OfType<ComboBoxItem>().ToArray() ?? [];
                var labels = dropdown is Views.BranchSelector
                    ? (frame?.GetVisualDescendants().OfType<Views.BranchSelectorChoice>() ?? []).Cast<TextBlock>()
                    : choices.Select(item => Captions(item).FirstOrDefault()).Where(t => t != null);
                var offsets = labels.Select(t => new { caption = t.Text, offset = left + TextStart(t, frame) - closedStart }).ToArray();
                var selectedCaptions = Captions(choices.FirstOrDefault(item => item.IsSelected));
                var selectedOffsets = selectedCaptions.Zip(closedCaptions, (option, caption) => left + TextStart(option, frame) - TextStart(caption, dropdown)).ToArray();
                return new { boxed = dropdown.Bounds.Width, left, right, popupOpen = popup?.IsOpen,
                    ready = frame?.IsArrangeValid == true && frame.Bounds.Width > 0 && frame.Bounds.Height > 0,
                    anchorX = anchor?.TranslatePoint(default, dropdown)?.X,
                    layerX = layer?.TranslatePoint(default, root)?.X,
                    alignment = (dropdown as ComboBox)?.HorizontalContentAlignment.ToString(), labelOffsets = offsets, selectedOffsets };
            }
            if (action == "checkbox_appearance")
            {
                var name = args.GetProperty("button").GetString();
                var scope = name == "SearchScopeCheckBox" ? window.GetVisualDescendants().OfType<Button>().FirstOrDefault(c => c.Name == "SearchScopeButton" && c.IsEffectivelyVisible) : null;
                if (scope?.Flyout is Flyout flyout && !flyout.IsOpen)
                    flyout.ShowAt(scope);
                var box = scope?.Flyout is Flyout { Content: CheckBox filter } ? filter :
                    window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
                if (box?.IsEffectivelyEnabled != true) return new { ready = false };
                box.SetCurrentValue(ToggleButton.IsCheckedProperty, index != 0);
                var border = box.GetVisualDescendants().OfType<Border>().FirstOrDefault(c => c.Name == "Border");
                var icon = border?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault();
                if (icon == null) return new { ready = false };
                var classes = (IPseudoClasses)box.Classes;
                var focused = classes.Contains(":focus-visible");
                var hovered = classes.Contains(":pointerover");
                var appearance = new List<object>();
                try
                {
                    foreach (var (focus, hover) in new[] { (false, false), (true, false), (false, true) })
                    {
                        classes.Set(":focus-visible", focus);
                        classes.Set(":pointerover", hover);
                        appearance.Add(new { fill = border.Background?.ToString(), stroke = border.BorderBrush?.ToString(), mark = icon.Fill?.ToString(), visible = icon.IsVisible });
                    }
                }
                finally
                {
                    classes.Set(":focus-visible", focused);
                    classes.Set(":pointerover", hovered);
                    if (scope != null && index == 0) scope.Flyout.Hide();
                }
                return new { ready = true, appearance };
            }
            if (action == "history_change_search")
            {
                var field = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(c => c.Name == "CommitChangeSearchBox" && c.IsEffectivelyVisible);
                if (field?.InnerRightContent is not StackPanel tools) return new { ready = false };
                var icon = tools.Children.OfType<Avalonia.Controls.Shapes.Path>().Single();
                var clear = tools.Children.OfType<Button>().Single();
                var iconRight = icon.TranslatePoint(new Point(icon.Bounds.Width, 0), field)?.X;
                return new { ready = icon.IsArrangeValid && iconRight.HasValue, leftIsEmpty = field.InnerLeftContent == null,
                    icon = ReferenceEquals(icon.Data, field.FindResource("Icons.Search")),
                    iconRight, width = field.Bounds.Width, clearShown = clear.IsVisible,
                    clearRight = clear.IsVisible ? clear.TranslatePoint(new Point(clear.Bounds.Width, 0), field)?.X : null };
            }
            if (action.StartsWith("diff_", StringComparison.Ordinal))
            {
                var profileLifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var profileViews = profileLifetime.Windows.SelectMany(w => w.GetVisualDescendants().OfType<Views.DiffView>().Where(v => v.IsEffectivelyVisible)).ToArray();
                if (action == "diff_open")
                {
                    var profileRepo = (ViewModels.Repository)((ViewModels.Launcher)window.DataContext).ActivePage.Data;
                    App.OpenInternalComparison(profileRepo.FullPath, new Models.DiffOption("HEAD~1", "HEAD", new Models.Change { Path = args.GetProperty("button").GetString() }));
                }
                else if (action == "diff_commit_tab")
                {
                    var repo = App.GetLauncher().ActivePage.LoadedRepository;
                    return new { selectedTab = (repo?.Histories.DetailContext as ViewModels.CommitDetail)?.ActiveTabIndex };
                }
                else if (action == "diff_read_reuse")
                {
                    var repo = App.GetLauncher().ActivePage.LoadedRepository;
                    var detail = repo?.Histories.DetailContext as ViewModels.CommitDetail;
                    if (detail?.DiffContext == null)
                        return new { ready = false };

                    var context = new ViewModels.DiffContext(repo.FullPath, detail.DiffContext.Option, standalone: true);
                    var comparisonSettings = context.Settings;
                    var originalCR = comparisonSettings.IgnoreCRAtEOLInDiff;
                    try
                    {
                        var initial = context.LoadCompletion;
                        var requests = new HashSet<System.Threading.Tasks.Task> { initial };
                        for (var i = 0; i < 4; i++)
                        {
                            context.CheckSettings();
                            requests.Add(context.LoadCompletion);
                        }
                        comparisonSettings.IgnoreCRAtEOLInDiff = !originalCR;
                        context.CheckSettings();
                        var changed = context.LoadCompletion;
                        comparisonSettings.IgnoreCRAtEOLInDiff = originalCR;
                        context.CheckSettings();
                        var reverted = context.LoadCompletion;
                        context.Reload();
                        return new { ready = true, repeatedChecks = 4, readRequests = requests.Count,
                            optionChange = initial != changed, optionRevert = changed != reverted, explicitRefresh = reverted != context.LoadCompletion };
                    }
                    finally
                    {
                        comparisonSettings.IgnoreCRAtEOLInDiff = originalCR;
                        context.CancelLoad();
                    }
                }
                else if (action == "diff_preferences_bounds")
                {
                    var preferences = profileLifetime.Windows.OfType<Views.Preferences>().FirstOrDefault(w => w.IsVisible);
                    var body = preferences?.FindControl<Grid>("SettingsBody");
                    var scroll = body?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.IsEffectivelyVisible);
                    if (scroll == null || scroll.Viewport.Height <= 0)
                        return new { ready = false };

                    return new { ready = true, width = preferences.Bounds.Width, height = preferences.Bounds.Height,
                        contentHeight = scroll.Extent.Height, visibleHeight = scroll.Viewport.Height,
                        checkboxes = scroll.GetVisualDescendants().OfType<CheckBox>().Count(box => box.IsEffectivelyVisible) };
                }
                else if (action == "diff_retired_line")
                {
                    var pane = profileViews.Where(v => v.Settings is ViewModels.Preferences)
                        .SelectMany(v => v.GetVisualDescendants().OfType<Views.ThemedTextDiffPresenter>())
                        .FirstOrDefault(v => v.IsEffectivelyVisible);
                    if (pane == null) return new { ready = false };
                    var view = pane.TextArea.TextView;
                    view.EnsureVisualLines();
                    var line = view.VisualLines.FirstOrDefault(v => v.FirstDocumentLine.LineNumber > 1 && v.FirstDocumentLine.Length > 0);
                    var visual = FindDiffLineDrawing(view, line);
                    if (visual == null) return new { ready = false };

                    var document = pane.Document;
                    var original = document.Text;
                    var offset = line.FirstDocumentLine.Offset;
                    var staleSelection = new AvaloniaEdit.Editing.SelectionSegment(offset, offset + 1);
                    var navigation = pane.BlockNavigation;
                    pane.Select(offset, 1);
                    try
                    {
                        document.Remove(0, document.TextLength);
                        using var bitmap = new RenderTargetBitmap(new PixelSize(128, 32), new Vector(96, 96));
                        using var drawing = bitmap.CreateDrawingContext();
                        visual.Render(drawing);
                        view.EnsureVisualLines();
                        var staleBounds = AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(view, staleSelection).ToArray();
                        pane.BlockNavigation = null;
                        pane.GotoChange(ViewModels.BlockNavigationDirection.First);
                        view.EnsureVisualLines();
                        view.Render(drawing);
                        var diff = (ViewModels.TextDiffContext)pane.DataContext;
                        var invalidPatch = new Models.PatchGenerator(diff.Option, diff.Data, -1, 1, true, false);
                        return new { ready = true, retired = line.IsDisposed, deleted = line.FirstDocumentLine.IsDeleted,
                            staleBounds = staleBounds.Length, missingNavigationSafe = true, rejectedInvalidPatch = !invalidPatch.IsValid };
                    }
                    finally
                    {
                        pane.BlockNavigation = navigation;
                        document.Text = original;
                        pane.Select(0, 0);
                    }
                }
                else if (action is "diff_encoding" or "diff_encoding_state")
                {
                    var view = profileViews.First(v => v.Settings is ViewModels.Preferences);
                    var picker = view.FindControl<Views.DisplayEncodingPicker>("DiffEncodingPicker");
                    var label = view.FindControl<TextBlock>("DiffEncodingLabel");
                    var first = view.FindControl<Button>("BtnGotoFirstChange");
                    var beforeFirst = picker.TranslatePoint(new Point(picker.Bounds.Width, 0), view)?.X <= first.TranslatePoint(default, view)?.X;
                    var labelBeforePicker = label.TranslatePoint(new Point(label.Bounds.Width, 0), view)?.X <= picker.TranslatePoint(default, view)?.X;
                    if (action == "diff_encoding") picker.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, index);
                    var labels = picker.Items.OfType<Models.DisplayEncodingOption>().Select(item => item.Name).ToArray();
                    var selectedText = picker.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(block => block.IsEffectivelyVisible && block.Opacity > 0 && ReferenceEquals(block.DataContext, picker.SelectedItem));
                    return new { labels, selected = picker.SelectedIndex, selectedCaption = selectedText?.Text,
                        sameFontSize = selectedText?.FontSize == label.FontSize,
                        beforeFirst, caption = label.Text, labelBeforePicker, labelVisible = label.IsEffectivelyVisible };
                }
                else if (action == "diff_decoded_text")
                {
                    var view = profileViews.FirstOrDefault(v => v.Settings is ViewModels.Preferences);
                    if (view?.DataContext is not ViewModels.DiffContext model)
                        return new { file = (string)null, kind = (string)null };

                    var decoded = model.Content as ViewModels.TextDiffContext;
                    return new { file = model.Title, encoding = model.DisplayEncodingIndex, kind = model.Content?.GetType().Name,
                        encodingVisible = view.FindControl<Views.DisplayEncodingPicker>("DiffEncodingPicker").IsEffectivelyVisible,
                        labelVisible = view.FindControl<TextBlock>("DiffEncodingLabel").IsEffectivelyVisible,
                        displayOnly = decoded?.Data.IsDisplayOnly, rows = decoded?.Data.Lines.Take(30).Select(line => line.Content).ToArray() };
                }
                else if (action is "diff_toggle_main" or "diff_toggle_standalone")
                {
                    var profileExternal = action == "diff_toggle_standalone";
                    var profileView = profileViews.First(v => (v.Settings is not ViewModels.Preferences) == profileExternal);
                    var profileNames = new[] { "ToggleBtnUseSideBySide", "ToggleDiffSyntax", "ToggleDiffWrap", "ToggleDiffWhitespace", "ToggleDiffSymbols" };
                    profileView.FindControl<ToggleButton>(profileNames[index]).SetCurrentValue(ToggleButton.IsCheckedProperty, bool.Parse(args.GetProperty("button").GetString()));
                }
                else if (action is "diff_context_main" or "diff_context_standalone" or "diff_context_menu_main" or "diff_context_menu_standalone")
                {
                    var standalone = action.EndsWith("standalone", StringComparison.Ordinal);
                    var profileView = profileViews.First(v => (v.Settings is not ViewModels.Preferences) == standalone);
                    var displayOptions = profileView.FindControl<Button>("DiffDisplayOptions");
                    if (action.Contains("_menu_", StringComparison.Ordinal))
                    {
                        if (index == 1) displayOptions.Flyout.ShowAt(displayOptions);
                        else displayOptions.Flyout.Hide();
                    }
                    else
                    {
                        var picker = profileView.FindControl<ComboBox>("DiffContextPicker");
                        displayOptions.Flyout.ShowAt(displayOptions);
                        picker.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, picker.Items[index]);
                        displayOptions.Flyout.Hide();
                    }
                }
                else if (action is "diff_context_style" or "diff_encoding_style")
                {
                    var isContext = action == "diff_context_style";
                    var profileView = profileViews.First(v => v.Settings is ViewModels.Preferences);
                    var picker = profileView.FindControl<ComboBox>(isContext ? "DiffContextPicker" : "DiffEncodingPicker");
                    if (!isContext && index == 2 && _autoHideBeforeDropdownAudit == null)
                    {
                        _autoHideBeforeDropdownAudit = ViewModels.Preferences.Instance.UseAutoHideScrollBars;
                        ViewModels.Preferences.Instance.UseAutoHideScrollBars = false;
                        picker.MaxDropDownHeight = 120;
                    }
                    picker.IsDropDownOpen = index > 0;
                    if (!isContext && index == 0 && _autoHideBeforeDropdownAudit is bool original)
                    {
                        ViewModels.Preferences.Instance.UseAutoHideScrollBars = original;
                        picker.ClearValue(ComboBox.MaxDropDownHeightProperty);
                        _autoHideBeforeDropdownAudit = null;
                    }
                    var parts = picker.GetVisualDescendants().ToArray();
                    var background = parts.OfType<Border>().FirstOrDefault(b => b.Name == "Background");
                    var selectionArea = parts.OfType<ContentControl>().FirstOrDefault(c => c.Name == "ContentPresenter");
                    var caption = parts.OfType<TextBlock>().FirstOrDefault(t => t.Name == (isContext ? "SelectedContextText" : "SelectedEncodingText"));
                    var guide = parts.OfType<Panel>().FirstOrDefault(p => p.Name == "ContextWidthGuide");
                    var popup = parts.OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");
                    var scroll = popup?.Child?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    var items = (popup?.Child?.GetVisualDescendants().OfType<ComboBoxItem>() ?? [])
                        .Select(i => (item: i, caption: i.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()))
                        .Where(entry => entry.caption != null).ToArray();
                    var syntax = profileView.FindControl<ToggleButton>("ToggleDiffSyntax");
                    var syntaxSurface = syntax.GetVisualDescendants().OfType<ContentPresenter>()
                        .FirstOrDefault(c => c.Name == "PART_ContentPresenter");
                    var contextLabel = profileView.FindControl<Border>("DiffContextLabelBackground");
                    var encodingBackground = profileView.FindControl<ComboBox>("DiffEncodingPicker")
                        .GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Background")?.Background;
                    var areaCenter = selectionArea?.TranslatePoint(new Point(selectionArea.Bounds.Width / 2, 0), picker)?.X;
                    var areaStart = selectionArea?.TranslatePoint(default, picker)?.X;
                    var captionStart = TextStart(caption, picker);
                    var popupLeft = popup?.Child?.TranslatePoint(default, picker)?.X;
                    var popupPositioned = popupLeft is double popupX && Math.Abs(popupX) <= 1;
                    return new { width = picker.Bounds.Width, longest = guide?.DesiredSize.Width,
                        text = caption?.Text, centerOffset = TextCenter(caption, picker) - areaCenter,
                        leftOffset = captionStart - areaStart,
                        fits = CaptionFits(caption, selectionArea, default),
                        background = background?.Background?.ToString(), syntaxBackground = syntaxSurface?.Background?.ToString(),
                        contextLabelBackground = contextLabel.Background?.ToString(),
                        contextLabelHeight = contextLabel.Bounds.Height, syntaxSurfaceHeight = syntaxSurface?.Bounds.Height,
                        verticalOffset = isContext ? picker.TranslatePoint(new Point(0, picker.Bounds.Height / 2), contextLabel)?.Y - contextLabel.Bounds.Height / 2 : null,
                        encodingBackground = encodingBackground?.ToString(),
                        popupAligned = popup?.CustomPopupPlacementCallback == Views.DropdownPlacement.Align &&
                            popup.HorizontalOffset == 0 && ReferenceEquals(popup.PlacementTarget, background),
                        staticScrollbar = scroll is { AllowAutoHide: false } && scroll.Extent.Height > scroll.Viewport.Height,
                        dropdown = items.Select(entry => new { entry.caption.Text,
                            centerOffset = TextCenter(entry.caption, popup.Child) - areaCenter,
                            leftOffset = popupPositioned ? popupLeft + TextStart(entry.caption, popup.Child) - captionStart : null,
                            fits = CaptionFits(entry.caption, entry.item, entry.item.Padding) }).ToArray() };
                }
                else if (action == "diff_selection_colors")
                {
                    var scope = args.GetProperty("button").GetString();
                    var profile = profileViews.FirstOrDefault(v => (v.Settings is ViewModels.Preferences) == (scope == "main"));
                    if (profile == null)
                        return new { ready = false };

                    var panes = profile.GetVisualDescendants().OfType<Views.ThemedTextDiffPresenter>().Where(p => p.IsEffectivelyVisible).ToArray();
                    if (panes.Length == 0)
                        return new { ready = false };

                    var checks = new List<object>();
                    foreach (var pane in panes)
                    {
                        var area = pane.TextArea;
                        var view = area.TextView;
                        if (!pane.UseSyntaxHighlighting)
                            return new { ready = false };

                        view.EnsureVisualLines();
                        var line = view.VisualLines.FirstOrDefault(l =>
                            l.VisualTop >= view.VerticalOffset &&
                            pane.Document.GetText(l.FirstDocumentLine).Contains("\u4e2d\u6587 English", StringComparison.Ordinal) &&
                            l.Elements.Select(e => e.TextRunProperties.ForegroundBrush?.ToString()).Distinct().Count() > 1);
                        if (line == null)
                            return new { ready = false };

                        var documentLine = line.FirstDocumentLine;
                        var space = pane.Document.GetText(documentLine).IndexOf(" English", StringComparison.Ordinal);
                        double[] Positions(AvaloniaEdit.Rendering.VisualLine visualLine) =>
                            Enumerable.Range(0, documentLine.Length + 1).Select(offset =>
                            {
                                var column = visualLine.GetVisualColumn(offset);
                                return visualLine.GetTextLineVisualXPosition(visualLine.GetTextLine(column), column);
                            }).ToArray();
                        var before = Positions(line);
                        var linesBefore = line.TextLines.Count;
                        var colors = line.Elements.Select(e => e.TextRunProperties.ForegroundBrush?.ToString()).ToArray();
                        int KeywordBrightness(AvaloniaEdit.Rendering.VisualLine currentLine)
                        {
                            var lineVisual = FindDiffLineDrawing(view, currentLine);
                            if (lineVisual == null)
                                return -1;

                            const int scale = 2;
                            var width = (int)Math.Ceiling(before[3] * scale);
                            var height = (int)Math.Ceiling(currentLine.TextLines[0].Height * scale);
                            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
                            using (var drawing = bitmap.CreateDrawingContext())
                                lineVisual.Render(drawing);
                            using var png = new MemoryStream();
                            bitmap.Save(png);
                            png.Position = 0;
                            var pixels = StbImageSharp.ImageResult.FromStream(png, StbImageSharp.ColorComponents.RedGreenBlue);
                            var value = 0;
                            for (var i = 0; i < pixels.Data.Length; i += 3)
                                value = Math.Max(value, Math.Min(pixels.Data[i], Math.Min(pixels.Data[i + 1], pixels.Data[i + 2])));
                            return value;
                        }
                        var brightnessBefore = KeywordBrightness(line);
                        if (brightnessBefore < 0)
                            return new { ready = false };

                        pane.Select(documentLine.Offset, 3);
                        try
                        {
                            view.EnsureVisualLines();
                            var after = view.GetOrConstructVisualLine(documentLine);
                            var brightnessSelected = KeywordBrightness(after);
                            if (brightnessSelected < 0)
                                return new { ready = false };
                            var copiedText = pane.SelectedText;
                            var unchangedSyntax = colors.SequenceEqual(after.Elements.Select(e => e.TextRunProperties.ForegroundBrush?.ToString()));
                            pane.Select(documentLine.Offset + space, 1);
                            view.EnsureVisualLines();
                            var spaceSelected = view.GetOrConstructVisualLine(documentLine);
                            checks.Add(new
                            {
                                readable = brightnessSelected >= 226 && brightnessSelected > brightnessBefore,
                                brightnessBefore, brightnessSelected, unchangedSyntax, copiedText,
                                foreground = area.SelectionForeground?.ToString(),
                                fixedLayout = linesBefore == spaceSelected.TextLines.Count && before.SequenceEqual(Positions(spaceSelected)),
                                spaceText = pane.SelectedText,
                            });
                        }
                        finally
                        {
                            pane.Select(0, 0);
                            view.EnsureVisualLines();
                        }
                    }
                    return new { ready = true, panes = checks };
                }
                else if (action is "diff_scroll_standalone" or "diff_select_standalone")
                {
                    var profileView = profileViews.First(v => v.Settings is not ViewModels.Preferences);
                    foreach (var presenter in profileView.GetVisualDescendants().OfType<Views.ThemedTextDiffPresenter>().Where(p => p.IsEffectivelyVisible))
                    {
                        if (action == "diff_scroll_standalone") presenter.ScrollToLine(index);
                        else { presenter.SelectAll(); if (presenter.SelectedText != presenter.Text) throw new InvalidOperationException("Visual alignment padding changed selected document text"); presenter.Select(0, 0); }
                    }
                }
                return new { views = profileViews.Select(v => new { scope = v.Settings is ViewModels.Preferences ? "main" : "standalone",
                    columns = v.Settings.UseSideBySideDiff, syntax = v.Settings.UseSyntaxHighlighting, wrap = v.Settings.EnableDiffViewWordWrap,
                    file = ((ViewModels.DiffContext)v.DataContext).Title, context = ((ViewModels.DiffContext)v.DataContext).ContextOption.Lines,
                    contextLabel = v.FindControl<TextBlock>("DiffContextLabel").Text,
                    selectedContext = (v.FindControl<ComboBox>("DiffContextPicker").SelectedItem as Models.DiffContextOption)?.Lines,
                    contextChoices = v.FindControl<ComboBox>("DiffContextPicker").Items.OfType<Models.DiffContextOption>().Select(option => option.Name).ToArray(),
                    presenters = v.GetVisualDescendants().OfType<Views.ThemedTextDiffPresenter>().Where(p => p.IsEffectivelyVisible)
                        .Select(p => new { wrap = p.WordWrap, syntax = p.UseSyntaxHighlighting, lines = p.Document?.LineCount, offset = p.TextArea.TextView.VerticalOffset,
                            rows = p.TextArea.TextView.VisualLinesValid ? p.TextArea.TextView.VisualLines.Take(18).Select(line => (object)new { index = line.FirstDocumentLine.LineNumber, top = line.VisualTop, height = line.Height, segments = line.TextLines.Count }).ToArray() : Array.Empty<object>() }).ToArray() }).ToArray() };
            }
            else if (action == "chrome_strip")
            {
                var chrome = window.FindControl<Grid>("TitleChrome");
                var start = window.Position;var before = window.WindowState;
                var stripPoint = new Point(index switch { 0 => 1, 1 => chrome.Bounds.Width / 2, _ => chrome.Bounds.Width - 1 },
                    index == 3 ? chrome.RowDefinitions[0].ActualHeight : index == 4 ? chrome.Bounds.Height - 1 : chrome.RowDefinitions[0].ActualHeight + 10);
                var clicks = args.TryGetProperty("button",out var count) && count.GetString()=="single" ? 1 : 2;
                var stripRootPoint = chrome.TranslatePoint(stripPoint, window).Value;
                var press = new PointerPressedEventArgs(chrome, Pointer, window, stripRootPoint, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, clicks);
                var localY = press.GetPosition(chrome).Y;
                chrome.RaiseEvent(press);
                return new { unchanged = window.WindowState == before && window.Position == start, handled = press.Handled, localY, expectedY = stripPoint.Y };
            }
            else if (action == "dialog_resize_open")
            {
                window.WindowState = WindowState.Normal;
                window.Width = window.Width > 1100 ? 1050 : 1280;
                window.Height = window.Height > 750 ? 700 : 820;
                window.Position = new PixelPoint(window.Position.X + (index % 2 == 0 ? 20 : -20), window.Position.Y + (index % 2 == 0 ? 15 : -15));
                var names = new[] { "WelcomeClone", "WelcomeOpen", "WelcomeCreate", "WelcomeGroup", "WelcomeScan" };
                var openingButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == names[index] && b.IsEffectivelyVisible);
                if (openingButton.Command?.CanExecute(openingButton.CommandParameter) != true) throw new InvalidOperationException("Welcome action is unavailable.");
                openingButton.Command.Execute(openingButton.CommandParameter);
            }
            else if (action == "tab_guard")
            {
                var launcher = (ViewModels.Launcher)window.DataContext;
                var bar = window.GetVisualDescendants().OfType<Views.LauncherTabBar>().First();
                switch (index)
                {
                    case 0: launcher.CloseTab(null); break;
                    case 1: launcher.CloseOtherTabs(); break;
                    case 2: launcher.CloseRightTabs(); break;
                    case 3: launcher.AddNewTab(); break;
                    case 4: launcher.GotoNextTab(); break;
                    case 5: launcher.GotoPrevTab(); break;
                    case 6: launcher.MoveTab(launcher.ActivePage, launcher.Pages.Count - 1); break;
                    case 7: window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Control }); break;
                    case 8:
                        var tab = bar.GetVisualDescendants().OfType<Views.LauncherTabSizeBox>().First(t => t.DataContext == launcher.ActivePage);
                        tab.RaiseEvent(new PointerPressedEventArgs(tab, Pointer, tab, new Point(10, 10), (ulong)Environment.TickCount64,
                            new PointerPointProperties(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed), KeyModifiers.None, 1));
                        break;
                    case 9:
                        bar.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("treegit_tab_close") && b.DataContext == launcher.ActivePage)
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
            else if (action.StartsWith("owner_", StringComparison.Ordinal))
            {
                var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime;
                var dialog = lifetime.Windows.OfType<Views.ChromelessWindow>().Last(w => w.IsVisible && w != window);
                if (dialog.OwnedMovementBounds == null) throw new InvalidOperationException("Only an owned fixture dialog can receive routed movement.");
                if (action == "owner_hide_show")
                {
                    var parent = dialog.Owner as Window ?? window;
                    dialog.Hide();
                    dialog.Show(parent);
                }
                else if (action is "owner_maximize" or "owner_restore")
                    dialog.WindowState = action == "owner_maximize" ? WindowState.Maximized : WindowState.Normal;
                else if (action is "owner_native_maximize_button" or "owner_native_caption_double_click")
                {
                    if (!OperatingSystem.IsWindows() || LocalAuditHost.IsEnabled || Environment.GetEnvironmentVariable("CI") != "true" ||
                        dialog is not Views.InternalDiffWindow || dialog.TryGetPlatformHandle()?.Handle is not { } handle ||
                        GetWindowThreadProcessId(handle, out var process) == 0 || process != Environment.ProcessId)
                        throw new InvalidOperationException("Native caption messages require a visible comparison window owned by this test process.");
                    // Deliver only to this fixture. No system pointer or keyboard input.
                    if (action == "owner_native_maximize_button")
                        SendCaptionMessage(handle, 0x112 /* WM_SYSCOMMAND */, 0xF030 /* SC_MAXIMIZE */, IntPtr.Zero);
                    else
                    {
                        var x = dialog.Position.X + (int)(100 * dialog.RenderScaling);
                        var y = dialog.Position.Y + (int)(16 * dialog.RenderScaling);
                        SendCaptionMessage(handle, 0xA3 /* WM_NCLBUTTONDBLCLK */, 2 /* HTCAPTION */, (y << 16) | (x & 0xFFFF));
                    }
                }
                else if (action != "owner_inspect")
                {
                    var start = new Point(24, 14);
                    var press = new PointerPressedEventArgs(dialog, Pointer, dialog, start, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1);
                    if (action == "owner_body_drag") dialog.RaiseEvent(press);
                    else dialog.BeginMoveWindow(dialog, press);
                    if (action == "owner_capture_lost") Pointer.Capture(null);
                    var delta = index switch { 0 => new Vector(-10000, -10000), 1 => new Vector(10000, 10000), 2 => new Vector(-10000, 10000), 3 => new Vector(10000, -10000), _ => new Vector(35, 20) };
                    dialog.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, dialog, Pointer, dialog, start + delta, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                    dialog.RaiseEvent(new PointerReleasedEventArgs(dialog, Pointer, dialog, start + delta, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                    Pointer.Capture(null);
                }
                var frame = dialog.FrameSize ?? dialog.Bounds.Size;
                var area = dialog.OwnedMovementBounds.Value;
                var screen = dialog.Screens.ScreenFromWindow(dialog) ?? dialog.Screens.Primary;
                var workArea = screen.WorkingArea;
                var body = (Control)dialog.Content;
                var bodyPosition = body.PointToScreen(default);
                var bodyEnd = body.PointToScreen(new Point(body.Bounds.Width, body.Bounds.Height));
                return new { type = dialog.GetType().Name, state = dialog.WindowState.ToString(), padding = dialog.Padding.ToString(), x = dialog.Position.X, y = dialog.Position.Y, width = frame.Width * dialog.RenderScaling, height = frame.Height * dialog.RenderScaling,
                    owner = new { x = area.X, y = area.Y, width = area.Width, height = area.Height },
                    screen = new { x = workArea.X, y = workArea.Y, right = workArea.Right, bottom = workArea.Bottom, scale = screen.Scaling },
                    body = new { x = bodyPosition.X, y = bodyPosition.Y, right = bodyEnd.X, bottom = bodyEnd.Y } };
            }
            else if (action.StartsWith("tab_drag_", StringComparison.Ordinal) || action == "tab_drop")
            {
                var bar = window.GetVisualDescendants().OfType<Views.LauncherTabBar>().First();
                var root = bar.FindControl<Grid>("TabDropArea");
                var list = bar.FindControl<ListBox>("LauncherTabsList");
                var model = (ViewModels.Launcher)bar.DataContext;
                var values = args.TryGetProperty("button", out var targetIndex) ? targetIndex.GetString().Split(',') : new[] { "0", "before" };
                var item = list.ContainerFromIndex(int.Parse(values[0])) as Control;
                var location = item.TranslatePoint(new Point(item.Bounds.Width * (values[1] == "after" ? .8 : .2), item.Bounds.Height / 2), root).Value;
                var transfer = new DataTransfer();
                if (action != "tab_drag_external") transfer.Add(DataTransferItem.Create(DataFormat.CreateStringApplicationFormat("sourcegit-dnd-main-tab"), model.Pages[index].Node.Id));
                else transfer.Add(DataTransferItem.CreateText("unrelated drag"));
                root.RaiseEvent(new DragEventArgs(action == "tab_drop" ? DragDrop.DropEvent : action == "tab_drag_leave" ? DragDrop.DragLeaveEvent : DragDrop.DragOverEvent,
                    transfer, root, location, KeyModifiers.None));
            }
            else if (action is "radio_inspect" or "radio_select" or "radio_hover" or "radio_focus")
            {
                var surface = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PopupDialogSurface" && b.IsEffectivelyVisible);
                var choices = surface.GetVisualDescendants().OfType<RadioButton>().Where(r => r.IsEffectivelyVisible).ToArray();
                var radio = choices[index];
                if (action == "radio_select")
                    typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(radio, null);
                else if (action == "radio_focus")
                    (radio.FindAncestorOfType<Views.DealWithLocalChangesMethod>() as InputElement ?? radio).Focus(NavigationMethod.Tab);
                else if (action == "radio_hover")
                    radio.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, radio, Pointer, window,
                        radio.TranslatePoint(new Point(7, 7), window).Value, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
                var items = choices.Select(r =>
                {
                    var ellipses = r.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>();
                    var ring = ellipses.Single(e => e.Name == "Border");
                    var dot = ellipses.Single(e => e.Name == "Dot");
                    return new { selected = r.IsChecked, ring = ring.Fill?.ToString(), dot = dot.Fill?.ToString(),
                        edge = ring.Stroke?.ToString(), focused = (r.FindAncestorOfType<Views.DealWithLocalChangesMethod>() as InputElement ?? r).IsFocused,
                        hovered = r.IsPointerOver, outer = Bounds(ring), inner = Bounds(dot) };
                }).ToArray();
                return new { items };
            }
            else if (action == "closed_combo_focus")
            {
                var form = window.GetVisualDescendants().OfType<Control>().Single(c =>
                    (c is Views.Pull or Views.StashChanges) && c.IsEffectivelyVisible);
                var combo = form.GetVisualDescendants().OfType<ComboBox>().First();
                var borders = combo.GetVisualDescendants().OfType<Border>();
                var focus = borders.Single(b => b.Name == "HighlightBackground");
                var surface = borders.Single(b => b.Name == "Background");
                return new { closed = !combo.IsDropDownOpen, focused = combo.IsFocused, visible = focus.IsVisible,
                    fill = focus.Background?.ToString(), border = focus.BorderBrush?.ToString(), surface = surface.Background?.ToString(),
                    themeBorder = combo.FindResource("Brush.TreeGit.ParentSHA")?.ToString(), themeSurface = combo.FindResource("Brush.Contents")?.ToString() };
            }
            else if (action is "pull_remote_choices" or "pull_branch_choices" or "pull_selected_colors")
            {
                var pull = window.GetVisualDescendants().OfType<Views.Pull>().Single(v => v.IsEffectivelyVisible);
                var combo = pull.GetVisualDescendants().OfType<ComboBox>().First();
                var branches = pull.GetVisualDescendants().OfType<Views.BranchSelector>().Single();
                if (action == "pull_remote_choices") { branches.IsDropDownOpened = false; combo.IsDropDownOpen = true; }
                if (action == "pull_branch_choices") { combo.IsDropDownOpen = false; branches.IsDropDownOpened = true; }
                var item = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i =>
                    i.IsSelected && i.IsEffectivelyVisible && i.FindAncestorOfType<OverlayPopupHost>() != null);
                var presenter = item?.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(c => c.Name == "PART_ContentPresenter");
                return new { opened = combo.IsDropDownOpen || branches.IsDropDownOpened, background = presenter?.Background?.ToString(), foreground = presenter?.Foreground?.ToString() };
            }
            else if (action == "operation_probe")
            {
                var popup = ((ViewModels.Launcher)window.DataContext).ActivePage.Popup;
                if (index == 0) { popup.BeginOperation(); popup.InProgress = true; popup.ShowOperationPanel(); }
                else if (index == 1) for (var n = 0; n < 100; n++) popup.AppendOperationOutput("Anonymous operation output " + n + " " + new string('x', 150), n % 20 == 0);
                else if (index == 2) { popup.FinishOperation(true); popup.InProgress = false; }
                else if (index == 3) { popup.CancelOperation(); popup.FinishOperation(false); popup.InProgress = false; }
                else if (index == 4) popup.ShowFullOutput = !popup.ShowFullOutput;
                else if (index == 5) window.GetVisualDescendants().OfType<Views.OperationOutputView>().Single(v => v.IsEffectivelyVisible).SelectAll();
                else if (index == 6) popup.OnReceiveCommandLog(args.GetProperty("button").GetString());
                else if (index == 7) { popup.FinishOperation(false); popup.InProgress = false; }
                else if (index == 8) { popup.BeginOperation(); popup.InProgress = true; }
                else if (index == 9) popup.OnAuthenticationState("ConnectionFailed");
                else if (index == 10) popup.InProgress = false;
            }
            else if (action is "remote_query_start" or "remote_query_cancel" or "remote_query_state")
            {
                var page = ((ViewModels.Launcher)window.DataContext).ActivePage;
                var repo = page.Data as ViewModels.Repository ?? throw new InvalidOperationException("Open the isolated query repository first.");
                if (action == "remote_query_start")
                {
                    var branch = repo.Branches.Single(b => !b.IsLocal && b.Remote == "origin" && b.Name == "main");
                    page.Popup = index == 0 ? new ViewModels.DeleteBranch(repo, branch) : new ViewModels.DeleteMultipleBranches(repo, [branch], false);
                    _ = page.ProcessPopupAsync();
                }
                else if (action == "remote_query_cancel") page.TerminatePopup();
                return new { inProgress = page.Popup?.InProgress == true, panelVisible = page.Popup?.OperationPanelVisible == true,
                    popupPresent = page.Popup != null, busy = page.HasRunningOperation, activeLogs = repo.Logs.Count(log => !log.IsComplete) };
            }
            else if (action.StartsWith("dialog_", StringComparison.Ordinal))
            {
                var surface = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PopupDialogSurface" && b.IsEffectivelyVisible);
                var handle = surface.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PopupDragHandle" && b.IsEffectivelyVisible);
                var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                var target = action == "dialog_input_drag" ? surface.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled) : action == "dialog_body_drag" ? (Control)surface : handle;
                if (action == "dialog_input_drag") start = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                if (action == "dialog_body_drag") start = surface.TranslatePoint(new Point(12, surface.Bounds.Height - 42), window)!.Value;
                if (action != "dialog_inspect")
                {
                    target.RaiseEvent(new PointerPressedEventArgs(target, Pointer, window, start, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                    if (action == "dialog_capture_lost") Pointer.Capture(null);
                    var delta = index switch { 0 => new Vector(-10000, -10000), 1 => new Vector(10000, 10000), 2 => new Vector(-10000, 10000), 3 => new Vector(10000, -10000), _ => new Vector(50, -30) };
                    var receiver = Pointer.Captured as Control ?? target;
                    receiver.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, receiver, Pointer, window, start + delta, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                    receiver.RaiseEvent(new PointerReleasedEventArgs(receiver, Pointer, window, start + delta, (ulong)Environment.TickCount64,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                    Pointer.Capture(null);
                }
            }
            else if (action == "welcome_mock") return RepositoryCreationProbe.Configure(index);
            else if (action == "welcome_mock_state") return RepositoryCreationProbe.State();
            else if (action == "popup_commit_action")
            {
                var repo = ((ViewModels.Launcher)window.DataContext).ActivePage.Data as ViewModels.Repository
                    ?? throw new InvalidOperationException("The isolated repository is not open.");
                var commit = repo.Histories.Commits.First(c => c.SHA.Length is 40 or 64 && c.SHA.All(Uri.IsHexDigit));
                if (!repo.CanCreatePopup()) throw new InvalidOperationException("The isolated popup is unavailable.");
                repo.ShowPopup(index == 0 ? new ViewModels.CherryPick(repo, [commit]) : new ViewModels.Revert(repo, commit));
            }
            else if (action == "welcome_action")
            {
                var names = new[] { "WelcomeClone", "WelcomeOpen", "WelcomeCreate", "WelcomeGroup", "WelcomeScan" };
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == names[index] && b.IsEffectivelyVisible);
                if (button.Command?.CanExecute(button.CommandParameter) != true) throw new InvalidOperationException("Welcome action is unavailable.");
                button.Command.Execute(button.CommandParameter);
            }
            else if (action == "file_repository") headers[0].Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (action == "fit_desktop")
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
            else if (action == "fullscreen")
                window.WindowState = WindowState.FullScreen;
            else if (action is "discard_tooltip" or "stash_tooltip")
            {
                var label = App.Text(action == "stash_tooltip" ? "Toolbar.Stash" : "TreeGit.Toolbar.Discard");
                _tooltipTarget = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
                    b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("toolbar_label") && t.Text == label));
                ToolTip.SetIsOpen(_tooltipTarget, true);
            }
            else if (action is "popup_tooltip" or "new_tab_tooltip")
            {
                _tooltipTarget = window.GetVisualDescendants().OfType<Control>().First(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 &&
                    ToolTip.GetTip(c) != null && (action == "popup_tooltip" || c.Classes.Contains("treegit_tab_add")));
                ToolTip.SetIsOpen(_tooltipTarget, true);
            }
            else if (action == "detail_tooltip")
            {
                var target = window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == (index == 0 ? "CollapseDetailsButton" : "OpenDetailsStandaloneButton") && c.IsEffectivelyVisible);
                if (_tooltipTarget != target)
                {
                    target.PropertyChanged += (_, change) =>
                    {
                        if (change.Property != ToolTip.IsOpenProperty || Events.Count > 240) return;
                        Events.Add(new { phase = "detail-tip", target = target.Name, open = ToolTip.GetIsOpen(target), active = window.IsActive,
                            callers = new System.Diagnostics.StackTrace(false).GetFrames().Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.FullName?.Contains("ToolTip", StringComparison.Ordinal) == true || m?.DeclaringType?.FullName == "SourceGit.Views.Histories").Select(m => m.DeclaringType.FullName + "." + m.Name).Take(8).ToArray() });
                    };
                }
                _tooltipTarget = target;
                if (!(OperatingSystem.IsWindows() && LocalAuditHost.IsEnabled)) window.Activate();
                // Native activation is asynchronous. Establish the same managed
                // precondition before opening a tooltip in a routed-input test;
                // hidden local audits never activate a native desktop window.
                if (!window.IsActive)
                    (typeof(WindowBase).GetMethod("HandleActivated", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("The pinned framework activation handler is unavailable."))
                        .Invoke(window, null);
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
                menu.Open(); settings.IsSubMenuOpen = true;
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
            else if (action == "outside_content")
            {
                var content = window.Content as Control ?? throw new InvalidOperationException("Window content is missing.");
                content.RaiseEvent(new PointerPressedEventArgs(content, Pointer, window,
                    new Point(window.Bounds.Width / 2, window.Bounds.Height - 40), (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            }
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
                            .Select(r =>
                            {
                                var origin = r.TranslatePoint(default, h);
                                return new { x = origin?.X, y = origin?.Y, width = r.Bounds.Width, height = r.Bounds.Height };
                            }).ToArray(),
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
                windowActive = window.IsActive,
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
                    first = settings.Items.OfType<MenuItem>().First().Name,
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
                welcomeButtons = window.GetVisualDescendants().OfType<Views.WelcomeToolbar>().Where(t => t.IsEffectivelyVisible)
                    .SelectMany(t => t.GetVisualDescendants().OfType<Button>()).Where(b => b.Name?.StartsWith("Welcome", StringComparison.Ordinal) == true)
                    .Select(b => new { name = b.Name, bounds = Bounds(b), text = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text }).ToArray(),
                tabState = window.GetVisualDescendants().OfType<Views.LauncherTabBar>().Select(bar =>
                {
                    var model = (ViewModels.Launcher)bar.DataContext;
                    var list = bar.FindControl<ListBox>("LauncherTabsList");
                    var marker = bar.FindControl<Border>("TabInsertionMarker");
                    var mask = window.FindControl<Border>("TabModalMask");
                    var selectedTab = list.ContainerFromIndex(list.SelectedIndex) as Control;
                    var close = selectedTab?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("treegit_tab_close"));
                    return new { order = model.Pages.Select(p => p.Node.Id).ToArray(), active = model.ActivePage.Node.Id,
                        enabled = bar.IsEffectivelyEnabled, opacity = bar.Opacity, canManage = model.CanManageTabs,
                        modalMask = new { visible = mask.IsEffectivelyVisible, bounds = Bounds(mask), color = mask.Background.ToString() },
                        windowWidth = window.Bounds.Width, selectedTab = Bounds(selectedTab), closeIcon = Bounds(close?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault()),
                        pages = model.Pages.Select(p => new { id = p.Node.Id, kind = p.Data?.GetType().Name, popup = p.Popup?.GetType().Name, dirty = p.DirtyState.ToString() }).ToArray(),
                        selected = (list.SelectedItem as ViewModels.LauncherPage)?.Node.Id,
                        selectedContainers = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsSelected).Select(i => (i.DataContext as ViewModels.LauncherPage)?.Node.Id).ToArray(),
                        displayed = (window.GetVisualDescendants().OfType<Views.LauncherPage>().FirstOrDefault(p => p.IsEffectivelyVisible)?.DataContext as ViewModels.LauncherPage)?.Node.Id,
                        markerVisible = marker.IsVisible, marker = Bounds(marker), viewport = Bounds(bar.FindControl<ScrollViewer>("LauncherTabsScroller")) };
                }).FirstOrDefault(),
                welcomeSeparators = window.GetVisualDescendants().OfType<Views.WelcomeToolbar>().Where(t => t.IsEffectivelyVisible)
                    .Select(t => new { toolbar = Bounds(t), content = Bounds(t.GetVisualDescendants().OfType<Button>().First(b => b.Name == "WelcomeClone").Content as Control), separator = Bounds(t.GetVisualDescendants().OfType<Views.CommitOptionSeparator>().Single()),
                        grooveWidth = t.GetVisualDescendants().OfType<Views.CommitOptionSeparator>().Single().GrooveWidth }).ToArray(),
                welcomePresentation = window.GetVisualDescendants().OfType<Views.WelcomeToolbar>().Where(t => t.IsEffectivelyVisible)
                    .SelectMany(t => t.GetVisualDescendants().OfType<Button>().Where(b => b.Name is "WelcomeClone" or "WelcomeOpen" or "WelcomeCreate" or "WelcomeGroup" or "WelcomeScan")
                        .Select(b =>
                        {
                            var path = b.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
                            var label = b.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("toolbar_label"));
                            var center = path.TranslatePoint(path.RenderedGeometry?.Bounds.Center ?? default, window);
                            return new { name = b.Name, labelColor = label.Foreground?.ToString(), label = Bounds(label),
                                contentOpacity = path.GetVisualAncestors().TakeWhile(v => v != b).Aggregate(path.Opacity, (opacity, visual) => opacity * visual.Opacity),
                                iconCenter = new { x = center?.X, y = center?.Y },
                                iconColors = path.Fill is Avalonia.Media.SolidColorBrush solid ? new[] { solid.Color.ToString() }
                                    : (path.Fill as Avalonia.Media.LinearGradientBrush)?.GradientStops.Select(s => s.Color.ToString()).ToArray() };
                        })).ToArray(),
                operationStatuses = window.GetVisualDescendants().OfType<Views.PopupRunningStatus>().Where(v => v.IsEffectivelyVisible).Select(v =>
                    new { v.Description, animated = v.FindControl<ProgressBar>("ProgressBar").IsIndeterminate }).ToArray(),
                operationPanels = window.GetVisualDescendants().OfType<Views.OperationProgressView>().Where(v => v.IsEffectivelyVisible).Select(v =>
                {
                    var popup = (ViewModels.Popup)v.DataContext;
                    var controls = v.GetVisualDescendants().OfType<Control>().ToArray();
                    var log = controls.OfType<Views.OperationOutputView>().Single();
                    var scroller = controls.OfType<ScrollViewer>().Single(c => c.Name == "OperationOutputScroll");
                    var bar = controls.OfType<Views.ActivityProgressBar>().Single();
                    var sweep = controls.OfType<ProgressBar>().FirstOrDefault(p => p.Name == "ActivitySweep");
                    var close = controls.OfType<Button>().Single(b => b.Name == "CloseOperation");
                    return new { panel = Bounds(v), area = Bounds(v.FindAncestorOfType<Views.LauncherPage>().FindControl<Grid>("PopupArea")),
                        cancelEnabled = controls.OfType<Button>().Single(b => b.Name == "CancelOperation").IsEffectivelyEnabled,
                        closeEnabled = controls.OfType<Button>().Single(b => b.Name == "CloseOperation").IsEffectivelyEnabled,
                        cancelBounds = Bounds(controls.OfType<Button>().Single(b => b.Name == "CancelOperation")),
                        barBounds = Bounds(bar), sweepWidth = sweep?.Bounds.Width, sweeping = sweep?.IsIndeterminate == true && sweep.IsEffectivelyVisible,
                        progress = bar.Value, reported = popup.OperationProgress, indeterminate = bar.IsIndeterminate,
                        scrollGap = scroller.ScrollBarMaximum.Y - scroller.Offset.Y, closeWidth = close.Bounds.Width, closeFill = close.Background?.ToString(),
                        popup.InProgress, popup.OperationStage, popup.OperationPhaseFailed, popup.ShowFullOutput,
                        lines = popup.OperationOutput.Count, errors = popup.OperationOutput.Count(l => l.IsError),
                        selectableText = log.Inlines.Text, selectedText = log.SelectedText, scrollExtent = scroller.Extent, scrollViewport = scroller.Viewport,
                        progressColor = bar.Foreground?.ToString() };
                }).ToArray(),
                pageDialogs = window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PopupDialogSurface" && b.IsEffectivelyVisible)
                    .Select(b => new { dialog = Bounds(b), area = Bounds(b.GetVisualAncestors().OfType<Grid>().First(g => g.Name == "PopupArea")),
                        form = Bounds(b.GetVisualDescendants().OfType<UserControl>().Where(v => v is Views.Clone or Views.OpenLocalRepository or Views.CreateRepository or Views.CreateGroup or Views.ScanRepositories)
                            .Select(v => (v.Content as StackPanel)?.Children.OfType<Grid>().FirstOrDefault()).FirstOrDefault()),
                        header = Bounds(b.GetVisualDescendants().OfType<Border>().FirstOrDefault(h => h.Name == "PopupDragHandle" && h.IsEffectivelyVisible)),
                        title = b.GetVisualDescendants().OfType<Border>().FirstOrDefault(h => h.Name == "PopupDragHandle")?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text,
                        legacyTitleVisible = b.GetVisualDescendants().OfType<Control>().Any(c => c.Classes.Contains("popup_heading") && c.IsEffectivelyVisible),
                        advancedExpanders = b.GetVisualDescendants().OfType<Expander>().Count(e => e.Name is "BranchAdvancedOptions" or "PushAdvancedOptions"),
                        advancedCheckBoxes = b.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.Name is "BranchAllowOverwrite" or "PushSkipHooks" or "PushForce").Select(c => c.Name).ToArray(),
                        headerColor = b.GetVisualDescendants().OfType<Border>().FirstOrDefault(h => h.Name == "PopupDragHandle")?.Background?.ToString(),
                        frameColor = (b.Child as Border)?.BorderBrush?.ToString(), frameBackground = (b.Child as Border)?.Background?.ToString(), hasShadow = b.Effect != null }).ToArray(),
                createPresentation = window.GetVisualDescendants().OfType<Views.CreateRepository>().Where(v => v.IsEffectivelyVisible && v.DataContext is ViewModels.CreateRepository { CreateOnGitHub: true }).Select(v => new
                {
                    button = Bounds(v.GetVisualDescendants().OfType<Button>().First(b => b.Name == "VerifyGitHubAccount")),
                    buttonAlignment = v.GetVisualDescendants().OfType<Button>().First(b => b.Name == "VerifyGitHubAccount").VerticalContentAlignment.ToString(),
                    samePrimaryStyle = v.GetVisualDescendants().OfType<Button>().First(b => b.Name == "VerifyGitHubAccount").Background?.ToString()
                        == window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ConfirmPopup" && b.IsEffectivelyVisible).Background?.ToString(),
                    label = Bounds(v.GetVisualDescendants().OfType<Button>().First(b => b.Name == "VerifyGitHubAccount").GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()),
                    linkColor = v.FindControl<TextBlock>("GitHubTokenHelpLabel")?.Foreground?.ToString(),
                    linkLabel = Bounds(v.FindControl<TextBlock>("GitHubTokenHelpLabel")),
                    linkUnderline = Bounds(v.FindControl<Border>("GitHubTokenHelpUnderline")),
                    linkUnderlineColor = v.FindControl<Border>("GitHubTokenHelpUnderline")?.Background?.ToString(),
                    tokenInput = Bounds(v.FindControl<TextBox>("CreateRepositoryToken")),
                }).ToArray(),
                creationState = (window.DataContext as ViewModels.Launcher)?.ActivePage?.Popup is ViewModels.CreateRepository creation
                    ? new { name = creation.Name, account = creation.Account, error = creation.Error, canConnectExisting = creation.CanConnectExisting, canEditName = creation.CanEditName, canEditParentFolder = creation.CanEditParentFolder, canVerify = creation.CanVerifyAccount } : null,
                detailTooltips = window.GetVisualDescendants().OfType<Control>().Where(c => c.Name is "CollapseDetailsButton" or "OpenDetailsStandaloneButton" && c.IsEffectivelyVisible)
                    .Select(c => new { name = c.Name, open = ToolTip.GetIsOpen(c), placement = ToolTip.GetPlacement(c).ToString(),
                        interactive = (ToolTip.GetTip(c) as Control)?.IsHitTestVisible }).ToArray(),
                headers = headers.Select((h, i) => new { index = i, text = h.Header?.ToString(), open = h.IsSubMenuOpen, selected = h.IsSelected, childSelected = h.SelectedIndex,
                    items = h.Items.OfType<MenuItem>().Select(item => new { name = item.Name, text = item.Header?.ToString(), enabled = item.IsEffectivelyEnabled }).ToArray(),
                    popup = (typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(h) as Popup)?.IsOpen,
                    outlineVisible = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.IsVisible,
                    outlineBrush = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.BorderBrush?.ToString(),
                    bounds = h.Bounds.ToString(),
                    screenX = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).X,
                    screenY = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).Y }).ToArray(),
                events = Events.ToArray(),
                localWindowsPrivate = OperatingSystem.IsWindows() && LocalAuditHost.IsEnabled
                    ? (bool?)LocalAuditHost.AllWindowsPrivate(((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)App.Current.ApplicationLifetime).Windows) : null,
            };
        }
    }
}
