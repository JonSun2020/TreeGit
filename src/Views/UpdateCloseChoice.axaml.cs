using System;
using System.ComponentModel;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class UpdateCloseChoice : ChromelessWindow
    {
        public UpdateCloseChoice()
        {
            CloseOnESC = true;
            InitializeComponent();
            ViewModels.Preferences.Instance.PropertyChanged += OnPreferencesChanged;
        }

        protected override void OnOpened(EventArgs e) { base.OnOpened(e); FitForZoom(); }
        protected override void OnClosed(EventArgs e)
        {
            ViewModels.Preferences.Instance.PropertyChanged -= OnPreferencesChanged;
            base.OnClosed(e);
        }

        private void OnPreferencesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ViewModels.Preferences.Zoom)) FitForZoom();
        }

        private void FitForZoom()
        {
            if (!IsVisible) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            var zoom = ViewModels.Preferences.Instance.Zoom;
            var width = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
            var height = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 64);
            MinWidth = Math.Min(300 * zoom + 24, width);
            MinHeight = Math.Min(165 * zoom + 24, height);
            Width = Math.Clamp(440 * zoom, MinWidth, width);
            Height = Math.Clamp(205 * zoom, MinHeight, height);
        }

        private void OnCancelUpdate(object sender, RoutedEventArgs e) => Close(false);
        private void OnBackground(object sender, RoutedEventArgs e) => Close(true);
    }
}
