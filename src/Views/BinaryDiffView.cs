using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public sealed class BinaryDiffView : UserControl
    {
        private readonly TextBlock _summary = new() { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _oldTitle = new() { Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _newTitle = new() { Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBox _address = new() { Width = 116, Watermark = "十六进制地址", Margin = new Thickness(4, 0) };
        private readonly ScrollBar _scroll = new() { Orientation = Avalonia.Layout.Orientation.Vertical, SmallChange = 1 };
        private readonly HexPane _left;
        private readonly HexPane _right;
        private CancellationTokenSource _cancel;
        private Models.BinaryComparison _comparison;
        private Task _navigation = Task.CompletedTask;
        private long _selected = -1;
        private int _bytesPerRow = 16;
        private bool _attached;
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
            summary = _summary.Text, oldSize = OldSize, newSize = NewSize,
            differentBytes = DifferentBytes, selectedOffset = SelectedOffset, topOffset = TopOffset, bytesPerRow = BytesPerRow,
            oldHex = _comparison == null ? null : Convert.ToHexString(_comparison.Read(true, TopOffset, 256)),
            newHex = _comparison == null ? null : Convert.ToHexString(_comparison.Read(false, TopOffset, 256)) };

        public BinaryDiffView()
        {
            _left = new HexPane(this, true);
            _right = new HexPane(this, false);
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
            root.Bind(BackgroundProperty, new DynamicResourceExtension("Brush.Contents"));
            root.Children.Add(_summary);
            var toolbar = new WrapPanel { Margin = new Thickness(6, 2), Orientation = Avalonia.Layout.Orientation.Horizontal };
            AddButton(toolbar, "首个差异", async () => await NavigateAsync("first"));
            AddButton(toolbar, "上一差异", async () => await NavigateAsync("previous"));
            AddButton(toolbar, "下一差异", async () => await NavigateAsync("next"));
            AddButton(toolbar, "末个差异", async () => await NavigateAsync("last"));
            toolbar.Children.Add(_address);
            AddButton(toolbar, "跳转", Jump);
            AddButton(toolbar, "独立窗口", OpenComparisonWindow);
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
            _scroll.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Redraw(); };
            SizeChanged += (_, _) => ResizeRows();
            PointerWheelChanged += (_, e) => { _scroll.Value = Math.Clamp(_scroll.Value - e.Delta.Y * 3, 0, _scroll.Maximum); e.Handled = true; };
            _address.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Jump(); e.Handled = true; } };
        }

        private static void AddButton(Panel panel, string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(2), Padding = new Thickness(8, 3) };
            button.Classes.Add("flat"); button.Click += (_, _) => action(); panel.Children.Add(button);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e); _attached = true; Reload();
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false; Stop(); base.OnDetachedFromVisualTree(e);
        }
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e); if (_attached) Reload();
        }

        private async void Stop()
        {
            var cancellation = _cancel; _cancel = null; cancellation?.Cancel();
            var comparison = _comparison; _comparison = null;
            try { await _navigation; } catch (OperationCanceledException) { }
            comparison?.Dispose(); cancellation?.Dispose();
        }

        private async void Reload()
        {
            Stop();
            if (DataContext is not Models.BinaryDiff diff) return;
            var cancellation = new CancellationTokenSource(); _cancel = cancellation;
            Status = "loading"; _summary.Text = "正在读取二进制文件并比较…";
            _oldTitle.Text = "旧版本 · " + Label(diff.OldRevision) + " · " + diff.OldFilePath;
            _newTitle.Text = "新版本 · " + Label(diff.NewRevision) + " · " + diff.FilePath;
            try
            {
                var comparison = await Commands.BinarySnapshots.LoadAsync(diff, cancellation.Token);
                if (_cancel != cancellation) { comparison.Dispose(); return; }
                _comparison = comparison; _selected = -1;
                _summary.Text = $"旧：{comparison.OldSize:N0} 字节    新：{comparison.NewSize:N0} 字节    差异：{comparison.DifferentBytes:N0} 字节 · 按相同偏移比较（只读）";
                _oldTitle.Text += $" · {comparison.OldSize:N0} 字节";
                _newTitle.Text += $" · {comparison.NewSize:N0} 字节";
                ResizeRows(); await NavigateAsync("first");
                if (_cancel == cancellation) Status = "ready";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_cancel != cancellation) return;
                Status = "error"; _summary.Text = "无法比较二进制文件：" + ex.Message;
            }
        }

        private static string Label(string revision) => revision == null ? "工作区" : revision.Length == 0 ? "暂存区" : revision.Length > 14 ? revision[..12] : revision;

        public async Task NavigateAsync(string action)
        {
            if (_comparison == null || _cancel == null) return;
            var comparison = _comparison; var cancellation = _cancel;
            var direction = action is "previous" or "last" ? -1 : 1;
            var current = action is "first" or "last" ? -1 : _selected;
            try
            {
                var pending = Task.Run(() => comparison.Navigate(current, direction, cancellation.Token), cancellation.Token);
                // Chain all readers so detaching the view cannot dispose a snapshot in use.
                _navigation = Task.WhenAll(_navigation, pending);
                var offset = await pending;
                if (_comparison == comparison && offset >= 0) GoTo(offset);
            }
            catch (OperationCanceledException) { }
        }

        public void GoTo(long offset)
        {
            if (_comparison == null || offset < 0 || offset >= _comparison.Length) return;
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
            _bytesPerRow = Bounds.Width >= 1170 ? 16 : Bounds.Width >= 730 ? 8 : 4;
            var rows = Math.Max(1, Math.Floor((_left.Bounds.Height - 24) / 20));
            var total = Math.Ceiling((_comparison?.Length ?? 0) / (double)_bytesPerRow);
            _scroll.Maximum = Math.Max(0, total - rows); _scroll.ViewportSize = rows; _scroll.LargeChange = rows;
            _scroll.Value = Math.Clamp(top / _bytesPerRow, 0, _scroll.Maximum); Redraw();
        }
        private void Redraw() { _left.InvalidateVisual(); _right.InvalidateVisual(); }
        public void OpenComparisonWindow()
        {
            if (DataContext is not Models.BinaryDiff diff || TopLevel.GetTopLevel(this) is not Window owner) return;
            var window = new Window { Title = "TreeGit · 二进制对比", Width = 1340, Height = 760, MinWidth = 640, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new BinaryDiffView { DataContext = diff } };
            // Use native frame for this independent inspector, including move/resize/close.
            window.SystemDecorations = SystemDecorations.Full;
            window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
            var screen = owner.Screens.ScreenFromWindow(owner);
            if (screen != null) { window.MaxWidth = screen.WorkingArea.Width / owner.RenderScaling; window.MaxHeight = screen.WorkingArea.Height / owner.RenderScaling; }
            window.Show(owner);
        }

        private sealed class HexPane(BinaryDiffView owner, bool old) : Control
        {
            public override void Render(DrawingContext context)
            {
                base.Render(context);
                if (owner._comparison is not { } comparison) return;
                using var clip = context.PushClip(new Rect(Bounds.Size));
                var fg = this.FindResource("Brush.FG1") as IBrush ?? Brushes.Black;
                var dim = this.FindResource("Brush.FG2") as IBrush ?? Brushes.Gray;
                var changed = new SolidColorBrush(old ? Color.FromArgb(75, 240, 90, 90) : Color.FromArgb(75, 50, 190, 100));
                var selection = new Pen(this.FindResource("Brush.Accent") as IBrush ?? Brushes.DodgerBlue, 1);
                var typeface = new Typeface("fonts:SourceGit#JetBrains Mono NL");
                void Text(string value, double x, double y, IBrush brush) => context.DrawText(new FormattedText(value,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12, brush), new Point(x, y));
                var columns = owner._bytesPerRow; var offset = owner.TopOffset;
                var rows = Math.Max(0, (int)Math.Ceiling((Bounds.Height - 24) / 20));
                var count = rows * columns;
                var a = comparison.Read(old, offset, count); var b = comparison.Read(!old, offset, count);
                Text("偏移", 6, 3, dim);
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
                        if (Models.BinaryComparison.Different(a, b, i))
                        {
                            context.FillRectangle(changed, new Rect(x - 2, y - 1, 22, 19));
                            context.FillRectangle(changed, new Rect(asciiX, y - 1, 8, 19));
                        }
                        if (offset + i == owner._selected) context.DrawRectangle(selection, new Rect(x - 2, y - 1, 22, 19));
                        Text(i < a.Length ? a[i].ToString("X2") : "--", x, y, fg);
                        Text(i < a.Length ? a[i] is >= 32 and <= 126 ? ((char)a[i]).ToString() : "." : "·", asciiX, y, fg);
                    }
                }
            }
        }
    }
}
