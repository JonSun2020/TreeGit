using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using AvaloniaEdit.Utils;

namespace SourceGit.Views
{
    public class MergeConflictTextPresenter : TextEditor
    {
        public class ConflictMarkerTransformer : DocumentColorizingTransformer
        {
            public ConflictMarkerTransformer(MergeConflictTextPresenter presenter)
            {
                _presenter = presenter;
            }

            protected override void ColorizeLine(DocumentLine line)
            {
                var lines = _presenter.Lines;
                if (lines == null || line.LineNumber > lines.Count)
                    return;

                var info = lines[line.LineNumber - 1];
                if (info.Type == Models.ConflictLineType.Marker)
                {
                    ChangeLinePart(line.Offset, line.EndOffset, element =>
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(_presenter.FontFamily, FontStyle.Italic));
                        element.TextRunProperties.SetForegroundBrush(Brushes.Gray);
                    });
                }
            }

            private readonly MergeConflictTextPresenter _presenter;
        }

        public class LineBackgroundRenderer : IBackgroundRenderer
        {
            public KnownLayer Layer => KnownLayer.Background;

            public LineBackgroundRenderer(MergeConflictTextPresenter presenter)
            {
                _presenter = presenter;
            }

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                MergeConflictHighlight.Draw(_presenter, true, drawingContext, textView.Bounds.Width);
            }

            private readonly MergeConflictTextPresenter _presenter;
        }

        public static readonly DirectProperty<MergeConflictTextPresenter, string> FileNameProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictTextPresenter, string>(
                nameof(FileName),
                static o => o.FileName,
                static (o, v) => o.FileName = v);

        public string FileName
        {
            get => _fileName;
            set => SetAndRaise(FileNameProperty, ref _fileName, value);
        }

        public static readonly DirectProperty<MergeConflictTextPresenter, Models.ConflictPanelType> PanelTypeProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictTextPresenter, Models.ConflictPanelType>(
                nameof(PanelType),
                static o => o.PanelType,
                static (o, v) => o.PanelType = v);

        public Models.ConflictPanelType PanelType
        {
            get => _panelType;
            set => SetAndRaise(PanelTypeProperty, ref _panelType, value);
        }

        public static readonly DirectProperty<MergeConflictTextPresenter, List<Models.ConflictLine>> LinesProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictTextPresenter, List<Models.ConflictLine>>(
                nameof(Lines),
                static o => o.Lines,
                static (o, v) => o.Lines = v);

        public List<Models.ConflictLine> Lines
        {
            get => _lines;
            set => SetAndRaise(LinesProperty, ref _lines, value);
        }

        public static readonly DirectProperty<MergeConflictTextPresenter, int> MaxLineNumberProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictTextPresenter, int>(
                nameof(MaxLineNumber),
                static o => o.MaxLineNumber,
                static (o, v) => o.MaxLineNumber = v);

        public int MaxLineNumber
        {
            get => _maxLineNumber;
            set => SetAndRaise(MaxLineNumberProperty, ref _maxLineNumber, value);
        }

        public static readonly DirectProperty<MergeConflictTextPresenter, ViewModels.TextLineRange> DisplayRangeProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictTextPresenter, ViewModels.TextLineRange>(
                nameof(DisplayRange),
                static o => o.DisplayRange);

        public ViewModels.TextLineRange DisplayRange
        {
            get => _displayRange;
            set => SetAndRaise(DisplayRangeProperty, ref _displayRange, value);
        }

        public static readonly StyledProperty<IBrush> OursContentBackgroundProperty =
            AvaloniaProperty.Register<MergeConflictTextPresenter, IBrush>(nameof(OursContentBackground), new SolidColorBrush(Color.FromArgb(60, 0, 255, 0)));

        public IBrush OursContentBackground
        {
            get => GetValue(OursContentBackgroundProperty);
            set => SetValue(OursContentBackgroundProperty, value);
        }

        public static readonly StyledProperty<IBrush> TheirsContentBackgroundProperty =
            AvaloniaProperty.Register<MergeConflictTextPresenter, IBrush>(nameof(TheirsContentBackground), new SolidColorBrush(Color.FromArgb(60, 255, 0, 0)));

        public IBrush TheirsContentBackground
        {
            get => GetValue(TheirsContentBackgroundProperty);
            set => SetValue(TheirsContentBackgroundProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(TextEditor);

        public MergeConflictTextPresenter() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            BorderThickness = new Thickness(0);

            Options.EnableHyperlinks = false;
            Options.EnableEmailHyperlinks = false;
            Options.AllowScrollBelowDocument = false;

            TextArea.TextView.Margin = new Thickness(0);
            TextArea.LeftMargins.Add(new MergeConflictNumberMargin(this, true));
            TextArea.LeftMargins.Add(new MergeConflictSeparator(this, true));
            TextArea.TextView.BackgroundRenderers.Add(new LineBackgroundRenderer(this));
            AddHandler(PointerPressedEvent, OnComparisonPointerPressed, RoutingStrategies.Tunnel, true);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged += OnTextViewScrollChanged;
                _scrollViewer.Bind(ScrollViewer.OffsetProperty, new Binding("ScrollOffset", BindingMode.OneWay));
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            _textMate = Models.TextMateHelper.CreateForEditor(this);
            if (!string.IsNullOrEmpty(FileName))
                Models.TextMateHelper.SetGrammarByFileName(_textMate, FileName);

            AddHandler(ContextRequestedEvent, OnTextViewContextRequested, RoutingStrategies.Bubble, true);
            TextArea.TextView.VisualLinesChanged += OnTextViewVisualLinesChanged;
            TextArea.TextView.LineTransformers.Add(new ConflictMarkerTransformer(this));

            OnTextViewVisualLinesChanged(null, null);
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            RemoveHandler(ContextRequestedEvent, OnTextViewContextRequested);
            OpenContextMenu?.Close();
            TextArea.TextView.VisualLinesChanged -= OnTextViewVisualLinesChanged;

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            base.OnUnloaded(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == LinesProperty)
                UpdateContent();
            else if (change.Property == FileNameProperty)
                Models.TextMateHelper.SetGrammarByFileName(_textMate, FileName);
            else if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
                Models.TextMateHelper.SetThemeByApp(_textMate);
        }

        private void UpdateContent()
        {
            var lines = Lines;
            if (lines == null || lines.Count == 0)
            {
                Text = string.Empty;
                return;
            }

            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Content.Length > 1000)
                {
                    builder.Append(line.Content.AsSpan(0, 1000));
                    builder.Append($"...({line.Content.Length - 1000} characters trimmed)");
                }
                else
                {
                    builder.Append(line.Content);
                }

                builder.Append('\n');
            }

            Text = builder.ToString();
        }

        public ContextMenu OpenContextMenu { get; private set; }

        private void OnTextViewContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm) return;
            var index = e.TryGetPosition(TextArea.TextView, out var point)
                ? RegionAtY(point.Y + TextArea.TextView.VerticalOffset)
                : vm.RegionForSourceLine(TextArea.Caret.Line);
            if (index >= 0) vm.ActiveConflictIndex = index;
            var menu = new ContextMenu();
            var choices = MergeConflictMenu.AddChoices(menu, vm, index, PanelType);
            if (!string.IsNullOrEmpty(SelectedText))
            {
                if (choices) menu.Items.Add(new Separator());
                var selected = SelectedText;
                var copy = new MenuItem { Header = App.Text("Copy"), Icon = this.CreateMenuIcon("Icons.Copy") };
                copy.Click += async (_, ev) => { await this.CopyTextAsync(selected); ev.Handled = true; };
                menu.Items.Add(copy);
            }
            e.Handled = true;
            if (menu.Items.Count == 0) return;
            OpenContextMenu?.Close();
            OpenContextMenu = menu;
            menu.Closed += (_, _) => { if (OpenContextMenu == menu) OpenContextMenu = null; };
            menu.Open(this);
        }

        private void OnTextViewVisualLinesChanged(object sender, EventArgs e)
        {
            if (Design.IsDesignMode)
                return;

            if (!TextArea.TextView.VisualLinesValid)
            {
                DisplayRange = null;
                return;
            }

            foreach (var margin in TextArea.LeftMargins)
                margin.InvalidateMeasure();

            var lines = Lines;
            var start = int.MaxValue;
            var count = 0;
            foreach (var line in TextArea.TextView.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted)
                    continue;

                var index = line.FirstDocumentLine.LineNumber - 1;
                if (index >= lines.Count)
                    continue;

                count++;
                if (start > index)
                    start = index;
            }

            DisplayRange = new(start, start + count);
        }

        private void OnTextViewScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer == null || DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            if (vm.ScrollOffset.NearlyEquals(_scrollViewer.Offset))
                return;

            if (IsPointerOver || e.OffsetDelta.SquaredLength > 1.0f)
            {
                vm.ScrollOffset = _scrollViewer.Offset;

            }
        }

        private void OnComparisonPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm) return;
            var index = RegionAtY(e.GetPosition(TextArea.TextView).Y + TextArea.TextView.VerticalOffset);
            if (index >= 0) vm.ActiveConflictIndex = index;
        }

        private int RegionAtY(double y)
        {
            var view = TextArea.TextView;
            if (!view.VisualLinesValid || DataContext is not ViewModels.MergeConflictEditor vm ||
                y < view.VerticalOffset || y >= view.VerticalOffset + view.Bounds.Height) return -1;
            foreach (var line in view.VisualLines)
            {
                if (line.IsDisposed || line.FirstDocumentLine == null || line.FirstDocumentLine.IsDeleted) continue;
                if (y >= line.VisualTop && y < line.VisualTop + line.Height)
                    return vm.RegionForSourceLine(line.FirstDocumentLine.LineNumber);
            }
            return -1;
        }

        private string _fileName = string.Empty;
        private Models.ConflictPanelType _panelType = Models.ConflictPanelType.Result;
        private List<Models.ConflictLine> _lines;
        private int _maxLineNumber = 0;
        private ViewModels.TextLineRange _displayRange = null;
        private TextMate.Installation _textMate;
        private ScrollViewer _scrollViewer;
    }

    public class MergeConflictMinimap : Control
    {
        public static readonly DirectProperty<MergeConflictMinimap, ViewModels.TextLineRange> DisplayRangeProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictMinimap, ViewModels.TextLineRange>(
                nameof(DisplayRange),
                static o => o.DisplayRange,
                static (o, v) => o.DisplayRange = v);

        public ViewModels.TextLineRange DisplayRange
        {
            get => _displayRange;
            set => SetAndRaise(DisplayRangeProperty, ref _displayRange, value);
        }

        public static readonly DirectProperty<MergeConflictMinimap, int> UnsolvedCountProperty =
            AvaloniaProperty.RegisterDirect<MergeConflictMinimap, int>(
                nameof(UnsolvedCount),
                static o => o.UnsolvedCount,
                static (o, v) => o.UnsolvedCount = v);

        public int UnsolvedCount
        {
            get => _unsolvedCount;
            set => SetAndRaise(UnsolvedCountProperty, ref _unsolvedCount, value);
        }

        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Bounds.Width, Bounds.Height));

            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var total = vm.OursLines.Count;
            var unitHeight = Bounds.Height / (total * 1.0);
            var conflicts = vm.ConflictRegions;
            var blockBGs = new SolidColorBrush[] { new(Colors.Red, 0.6), new(Colors.Green, 0.6) };
            for (var i = 0; i < conflicts.Count; i++)
            {
                var range = vm.ComparisonRanges[i];
                var topY = range.Start * unitHeight;
                var bottomY = (range.End + 1) * unitHeight;
                var bg = blockBGs[conflicts[i].IsResolved ? 1 : 0];
                context.DrawRectangle(bg, null, new Rect(0, topY, Bounds.Width, bottomY - topY));
            }

            var range = DisplayRange;
            if (range == null || range.End == 0)
                return;

            var startY = range.Start * unitHeight;
            var endY = range.End * unitHeight;
            var color = (Color)this.FindResource("SystemAccentColor")!;
            var brush = new SolidColorBrush(color, 0.2);
            var pen = new Pen(color.ToUInt32());
            var rect = new Rect(0, startY, Bounds.Width, endY - startY);

            context.DrawRectangle(brush, null, rect);
            context.DrawLine(pen, rect.TopLeft, rect.TopRight);
            context.DrawLine(pen, rect.BottomLeft, rect.BottomRight);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == DisplayRangeProperty ||
                change.Property == UnsolvedCountProperty ||
                change.Property.Name.Equals(nameof(ActualThemeVariant), StringComparison.Ordinal))
                InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            var total = vm.OursLines.Count;
            var range = DisplayRange;
            if (range == null || range.End == 0)
                return;

            var unitHeight = Bounds.Height / (total * 1.0);
            var startY = range.Start * unitHeight;
            var endY = range.End * unitHeight;
            var pressedY = e.GetPosition(this).Y;
            if (pressedY >= startY && pressedY <= endY)
                return;

            var line = Math.Max(1, Math.Min(total, (int)Math.Ceiling(pressedY / unitHeight)));
            var editor = this.FindAncestorOfType<MergeConflictEditor>();
            if (editor != null)
                editor.OursPresenter.ScrollToLine(line);
        }

        private ViewModels.TextLineRange _displayRange = null;
        private int _unsolvedCount = 0;
    }

    public partial class MergeConflictEditor : ChromelessWindow
    {
        public MergeConflictEditor()
        {
            InitializeComponent();
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            if (DataContext is not ViewModels.MergeConflictEditor vm)
                return;

            if (vm.IsSaving) { e.Cancel = true; return; }
            if (_forceClose || !vm.HasChanges) return;
            if (_confirmingClose) { e.Cancel = true; return; }
            _confirmingClose = true;

            e.Cancel = true;

            var confirm = new Confirm();
            confirm.SetData(App.Text("MergeConflictEditor.UnsavedChanges"), Models.ConfirmButtonType.OkCancel);

            var result = await confirm.ShowDialog<bool>(this);
            _confirmingClose = false;
            if (result)
            {
                _forceClose = true;
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is ViewModels.MergeConflictEditor vm) vm.PropertyChanged -= OnEditorStateChanged;
            base.OnClosed(e);
            GC.Collect();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (DataContext is ViewModels.MergeConflictEditor vm)
            {
                vm.PropertyChanged += OnEditorStateChanged;
                if (!vm.HasComparison)
                {
                    ConflictContent.RowDefinitions[0].MinHeight = 0;
                    ConflictContent.RowDefinitions[0].Height = new GridLength(0);
                    ConflictContent.RowDefinitions[1].Height = new GridLength(0);
                    ConflictContent.ColumnDefinitions[1].Width = new GridLength(0);
                }
                if (vm.ResultDocument != null) ResultPresenter.TextArea.Focus();
            }
        }

        private void OnEditorStateChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.MergeConflictEditor.UnsolvedCount) or nameof(ViewModels.MergeConflictEditor.ActiveConflictIndex))
            {
                foreach (var presenter in new TextEditor[] { OursPresenter, BasePresenter, TheirsPresenter, ResultPresenter })
                {
                    presenter.TextArea.TextView.Redraw();
                    foreach (var margin in presenter.TextArea.LeftMargins) margin.InvalidateVisual();
                }
            }
        }

        private void OnGotoPrevConflict(object sender, RoutedEventArgs e) => GotoConflict(false, e);
        private void OnGotoNextConflict(object sender, RoutedEventArgs e) => GotoConflict(true, e);

        private void GotoConflict(bool next, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.MergeConflictEditor vm && vm.TotalConflictCount > 0)
            {
                vm.ActiveConflictIndex = (vm.ActiveConflictIndex + (next ? 1 : vm.TotalConflictCount - 1)) % vm.TotalConflictCount;
                var index = vm.ActiveConflictIndex;
                var target = vm.ResultOffset(index);
                ResultPresenter.CaretOffset = target;
                ResultPresenter.ScrollToLine(vm.ResultDocument.GetLineByOffset(target).LineNumber);
                OursPresenter.ScrollToLine(vm.ComparisonRanges[index].Start + 1);
                ResultPresenter.TextArea.Focus();
            }
            e.Handled = true;
        }

        private void OnUndoResult(object sender, RoutedEventArgs e) => ResultPresenter.Undo();

        private async void OnSaveAndStage(object sender, RoutedEventArgs e)
        {
            await SaveAndCloseAsync();
        }

        public async System.Threading.Tasks.Task<bool> SaveAndCloseAsync()
        {
            if (DataContext is ViewModels.MergeConflictEditor vm)
            {
                var success = await vm.SaveAndStageAsync();
                if (success)
                {
                    _forceClose = true;
                    Close();
                }
                return success;
            }
            return false;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool _confirmingClose;
        private bool _forceClose = false;
    }
}
