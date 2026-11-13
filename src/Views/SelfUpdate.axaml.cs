using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class SelfUpdate : ChromelessWindow
    {
        public SelfUpdate()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_model != null)
            {
                _model.CloseRequested -= CloseExplicitly;
                _model.RestartRequested -= OnRestart;
                _model.BackgroundRequested -= MoveToBackground;
                _model.ForegroundRequested -= OnForeground;
                _model.PropertyChanged -= OnUpdateStateChanged;
            }
            _model = DataContext as ViewModels.SelfUpdate;
            if (_model != null)
            {
                _model.CloseRequested += CloseExplicitly;
                _model.RestartRequested += OnRestart;
                _model.BackgroundRequested += MoveToBackground;
                _model.ForegroundRequested += OnForeground;
                _model.PropertyChanged += OnUpdateStateChanged;
            }
        }

        public void Present(Window owner)
        {
            if (owner.PlatformImpl == null) return;
            if (_presentationOwner != owner)
            {
                if (_presentationOwner != null) _presentationOwner.Closed -= OnOwnerClosed;
                _presentationOwner = owner;
                owner.Closed += OnOwnerClosed;
            }
            _model?.EnterForeground();
            if (!owner.IsVisible) owner.Show();
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            if (!IsVisible) _ = ShowDialog(owner);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            {
                if (_model is { CanDismiss: false, Restarting: false }) e.Cancel = true;
                else if (!_allowClose && _model is { CanBackground: true })
                {
                    e.Cancel = true;
                    if (!_askingClose)
                    {
                        _askingClose = true;
                        Dispatcher.UIThread.Post(AskCloseChoice);
                    }
                }
            }
            base.OnClosing(e);
        }

        private async void AskCloseChoice()
        {
            try
            {
                if (!IsVisible || _model is not { CanBackground: true }) return;
                _closeChoice = new UpdateCloseChoice();
                var background = await _closeChoice.ShowDialog<bool?>(this);
                if (_model is not { CanBackground: true } || background == null) return;
                if (background.Value) await _model.RunInBackgroundAsync();
                else { _allowClose = true; Close(); }
            }
            finally { _closeChoice = null; _askingClose = false; }
        }

        private void OnUpdateStateChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) && _closeChoice is { IsVisible: true } && _model?.State is "ready" or "failed" or "cancelled")
                _closeChoice.Close();
        }

        private void MoveToBackground()
        {
            // A direct MCP background action can arrive while the X choice is open.
            // Close that modal rather than leaving a hidden, unresolved choice alive.
            if (_closeChoice is { IsVisible: true }) _closeChoice.Close();
            Hide();
        }

        private void OnForeground()
        {
            if (_presentationOwner is { PlatformImpl: not null } owner)
            {
                if (!owner.IsVisible) owner.Show();
                Present(owner);
            }
        }

        private void OnOwnerClosed(object sender, EventArgs e) { _allowClose = true; Close(); }

        protected override void OnClosed(EventArgs e)
        {
            _model?.OnClosed();
            if (_presentationOwner != null) _presentationOwner.Closed -= OnOwnerClosed;
            if (_model != null)
            {
                _model.CloseRequested -= CloseExplicitly;
                _model.RestartRequested -= OnRestart;
                _model.BackgroundRequested -= MoveToBackground;
                _model.ForegroundRequested -= OnForeground;
                _model.PropertyChanged -= OnUpdateStateChanged;
            }
            base.OnClosed(e);
        }

        private async void OnAction(object sender, RoutedEventArgs e)
        {
            if (_model != null) await _model.ActAsync();
        }

        private void OnDismiss(object sender, RoutedEventArgs e) => _model?.Dismiss();
        private void CloseExplicitly() { _allowClose = true; Close(); }
        private async void OnBackground(object sender, RoutedEventArgs e) { if (_model != null) await _model.RunInBackgroundAsync(); }
        private void GotoDownload(object sender, RoutedEventArgs e) => Native.OS.OpenBrowser(Native.TreeGitUpdater.DownloadPage);

        private void OnRestart()
        {
            Close();
            if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) window.Close();
        }

        private ViewModels.SelfUpdate _model;
        private Window _presentationOwner;
        private UpdateCloseChoice _closeChoice;
        private bool _allowClose;
        private bool _askingClose;
    }
}
