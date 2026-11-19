using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class OperationProgressView : UserControl
    {
        private bool _followOutput = true;
        private bool _scrollPending;

        public OperationProgressView()
        {
            InitializeComponent();
            OperationOutputText.OutputUpdated += (_, _) => QueueScroll();
            OperationOutputScroll.ScrollChanged += OnOutputScrollChanged;
            ShowFullOperationOutput.IsCheckedChanged += (_, _) =>
            {
                _followOutput = true;
                QueueScroll();
            };
            DataContextChanged += (_, _) => _followOutput = true;
        }

        private void OnOutputScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var scroll = OperationOutputScroll;
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
                QueueScroll();
            else if (e.OffsetDelta.Y != 0)
                _followOutput = scroll.ScrollBarMaximum.Y - scroll.Offset.Y <= 2;
        }

        private void QueueScroll()
        {
            if (_scrollPending || !_followOutput || !OperationOutputScroll.IsVisible) return;
            _scrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollPending = false;
                var scroll = OperationOutputScroll;
                if (_followOutput && scroll.IsEffectivelyVisible)
                    scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            }, DispatcherPriority.Loaded);
        }
        private void OnCancelOperation(object sender, RoutedEventArgs args)
        {
            if (DataContext is ViewModels.Popup popup) popup.CancelOperation();
            args.Handled = true;
        }
        private void OnCloseOperation(object sender, RoutedEventArgs args)
        {
            if (DataContext is ViewModels.Popup { CanCloseOperation: true } && this.FindAncestorOfType<LauncherPage>()?.DataContext is ViewModels.LauncherPage page)
                page.CancelPopup();
            args.Handled = true;
        }
    }
}
