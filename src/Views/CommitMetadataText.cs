using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public sealed record CommitMetadataSpan(string Name, string Text, int Start, string SHA = null, bool IsParent = false, bool IsLabel = false)
    {
        public int Length => Text.Length;
    }

    public sealed class CommitParentNavigationEventArgs(string sha) : EventArgs
    {
        public string SHA { get; } = sha;
    }

    // One selection surface for labels, values and the full commit message.
    // Keep inline foregrounds while selecting (set by the view): otherwise the
    // base control splits shaped runs at each selection edge and caret positions
    // can move under a captured pointer. A tinted background marks the selection.
    public sealed class CommitMetadataText : SelectableTextBlock
    {
        public static readonly StyledProperty<IBrush> LinkBrushProperty =
            AvaloniaProperty.Register<CommitMetadataText, IBrush>(nameof(LinkBrush), Brushes.DodgerBlue);
        public IBrush LinkBrush { get => GetValue(LinkBrushProperty); set => SetValue(LinkBrushProperty, value); }
        public string PlainText => _plainText;
        public IReadOnlyList<CommitMetadataSpan> Spans => _spans;
        public event EventHandler<CommitParentNavigationEventArgs> ParentNavigationRequested;
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        public CommitMetadataText()
        {
            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _scrollTimer.Tick += (_, _) => AutoScrollSelection();
            // Handle before the attached ContextMenu's bubbling handler, so every
            // request refreshes its text and SHA instead of reopening stale items.
            AddHandler(ContextRequestedEvent, OnMetadataContextRequested, RoutingStrategies.Tunnel);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            ViewModels.Preferences.Instance.PropertyChanged += OnPreferencesChanged;
            AttachDetail(DataContext as ViewModels.CommitDetail);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            ViewModels.Preferences.Instance.PropertyChanged -= OnPreferencesChanged;
            AttachDetail(null);
            StopDrag(); SetHoveredParent(null);
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_attached) AttachDetail(DataContext as ViewModels.CommitDetail);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (_attached && (change.Property == LinkBrushProperty || change.Property == FontSizeProperty)) Rebuild(true);
        }

        private void AttachDetail(ViewModels.CommitDetail detail)
        {
            if (_detail != null) _detail.PropertyChanged -= OnDetailChanged;
            foreach (var parent in _parents) parent.PropertyChanged -= OnParentChanged;
            _detail = detail;
            if (_detail != null) _detail.PropertyChanged += OnDetailChanged;
            ObserveParents();
            Rebuild();
        }

        private void ObserveParents()
        {
            foreach (var parent in _parents) parent.PropertyChanged -= OnParentChanged;
            _parents = _detail?.ParentRevisions ?? [];
            foreach (var parent in _parents) parent.PropertyChanged += OnParentChanged;
        }

        private void OnDetailChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ViewModels.CommitDetail.ParentRevisions)) ObserveParents();
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ViewModels.CommitDetail.Commit) or nameof(ViewModels.CommitDetail.ParentRevisions) or nameof(ViewModels.CommitDetail.DisplayMessage)) Rebuild();
        }
        private void OnParentChanged(object sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(Models.CommitParent.ShortSHA)) Rebuild(); }
        private void OnPreferencesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ViewModels.Preferences.Locale) or nameof(ViewModels.Preferences.DateTimeFormat) or nameof(ViewModels.Preferences.Use24Hours))
                Dispatcher.UIThread.Post(() => { if (_attached) Rebuild(); });
        }

        private void Rebuild(bool restyle = false)
        {
            var text = new StringBuilder();
            var spans = new List<CommitMetadataSpan>();
            void Add(string name, string value, string sha = null, bool parent = false, bool label = false)
            {
                value ??= string.Empty;
                spans.Add(new(name, value, text.Length, sha, parent, label));
                text.Append(value);
            }
            void Label(string name, string key) => Add(name, App.Text(key), label: true);
            var commit = _detail?.Commit;
            if (commit != null)
            {
                Label("CommitLabel", "CommitChanges.CommitLabel");
                Add("CommitSHA", Converters.StringConverters.FormatFullAndShortSHA(commit.SHA), commit.SHA);
                if (_parents.Count > 0)
                {
                    text.Append('\n'); Label("ParentLabel", "CommitDetail.Info.Parents");
                    for (var i = 0; i < _parents.Count; i++)
                    {
                        if (i > 0) text.Append(' ');
                        Add("ParentSHA", _parents[i].ShortSHA, _parents[i].SHA, parent: true);
                    }
                }
                text.Append('\n'); Label("AuthorLabel", "CommitChanges.AuthorLabel"); Add("Author", commit.Author?.ToString());
                text.Append('\n'); Label("DateLabel", "CommitChanges.DateLabel"); Add("Date", Models.DateTimeFormat.Format(commit.CommitterTime));
                text.Append('\n'); Label("CommitterLabel", "CommitDetail.Info.Committer"); Add("Committer", commit.Committer?.ToString());
                text.Append("\n\n"); Add("Message", _detail.DisplayMessage);
            }
            var next = text.ToString();
            _spans = spans;
            if (next == _plainText && !restyle) return;
            var start = SelectionStart; var end = SelectionEnd;
            var preserve = _revision == commit?.SHA && Math.Max(start, end) <= next.Length &&
                Math.Max(start, end) <= _plainText.Length && next.AsSpan(0, Math.Max(start, end)).SequenceEqual(_plainText.AsSpan(0, Math.Max(start, end)));
            _revision = commit?.SHA;
            _plainText = next;
            var runs = new List<Inline>(); var position = 0;
            foreach (var span in spans)
            {
                if (span.Start > position) runs.Add(new Run(next[position..span.Start]) { FontSize = FontSize });
                var run = new Run(span.Text) { FontSize = FontSize };
                if (span.IsLabel) run.FontWeight = Avalonia.Media.FontWeight.Bold;
                if (span.IsParent) { run.Foreground = LinkBrush; run.TextDecorations = Avalonia.Media.TextDecorations.Underline; }
                runs.Add(run); position = span.Start + span.Length;
            }
            Inlines!.Clear(); Inlines.AddRange(runs);
            SelectionStart = preserve ? start : 0;
            SelectionEnd = preserve ? end : 0;
            SetHoveredParent(null);
        }

        public CommitMetadataSpan SpanAt(Point point)
        {
            var local = point - new Point(Padding.Left, Padding.Top);
            var hit = TextLayout.HitTestPoint(local);
            var index = hit.CharacterHit.FirstCharacterIndex;
            var span = _spans.FirstOrDefault(s => index >= s.Start && index < s.Start + s.Length);
            // Check actual glyph bounds: the framework's IsInside compares the
            // document's Y coordinate with one line's height on multiline text.
            return span != null && TextLayout.HitTestTextRange(span.Start, span.Length).Any(rect => rect.Contains(local)) ? span : null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            _characterDrag = point.Properties.IsLeftButtonPressed && e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None;
            _pressedParent = point.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None && SpanAt(point.Position) is { IsParent: true } parent ? parent : null;
            _pressPosition = point.Position; _dragged = false;
            SetHoveredParent(null);
            Focus(NavigationMethod.Pointer);
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured == this && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var delta = e.GetPosition(this) - _pressPosition;
                if (delta.X * delta.X + delta.Y * delta.Y > 9) _dragged = true;
                _dragEvent = e; _scrollTimer.Start();
                SetHoveredParent(null);
            }
            else
                SetHoveredParent(SpanAt(e.GetPosition(this)) is { IsParent: true } parent ? parent : null);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton == MouseButton.Left && _characterDrag)
            {
                var delta = e.GetPosition(this) - _pressPosition;
                _dragged |= delta.X * delta.X + delta.Y * delta.Y > 9;
                if (_dragged)
                {
                    // The OS can coalesce the final move into the release. Use
                    // the release position as the final selection endpoint.
                    var point = e.GetPosition(this) - new Point(Padding.Left, Padding.Top);
                    point = new Point(Math.Clamp(point.X, 0, Math.Max(TextLayout.WidthIncludingTrailingWhitespace, 0)), Math.Clamp(point.Y, 0, Math.Max(TextLayout.Height, 0)));
                    SetCurrentValue(SelectionEndProperty, TextLayout.HitTestPoint(point).TextPosition);
                }
            }
            var navigate = e.InitialPressMouseButton == MouseButton.Left && !_dragged && _pressedParent != null &&
                Equals(_pressedParent, SpanAt(e.GetPosition(this))) && SelectionStart == SelectionEnd ? _pressedParent.SHA : null;
            base.OnPointerReleased(e);
            StopDrag();
            if (navigate != null) { e.Handled = true; ParentNavigationRequested?.Invoke(this, new(navigate)); }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { StopDrag(); base.OnPointerCaptureLost(e); }
        protected override void OnPointerExited(PointerEventArgs e) { SetHoveredParent(null); base.OnPointerExited(e); }
        private void StopDrag() { _scrollTimer.Stop(); _dragEvent = null; _pressedParent = null; _characterDrag = false; }
        private void AutoScrollSelection()
        {
            var e = _dragEvent;
            if (e == null || e.Pointer.Captured != this) { _scrollTimer.Stop(); return; }
            var scroll = this.FindAncestorOfType<ScrollViewer>();
            if (scroll == null) return;
            var y = e.GetPosition(scroll).Y;
            // Staying inside the pane must not move text under a normal drag.
            // Scroll only once the captured pointer leaves its viewport.
            if (y < 0) scroll.LineUp();
            else if (y > scroll.Bounds.Height) scroll.LineDown();
            else return;
            _dragged = true;
            base.OnPointerMoved(e);
        }

        private void SetHoveredParent(CommitMetadataSpan parent)
        {
            if (Equals(_hoveredParent, parent)) return;
            _hoveredParent = parent;
            SetCurrentValue(CursorProperty, Cursor.Parse(parent == null ? "IBeam" : "Hand"));
            ToolTip.SetTip(this, parent?.SHA);
            ToolTip.SetIsOpen(this, parent != null);
        }

        private void OnMetadataContextRequested(object sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;
            SetHoveredParent(null);
            var selected = SelectedText;
            var whole = PlainText;
            var copy = new MenuItem { Name = "CopyMetadataSelection", Header = App.Text("Copy"), IsEnabled = selected.Length > 0, InputGesture = TextBox.CopyGesture };
            copy.Click += async (_, ev) => { await this.CopyTextAsync(selected); ev.Handled = true; };
            var all = new MenuItem { Name = "CopyMetadataAll", Header = App.Text("CopyAllText") };
            all.Click += async (_, ev) => { await this.CopyTextAsync(whole); ev.Handled = true; };
            var items = new List<MenuItem> { copy, all };
            var low = Math.Min(SelectionStart, SelectionEnd); var high = Math.Max(SelectionStart, SelectionEnd);
            var span = e.TryGetPosition(this, out var point) ? SpanAt(point) : _spans.FirstOrDefault(s => s.SHA != null && low >= s.Start && high <= s.Start + s.Length);
            if (span?.SHA is { } sha)
            {
                var full = new MenuItem { Name = "CopyMetadataSHA", Header = App.Text("CommitChanges.CopySHA") };
                full.Click += async (_, ev) => { await this.CopyTextAsync(sha); ev.Handled = true; };
                items.Add(full);
            }
            ContextMenu ??= new ContextMenu();
            ContextMenu.ItemsSource = items;
            ContextMenu.Open(this);
        }

        private ViewModels.CommitDetail _detail;
        private IReadOnlyList<Models.CommitParent> _parents = [];
        private IReadOnlyList<CommitMetadataSpan> _spans = [];
        private string _plainText = string.Empty;
        private string _revision;
        private bool _attached;
        private bool _dragged;
        private bool _characterDrag;
        private Point _pressPosition;
        private CommitMetadataSpan _pressedParent;
        private CommitMetadataSpan _hoveredParent;
        private PointerEventArgs _dragEvent;
        private readonly DispatcherTimer _scrollTimer;
    }
}
