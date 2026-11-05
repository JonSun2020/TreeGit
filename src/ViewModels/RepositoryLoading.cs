using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RepositoryLoading : ObservableObject
    {
        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        public bool Failed { get; set; }

        private string _message = App.Text("Repository.Loading");
    }
}
