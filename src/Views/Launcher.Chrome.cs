using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class Launcher
    {
        public static readonly Avalonia.StyledProperty<bool> IsRepositorySettingsOpenProperty = Avalonia.AvaloniaProperty.Register<Launcher, bool>(nameof(IsRepositorySettingsOpen));
        public bool IsRepositorySettingsOpen { get => GetValue(IsRepositorySettingsOpenProperty); set => SetValue(IsRepositorySettingsOpenProperty, value); }
        public async System.Threading.Tasks.Task OpenRepositorySettingsAsync(ViewModels.Repository repo)
        {
            IsRepositorySettingsOpen = true;
            try
            {
                var settings = new RepositorySettings(repo);
                settings.FitToOwner(this);
                await settings.ShowDialog(this);
            }
            finally { IsRepositorySettingsOpen = false; }
        }

        private void InitializeTitleChrome()
        {
            TitleChrome.AddHandler(PointerPressedEvent, OnTitleChromePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        }

        private void OnTitleChromePointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            for (var control = e.Source as Control; control != null && control != TitleChrome; control = control.GetVisualParent() as Control)
                if (control is Button or MenuItem or ListBoxItem or ScrollBar or TextBox)
                    return;
            if (e.ClickCount >= 2)
            {
                ToggleMaximizeRestore();
                e.Handled = true;
            }
            else
                BeginMoveWindow(sender, e);
        }

        private void OnTreeGitExtraAction(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string action } && TreeGitRepository is { } repo)
                this.GetVisualDescendants().OfType<RepositoryToolbar>().FirstOrDefault(v => v.DataContext == repo)?.ExecuteMenuAction(action, TitleChrome);
        }

        public void ToggleMaximizeRestore()
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
