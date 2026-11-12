using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class UpdateCloseChoice : ChromelessWindow
    {
        public UpdateCloseChoice()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        private void OnCancelUpdate(object sender, RoutedEventArgs e) => Close(false);
        private void OnBackground(object sender, RoutedEventArgs e) => Close(true);
    }
}
