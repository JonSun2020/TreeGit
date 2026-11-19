using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class LauncherPage
    {
        private readonly TranslateTransform _dialogTranslation = new();
        private ViewModels.LauncherPage _dialogPage;
        private ViewModels.Popup _dialog;
        private IPointer _dialogPointer;
        private Point _dialogPointerStart;
        private Point _dialogPositionStart;
        private bool _dialogClampQueued;

        private void InitializeDialogDragging()
        {
            PopupDialogSurface.RenderTransform = _dialogTranslation;
            DataContextChanged += (_, _) =>
            {
                if (this.GetVisualRoot() != null) TrackDialogPage();
            };
            PopupArea.SizeChanged += (_, _) => QueueDialogClamp();
            PopupDialogSurface.SizeChanged += (_, _) => { ClampDialogPosition(); QueueDialogClamp(); };
            PopupDialogSurface.LayoutUpdated += (_, _) =>
            {
                if (_dialog != null && _dialog.DialogPosition == null)
                {
                    ClampDialogPosition();
                    QueueDialogClamp();
                }
                else if (_dialog?.DialogPosition is { } desired &&
                    PopupDialogSurface.TranslatePoint(default, PopupArea) is { } actual &&
                    (Math.Abs(actual.X - desired.X) > .1 || Math.Abs(actual.Y - desired.Y) > .1))
                    ClampDialogPosition();
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TrackDialogPage();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            EndDialogDrag();
            if (_dialogPage != null) _dialogPage.PropertyChanged -= OnDialogPageChanged;
            _dialogPage = null;
            _dialog = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void TrackDialogPage()
        {
            if (_dialogPage != null) _dialogPage.PropertyChanged -= OnDialogPageChanged;
            _dialogPage = DataContext as ViewModels.LauncherPage;
            if (_dialogPage != null) _dialogPage.PropertyChanged += OnDialogPageChanged;
            RestoreDialogPosition();
        }

        private void OnDialogPageChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.LauncherPage.Popup)) RestoreDialogPosition();
        }

        private void RestoreDialogPosition()
        {
            EndDialogDrag();
            _dialog = _dialogPage?.Popup;
            PopupDialogSurface.MaxHeight = double.PositiveInfinity;
            _dialogTranslation.X = _dialogTranslation.Y = 0;
            QueueDialogClamp();
        }

        private void QueueDialogClamp()
        {
            if (_dialogClampQueued) return;
            _dialogClampQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _dialogClampQueued = false;
                DialogButtons.Apply(PopupArea);
                ClampDialogPosition(captureInitial: true);
            }, DispatcherPriority.Loaded);
        }

        private void ClampDialogPosition(Point? requested = null, bool captureInitial = false)
        {
            if (_dialog == null || !PopupDialogSurface.IsEffectivelyVisible ||
                PopupArea.Bounds.Width <= 0 || PopupArea.Bounds.Height <= 0 ||
                PopupDialogSurface.Bounds.Width <= 0 || PopupDialogSurface.Bounds.Height <= 0 ||
                PopupDialogSurface.TranslatePoint(default, PopupArea) is not { } current)
                return;

            var origin = current - new Vector(_dialogTranslation.X, _dialogTranslation.Y);
            // The first SizeChanged can precede Arrange after the main window
            // resized. Its old translated origin is not the new dialog center.
            var target = requested ?? _dialog.DialogPosition ?? new Point(
                (PopupArea.Bounds.Width - PopupDialogSurface.Bounds.Width) / 2,
                (PopupArea.Bounds.Height - PopupDialogSurface.Bounds.Height) / 2);
            var left = Math.Clamp(target.X, 0, Math.Max(0, PopupArea.Bounds.Width - PopupDialogSurface.Bounds.Width));
            var top = Math.Clamp(target.Y, 0, Math.Max(0, PopupArea.Bounds.Height - PopupDialogSurface.Bounds.Height));
            _dialogTranslation.X = left - origin.X;
            _dialogTranslation.Y = top - origin.Y;
            // Pin only once layout has settled, or the user starts moving it.
            // Later progress output can then grow down without moving the title.
            if (captureInitial || requested.HasValue || _dialog.DialogPosition.HasValue)
            {
                _dialog.DialogPosition = new Point(left, top);
                PopupDialogSurface.MaxHeight = Math.Max(1, PopupArea.Bounds.Height - top);
            }
        }

        private void OnPopupDragPressed(object sender, PointerPressedEventArgs e)
        {
            if (_dialog == null || !e.GetCurrentPoint(PopupDialogSurface).Properties.IsLeftButtonPressed) return;
            var onTitle = false;
            for (var control = e.Source as Control; control != null && control != PopupDialogSurface; control = control.GetVisualParent() as Control)
            {
                if (control is Button) return;
                if (control.Name == "PopupDragHandle") onTitle = true;
            }
            // Only the title starts a drag; the form and empty body keep normal input behavior.
            if (!onTitle) return;
            if (PopupDialogSurface.TranslatePoint(default, PopupArea) is not { } position) return;
            _dialogPointerStart = e.GetPosition(PopupArea);
            _dialogPositionStart = position;
            _dialogPointer = e.Pointer;
            e.Pointer.Capture(PopupDialogSurface);
            e.Handled = true;
        }

        private void OnPopupDragMoved(object sender, PointerEventArgs e)
        {
            if (_dialogPointer != e.Pointer || e.Pointer.Captured != PopupDialogSurface) return;
            ClampDialogPosition(_dialogPositionStart + (e.GetPosition(PopupArea) - _dialogPointerStart));
            e.Handled = true;
        }

        private void OnPopupDragReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_dialogPointer != e.Pointer) return;
            EndDialogDrag();
            e.Handled = true;
        }

        private void OnPopupDragCaptureLost(object sender, PointerCaptureLostEventArgs e) => EndDialogDrag();

        private void EndDialogDrag()
        {
            var pointer = _dialogPointer;
            _dialogPointer = null;
            if (pointer?.Captured == PopupDialogSurface) pointer.Capture(null);
        }
    }
}
