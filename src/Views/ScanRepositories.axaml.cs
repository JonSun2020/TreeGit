using System.IO;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class ScanRepositories : UserControl
    {
        public ScanRepositories()
        {
            InitializeComponent();
        }

        private async void OnChooseRoot(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.ScanRepositories vm || TopLevel.GetTopLevel(this) is not { } owner)
                return;

            e.Handled = true;
            try
            {
                var picker = new FolderPickerOpenOptions { AllowMultiple = false, Title = App.Text("ScanRepositories") };
                if (Directory.Exists(vm.RootPath))
                    picker.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(vm.RootPath);

                var folders = await owner.StorageProvider.OpenFolderPickerAsync(picker);
                if (folders.Count == 1 && folders[0].Path is { IsAbsoluteUri: true, IsFile: true } path)
                    vm.RootPath = path.LocalPath;
            }
            catch
            {
                Models.Notification.Send(null, App.Text("TreeGit.CreateRepository.FolderPickerFailed"), true);
            }
        }
    }
}
