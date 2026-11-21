using System;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public sealed class TextBoxCaret : Control
    {
        public static readonly AttachedProperty<bool> CorrectPositionProperty =
            AvaloniaProperty.RegisterAttached<TextBoxCaret, TextPresenter, bool>("CorrectPosition");

        private static readonly AttachedProperty<TextBoxCaret> ActiveCaretProperty =
            AvaloniaProperty.RegisterAttached<TextBoxCaret, TextPresenter, TextBoxCaret>("ActiveCaret");

        private readonly DispatcherTimer _blinkTimer = new();
        private readonly TextPresenter _presenter;
        private readonly Panel _surface;
        private readonly TextBox _textBox;
        private IDisposable _nativeCaret;
        private bool _visible;
        private bool _scrollQueued;

        static TextBoxCaret() => CorrectPositionProperty.Changed.AddClassHandler<TextPresenter>((presenter, _) =>
        {
            if (!GetCorrectPosition(presenter))
            {
                if (presenter.GetValue(ActiveCaretProperty) is { } caret)
                {
                    presenter.ClearValue(ActiveCaretProperty);
                    caret._surface.Children.Remove(caret);
                }
                return;
            }

            // Focus styles can apply while the template is still being attached.
            Dispatcher.UIThread.Post(() =>
            {
                if (!GetCorrectPosition(presenter) || !presenter.IsAttachedToVisualTree() ||
                    presenter.GetValue(ActiveCaretProperty) != null || presenter.TemplatedParent is not TextBox box ||
                    presenter.GetVisualParent() is not Panel surface)
                    return;

                // Keep the native caret if a future Avalonia version changes the geometry API.
                try { ReadCursorRectangle(presenter); }
                catch (MissingMethodException) { return; }

                var caret = new TextBoxCaret(presenter, box, surface);
                presenter.SetValue(ActiveCaretProperty, caret);
                surface.Children.Add(caret);
            }, DispatcherPriority.Normal);
        });

        public static bool GetCorrectPosition(TextPresenter presenter) => presenter.GetValue(CorrectPositionProperty);
        public static void SetCorrectPosition(TextPresenter presenter, bool value) => presenter.SetValue(CorrectPositionProperty, value);

        private TextBoxCaret(TextPresenter presenter, TextBox box, Panel surface)
        {
            _presenter = presenter;
            _surface = surface;
            _textBox = box;
            IsHitTestVisible = false;
            _blinkTimer.Tick += (_, _) =>
            {
                _visible = !_visible;
                InvalidateVisual();
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _nativeCaret = _presenter.SetValue(TextPresenter.CaretBrushProperty, Brushes.Transparent, BindingPriority.Animation);
            _presenter.CaretBoundsChanged += OnCaretMoved;
            _presenter.PropertyChanged += OnPresenterChanged;
            _textBox.PropertyChanged += OnTextBoxChanged;
            OnCaretMoved(null, EventArgs.Empty);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _blinkTimer.Stop();
            _presenter.CaretBoundsChanged -= OnCaretMoved;
            _presenter.PropertyChanged -= OnPresenterChanged;
            _textBox.PropertyChanged -= OnTextBoxChanged;
            _nativeCaret?.Dispose();
            _nativeCaret = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnPresenterChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextPresenter.SelectionStartProperty || e.Property == TextPresenter.SelectionEndProperty ||
                e.Property == TextPresenter.CaretBlinkIntervalProperty)
                RestartBlink();
            else if (e.Property == TextElement.ForegroundProperty)
                InvalidateVisual();
        }

        private void OnTextBoxChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.CaretBrushProperty)
                InvalidateVisual();
        }

        private void RestartBlink()
        {
            _blinkTimer.Stop();
            _visible = true;
            var interval = _presenter.CaretBlinkInterval;
            if (interval > TimeSpan.Zero && _presenter.SelectionStart == _presenter.SelectionEnd)
            {
                _blinkTimer.Interval = interval;
                _blinkTimer.Start();
            }
            InvalidateVisual();
        }

        private void OnCaretMoved(object sender, EventArgs e)
        {
            if (_scrollQueued) return;
            _scrollQueued = true;
            // The formatter can report new bounds during rendering.
            Dispatcher.UIThread.Post(() =>
            {
                _scrollQueued = false;
                if (this.IsAttachedToVisualTree() && _textBox.IsFocused)
                {
                    RestartBlink();
                    _ = _presenter.TextLayout;
                    var caret = ReadCursorRectangle(_presenter);
                    // Include the visible stroke when the caret reaches the end of a long line.
                    _presenter.BringIntoView(caret.WithWidth(_presenter.Margin.Right));
                }
            }, DispatcherPriority.Loaded);
        }

        public override void Render(DrawingContext context)
        {
            if (!_visible || !_textBox.IsFocused || _presenter.SelectionStart != _presenter.SelectionEnd)
                return;

            var bounds = GetPaintBounds();
            if (bounds.Height > 0 && _presenter.TranslatePoint(default, _surface) is { } origin)
                context.FillRectangle(_textBox.CaretBrush ?? _presenter.Foreground ?? Brushes.Black, bounds.Translate((Vector)origin));
        }

        internal Rect GetPaintBounds()
        {
            var presenter = _presenter;
            var textHeight = presenter.TextLayout.Height;
            var caret = ReadCursorRectangle(presenter);
            var extra = Math.Min(0, presenter.Bounds.Height - textHeight);
            var top = caret.Y + (presenter.VerticalAlignment switch
            {
                VerticalAlignment.Center => extra / 2,
                VerticalAlignment.Bottom => extra,
                _ => 0,
            });

            if (TopLevel.GetTopLevel(presenter) is not { } root || presenter.TransformToVisual(root) is not { } transform ||
                Math.Abs(transform.M11) < 0.001)
                return new Rect(caret.X, top, 1, caret.Height);

            var scale = root.RenderScaling;
            var horizontalScale = transform.M11 * scale;
            var physicalX = (caret.X * transform.M11 + transform.M31) * scale;
            var x = caret.X + (Math.Ceiling(physicalX - 0.000001) - physicalX) / horizontalScale;
            return new Rect(x, top, 1 / Math.Abs(horizontalScale), caret.Height);
        }

        // Avalonia rounds and moves the painted line inward. Use its unrounded
        // insertion geometry so wrapped lines, bidi text and IME share one caret.
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetCursorRectangle")]
        private static extern Rect ReadCursorRectangle(TextPresenter presenter);
    }
}
