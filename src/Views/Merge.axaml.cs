using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class Merge : UserControl
    {
        public Merge()
        {
            InitializeComponent();
        }

        private async void OnConfigureAuthor(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Merge model && TopLevel.GetTopLevel(this) is Launcher launcher)
                await launcher.OpenRepositorySettingsAsync(model.Repository);
        }
    }
}
