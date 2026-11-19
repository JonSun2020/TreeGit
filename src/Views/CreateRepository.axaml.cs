using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class CreateRepository : UserControl
    {
        public CreateRepository() => InitializeComponent();

        private async void OnChooseParent(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.CreateRepository vm || TopLevel.GetTopLevel(this) is not { } owner) return;
            try
            {
                var options = new FolderPickerOpenOptions { AllowMultiple = false };
                if (Directory.Exists(vm.ParentFolder)) options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(vm.ParentFolder);
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
                if (folders.Count == 1 && folders[0].Path is { IsAbsoluteUri: true } path && path.IsFile) vm.ParentFolder = path.LocalPath;
            }
            catch { Models.Notification.Send(null, App.Text("TreeGit.CreateRepository.FolderPickerFailed"), true); }
            e.Handled = true;
        }

        private async void OnVerifyAccount(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.CreateRepository vm) return;
            await vm.VerifyAccount();
            e.Handled = true;
        }

        private void OnToggleToken(object sender, RoutedEventArgs e) => CreateRepositoryToken.RevealPassword = ShowGitHubToken.IsChecked == true;
        private void OnTokenHelp(object sender, RoutedEventArgs e) => Native.OS.OpenBrowser("https://github.com/settings/tokens/new");
        private void OnConnectExisting(object sender, RoutedEventArgs e) => (DataContext as ViewModels.CreateRepository)?.ConnectExistingRemote();
    }
}
