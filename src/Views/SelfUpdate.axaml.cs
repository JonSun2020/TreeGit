using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

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
                _model.CloseRequested -= Close;
                _model.RestartRequested -= OnRestart;
            }
            _model = DataContext as ViewModels.SelfUpdate;
            if (_model != null)
            {
                _model.CloseRequested += Close;
                _model.RestartRequested += OnRestart;
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_model is { CanDismiss: false, Restarting: false }) e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _model?.OnClosed();
            base.OnClosed(e);
        }

        private async void OnAction(object sender, RoutedEventArgs e)
        {
            if (_model != null) await _model.ActAsync();
        }

        private void OnDismiss(object sender, RoutedEventArgs e) => _model?.Dismiss();
        private void GotoDownload(object sender, RoutedEventArgs e) => Native.OS.OpenBrowser(Native.TreeGitUpdater.DownloadPage);

        private void OnRestart()
        {
            Close();
            if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) window.Close();
        }

        private ViewModels.SelfUpdate _model;
    }
}