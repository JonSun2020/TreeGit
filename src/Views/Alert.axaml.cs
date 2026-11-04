using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SourceGit.Views
{
    public partial class Alert : ChromelessWindow
    {
        public Alert()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        public async Task ShowAsync(Window owner, string message, bool isError)
        {
            var title = isError ? App.Text("Launcher.Error") : App.Text("Launcher.Info");
            Title = title;
            TxtTitle.Text = title;
            Message.Text = message;
            AlertIcon.Data = this.FindResource(isError ? "Icons.Error" : "Icons.Info") as Geometry;
            await ShowDialog(owner);
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
