using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public sealed class BinaryDiffView : UserControl
    {
        private readonly TextBlock _summary = new() { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _oldTitle = new() { Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _newTitle = new() { Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBox _address = new() { Width = 116, [!TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Binary.AddressHint"), Margin = new Thickness(4, 0) };
        private readonly ScrollBar _scroll = new() { Orientation = Avalonia.Layout.Orientation.Vertical, SmallChange = 1 };
        private readonly Button _retry = new() { Name = "RetryBinaryDiff", IsVisible = false, Margin = new Thickness(2), Padding = new Thickness(8, 3) };
        private readonly HexPane _left;
        private readonly HexPane _right;
        private CancellationTokenSource _cancel;
        private ComparisonSession _session;
        private ComparisonSession _sharedSession;
        private Models.BinaryComparison _comparison;
        private Task _navigation = Task.CompletedTask;
        private int _navigationVersion;
        private long _selected = -1;
        private int _bytesPerRow = 16;
        private bool _attached;
        private readonly bool _allowSeparateWindow;
        private readonly Button _separateWindowButton;
        private string _errorDetails;
        public string Status { get; private set; } = "loading";
        public long SelectedOffset => _selected;
        public long DifferentBytes => _comparison?.DifferentBytes ?? 0;
        public long OldSize => _comparison?.OldSize ?? 0;
        public long NewSize => _comparison?.NewSize ?? 0;
        public long TopOffset => (long)_scroll.Value * _bytesPerRow;
        public int BytesPerRow => _bytesPerRow;
        public object Inspect() => new { status = Status, file = (DataContext as Models.BinaryDiff)?.FilePath,
#if INTEGRATION_TESTS
            snapshotDirectory = _comparison?.SnapshotDirectory,
#endif
            oldRevision = (DataContext as Models.BinaryDiff)?.OldRevision, newRevision = (DataContext as Models.BinaryDiff)?.NewRevision,
            oldTitle = _oldTitle.Text, newTitle = _newTitle.Text,
            summary = _summary.Text, errorDetails = _errorDetails, oldSize = OldSize, newSize = NewSize,
            differentBytes = DifferentBytes, selectedOffset = SelectedOffset, topOffset = TopOffset, bytesPerRow = BytesPerRow,
            leftPaneWidth = _left.Bounds.Width, rightPaneWidth = _right.Bounds.Width,
            requiredPaneWidth = (_comparison?.Length > uint.MaxValue ? 102 : 74) + _bytesPerRow * 23 + 10 + Math.Max(_bytesPerRow * 8, 36),
            oldHex = _comparison == null ? null : Convert.ToHexString(_comparison.Read(true, TopOffset, 256)),
            newHex = _comparison == null ? null : Convert.ToHexString(_comparison.Read(false, TopOffset, 256)) };

        public BinaryDiffView() : this(true) { }

        private BinaryDiffView(bool allowSeparateWindow)
        {
            _allowSeparateWindow = allowSeparateWindow;
            _left = new HexPane(this, true);
            _right = new HexPane(this, false);
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
            root.Bind(BackgroundProperty, new DynamicResourceExtension("Brush.Contents"));
            root.Children.Add(_summary);
            var toolbar = new WrapPanel { Margin = new Thickness(6, 2), Orientation = Avalonia.Layout.Orientation.Horizontal };
            AddButton(toolbar, "TreeGit.Binary.First", async () => await NavigateAsync("first"));
            AddButton(toolbar, "TreeGit.Binary.Previous", async () => await NavigateAsync("previous"));
            AddButton(toolbar, "TreeGit.Binary.Next", async () => await NavigateAsync("next"));
            AddButton(toolbar, "TreeGit.Binary.Last", async () => await NavigateAsync("last"));
            var jump = new StackPanel { Name = "BinaryJumpGroup", Orientation = Avalonia.Layout.Orientation.Horizontal };
            jump.Children.Add(_address);
            AddButton(jump, "TreeGit.Binary.GoTo", Jump);
            toolbar.Children.Add(jump);
            if (allowSeparateWindow) _separateWindowButton = AddButton(toolbar, "TreeGit.Binary.SeparateWindow", OpenComparisonWindow);
            App.BindText(_retry, ContentControl.ContentProperty, "BinaryDiff.Retry");
            _retry.Classes.Add("flat"); _retry.Click += (_, _) => Retry();
            toolbar.Children.Add(_retry);
            Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
            var titles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            titles.Children.Add(_oldTitle); Grid.SetColumn(_newTitle, 1); titles.Children.Add(_newTitle);
            Grid.SetRow(titles, 2); root.Children.Add(titles);
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*,Auto") };
            content.Children.Add(_left);
            var divider = new Border(); divider.Bind(BackgroundProperty, new DynamicResourceExtension("Brush.Border1"));
            Grid.SetColumn(divider, 1); content.Children.Add(divider);
            Grid.SetColumn(_right, 2); content.Children.Add(_right);
            Grid.SetColumn(_scroll, 3); content.Children.Add(_scroll);
            Grid.SetRow(content, 3); root.Children.Add(content);
            Content = root;
            _scroll.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                _navigationVersion++;
                Redraw();
            };
            SizeChanged += (_, _) => ResizeRows();
            _left.SizeChanged += (_, _) => ResizeRows();
            _right.SizeChanged += (_, _) => ResizeRows();
            PointerWheelChanged += (_, e) =>
            {
                _navigationVersion++;
                _scroll.Value = Math.Clamp(_scroll.Value - e.Delta.Y * 3, 0, _scroll.Maximum);
                e.Handled = true;
            };
            _address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Jump(); e.Handled = true; } };
        }

        private static Button AddButton(Panel panel, string label, Action action)
        {
            var button = new Button { [!ContentControl.ContentProperty] = App.TextBinding(label), Margin = new Thickness(2), Padding = new Thickness(8, 3) };
            button.Classes.Add("flat"); button.Click += (_, _) => action(); panel.Children.Add(button);
            return button;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_separateWindowButton != null) _separateWindowButton.IsVisible = TopLevel.GetTopLevel(this) is not InternalDiffWindow;
            base.OnAttachedToVisualTree(e); _attached = true; App.LocaleChanged += RefreshLocalizedLabels; Reload();
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false; App.LocaleChanged -= RefreshLocalizedLabels; Stop(); base.OnDetachedFromVisualTree(e);
        }
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e); if (_attached) Reload();
        }

        private void RefreshLocalizedLabels()
        {
            if (DataContext is not Models.BinaryDiff diff) return;
            _oldTitle.Text = App.Text("TreeGit.Binary.OldPrefix") + Label(diff.OldRevision, diff.NewRevision) + " · " + DisplayPath(diff.OldFilePath);
            _newTitle.Text = App.Text("TreeGit.Binary.NewPrefix") + Label(diff.NewRevision) + " · " + Native.OS.FormatLocalPath(diff.FilePath);
            if (Status == "error")
                _summary.Text = App.Text("BinaryDiff.LoadFailed");
            else if (_comparison is { } comparison)
            {
                _summary.Text = _session.IsIndexed
                    ? App.Text("TreeGit.Binary.Summary", comparison.OldSize, comparison.NewSize, comparison.DifferentBytes)
                    : App.Text("TreeGit.Binary.Indexing");
            }
            else _summary.Text = App.Text("TreeGit.Binary.Loading");
            if (_comparison is { } snapshot)
            {
                _oldTitle.Text += App.Text("TreeGit.Binary.ByteCount", snapshot.OldSize);
                _newTitle.Text += App.Text("TreeGit.Binary.ByteCount", snapshot.NewSize);
            }
            _left.InvalidateVisual(); _right.InvalidateVisual();
        }

        private async void Stop()
        {
            var cancellation = _cancel; _cancel = null; cancellation?.Cancel();
            var session = _session; _session = null; _comparison = null;
            var navigation = _navigation; _navigation = Task.CompletedTask;
            _selected = -1; _address.Text = string.Empty; ResizeRows();
            _navigationVersion = 0;
            await navigation;
            if (session != null) await session.ReleaseAsync();
            cancellation?.Dispose();
        }

        private async void Reload()
        {
            Stop();
            if (DataContext is not Models.BinaryDiff diff) return;
            var cancellation = new CancellationTokenSource(); _cancel = cancellation;
            var token = cancellation.Token;
            var session = _sharedSession?.Retain() ?? new ComparisonSession(diff);
            _sharedSession = null;
            _session = session;
            _retry.IsVisible = false; _errorDetails = null;
            Status = "loading"; _summary.Text = App.Text("TreeGit.Binary.Loading");
            _oldTitle.Text = App.Text("TreeGit.Binary.OldPrefix") + Label(diff.OldRevision, diff.NewRevision) + " · " + DisplayPath(diff.OldFilePath);
            _newTitle.Text = App.Text("TreeGit.Binary.NewPrefix") + Label(diff.NewRevision) + " · " + Native.OS.FormatLocalPath(diff.FilePath);
            try
            {
                var comparison = await session.Snapshots.WaitAsync(token);
                if (_cancel != cancellation) return;
                _comparison = comparison; _selected = -1;
                RefreshLocalizedLabels();
                ResizeRows();
                if (!session.IsIndexed)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, token);
                await session.IndexAsync(comparison).WaitAsync(token);
                if (_cancel != cancellation) return;
                if (_navigationVersion == 0 && TopOffset == 0) await NavigateAsync("first");
                if (_cancel != cancellation || Status == "error") return;
                Status = "ready";
                RefreshLocalizedLabels();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportFailure(cancellation, ex); }
        }

        private void ReportFailure(CancellationTokenSource cancellation, Exception error)
        {
            if (_cancel != cancellation) return;
            Status = "error"; _errorDetails = error.Message;
            _summary.Text = App.Text("BinaryDiff.LoadFailed"); _retry.IsVisible = true;
        }

        public void Retry()
        {
            if (_attached && Status == "error") Reload();
        }

        private static string Label(string revision, string comparedRevision = null)
        {
            if (revision == null) return App.Text("TreeGit.Binary.WorkingTree");
            if (revision.Length == 0) return App.Text("TreeGit.Binary.Index");
            if (comparedRevision != null && revision == comparedRevision + "^") return App.Text("Diff.Source.Parent");
            // Shorten only object IDs. Truncating refs or revision expressions
            // can hide their distinguishing suffix, including a parent '^'.
            return revision.Length > 14 && revision.All(Uri.IsHexDigit) ? revision[..12] : revision;
        }

        public async Task NavigateAsync(string action)
        {
            if (_comparison == null || _cancel == null) return;
            var comparison = _comparison;
            var session = _session;
            var cancellation = _cancel;
            var direction = action is "previous" or "last" ? -1 : 1;
            var current = action is "first" or "last" ? -1 : _selected;
            var request = ++_navigationVersion;
            var pending = NavigateCoreAsync(comparison, session, cancellation, current, direction, request);
            // Detaching must allow all readers to finish before releasing the session.
            _navigation = Task.WhenAll(_navigation, pending);
            await pending;
        }

        private async Task NavigateCoreAsync(Models.BinaryComparison comparison, ComparisonSession session,
            CancellationTokenSource cancellation, long current, int direction, int request)
        {
            var token = cancellation.Token;
            try
            {
                await session.IndexAsync(comparison).WaitAsync(token);
                var offset = await Task.Run(() => comparison.Navigate(current, direction, token), token);
                if (_cancel == cancellation && request == _navigationVersion && offset >= 0) GoTo(offset);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportFailure(cancellation, ex); }
        }

        public void GoTo(long offset)
        {
            if (_comparison == null || offset < 0 || offset >= _comparison.Length) return;
            _navigationVersion++;
            _selected = offset; _address.Text = offset.ToString("X");
            _scroll.Value = Math.Min(_scroll.Maximum, offset / _bytesPerRow); Redraw();
        }
        private void Jump()
        {
            var text = _address.Text?.Trim() ?? "";
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset)) GoTo(offset);
        }
        private void ResizeRows()
        {
            var top = TopOffset;
            var paneWidth = Math.Min(_left.Bounds.Width, _right.Bounds.Width);
            var addressWidth = _comparison?.Length > uint.MaxValue ? 102 : 74;
            _bytesPerRow = 1;
            foreach (var count in new[] { 16, 8, 4, 2, 1 })
                if (addressWidth + count * 23 + 10 + Math.Max(count * 8, 36) <= paneWidth)
                {
                    _bytesPerRow = count;
                    break;
                }
            var rows = Math.Max(1, Math.Floor((_left.Bounds.Height - 24) / 20));
            var total = Math.Ceiling((_comparison?.Length ?? 0) / (double)_bytesPerRow);
            _scroll.Maximum = Math.Max(0, total - rows); _scroll.ViewportSize = rows; _scroll.LargeChange = rows;
            _scroll.Value = Math.Clamp(top / _bytesPerRow, 0, _scroll.Maximum); Redraw();
        }
        private void Redraw() { _left.InvalidateVisual(); _right.InvalidateVisual(); }
        public void OpenComparisonWindow()
        {
            if (!_allowSeparateWindow || DataContext is not Models.BinaryDiff diff || TopLevel.GetTopLevel(this) is not Window owner || owner is InternalDiffWindow) return;
            var window = new OwnedNativeWindow { [!Window.TitleProperty] = App.TextBinding("TreeGit.Binary.WindowTitle"), Width = 1340, Height = 760, MinWidth = 640, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new BinaryDiffView(false) { DataContext = diff, _sharedSession = Status == "error" ? null : _session } };
            // Use native frame for this independent inspector, including move/resize/close.
            window.SystemDecorations = SystemDecorations.Full;
            window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
            var screen = owner.Screens.ScreenFromWindow(owner);
            if (screen != null)
            {
                // Width/Height describe the client area. Reserve room for the
                // native frame and caption, including on a small or scaled desktop.
                var area = screen.WorkingArea;
                var scaling = owner.RenderScaling;
                var maxWidth = Math.Max(1, area.Width / scaling - 32);
                var maxHeight = Math.Max(1, area.Height / scaling - 64);
                window.MinWidth = Math.Min(window.MinWidth, maxWidth);
                window.MinHeight = Math.Min(window.MinHeight, maxHeight);
                window.Width = Math.Min(window.Width, maxWidth);
                window.Height = Math.Min(window.Height, maxHeight);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(
                    area.X + (int)Math.Max(8 * scaling, (area.Width - (window.Width + 16) * scaling) / 2),
                    area.Y + (int)Math.Max(8 * scaling, (area.Height - (window.Height + 48) * scaling) / 2));
            }
            window.Show(owner);
        }

        // Both inspectors keep the same immutable pair and one difference index.
        // The final view releases the files after snapshot work and readers finish.
        private sealed class ComparisonSession
        {
            private readonly CancellationTokenSource _lifetime = new();
            private Task _index;
            private int _views = 1;
            public Task<Models.BinaryComparison> Snapshots { get; }

            public ComparisonSession(Models.BinaryDiff diff)
            {
                Snapshots = Commands.BinarySnapshots.LoadAsync(diff, _lifetime.Token);
            }
            public bool IsIndexed => _index?.IsCompletedSuccessfully == true;

            public ComparisonSession Retain()
            {
                _views++;
                return this;
            }

            public Task IndexAsync(Models.BinaryComparison comparison) => _index ??= comparison.ScanAsync(_lifetime.Token);

            public async Task ReleaseAsync()
            {
                if (--_views != 0) return;
                _lifetime.Cancel();
                try
                {
                    using var comparison = await Snapshots;
                    if (_index != null) await _index;
                }
                catch (Exception)
                {
                    // Load and scan failures are reported by the view that requested them.
                }
                finally { _lifetime.Dispose(); }
            }
        }

        private static string DisplayPath(string path) =>
            path == "/dev/null" ? App.Text("Diff.Source.Empty") : Native.OS.FormatLocalPath(path);

        private sealed class HexPane(BinaryDiffView owner, bool old) : Control
        {
            static HexPane() => AffectsRender<HexPane>(ThemeVariantScope.ActualThemeVariantProperty);

            public override void Render(DrawingContext context)
            {
                base.Render(context);
                if (owner._comparison is not { } comparison) return;
                using var clip = context.PushClip(new Rect(Bounds.Size));
                var fg = this.FindResource("Brush.FG1") as IBrush ?? Brushes.Black;
                var dim = this.FindResource("Brush.FG2") as IBrush ?? Brushes.Gray;
                var changed = new SolidColorBrush(old ? Color.FromArgb(75, 240, 90, 90) : Color.FromArgb(75, 50, 190, 100));
                var highlight = this.FindResource(old ? "Brush.Diff.DeletedHighlight" : "Brush.Diff.AddedHighlight") as IBrush
                    ?? (old ? Brushes.IndianRed : Brushes.SeaGreen);
                var addressHighlight = this.FindResource("Brush.Diff.EmptyBG") as IBrush;
                var typeface = new Typeface("fonts:SourceGit#JetBrains Mono NL");
                var boldTypeface = new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.Bold);
                void Text(string value, double x, double y, IBrush brush, bool bold = false, IBrush background = null)
                {
                    var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        bold ? boldTypeface : typeface, 12, brush);
                    if (background != null)
                        context.FillRectangle(background, new Rect(x, y - 1, text.WidthIncludingTrailingWhitespace, 19));
                    context.DrawText(text, new Point(x, y));
                }
                var columns = owner._bytesPerRow; var offset = owner.TopOffset;
                var rows = Math.Max(0, (int)Math.Ceiling((Bounds.Height - 24) / 20));
                var count = rows * columns;
                var a = comparison.Read(old, offset, count); var b = comparison.Read(!old, offset, count);
                Text(App.Text("TreeGit.Binary.Offset"), 6, 3, dim);
                var addressWidth = comparison.Length > uint.MaxValue ? 102 : 74;
                for (var c = 0; c < columns; c++) Text(c.ToString("X2"), addressWidth + c * 23, 3, dim);
                Text("ASCII", addressWidth + columns * 23 + 10, 3, dim);
                for (var row = 0; row < rows; row++)
                {
                    var rowOffset = offset + row * columns;
                    if (rowOffset >= comparison.Length) break;
                    var y = 26 + row * 20;
                    Text(rowOffset.ToString(comparison.Length > uint.MaxValue ? "X12" : "X8"), 6, y, dim);
                    for (var c = 0; c < columns; c++)
                    {
                        var i = row * columns + c;
                        if (offset + i >= comparison.Length) break;
                        var x = addressWidth + c * 23; var asciiX = addressWidth + columns * 23 + 10 + c * 8;
                        var different = Models.BinaryComparison.Different(a, b, i);
                        var selected = offset + i == owner._selected;
                        IBrush background = different ? changed : null;
                        if (selected)
                            background = different ? highlight : addressHighlight;
                        Text(i < a.Length ? a[i].ToString("X2") : "--", x, y, fg, selected, background);
                        Text(i < a.Length ? a[i] is >= 32 and <= 126 ? ((char)a[i]).ToString() : "." : "·", asciiX, y, fg, selected, background);
                    }
                }
            }
        }
    }
}
