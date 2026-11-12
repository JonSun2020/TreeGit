using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public sealed class CommitParent : ObservableObject
    {
        public string SHA { get; }
        public string ShortSHA
        {
            get => _shortSHA;
            internal set => SetProperty(ref _shortSHA, value);
        }

        public CommitParent(string sha, string shortSHA = null)
        {
            SHA = sha;
            // A complete ID remains safe if the repository cannot be queried.
            _shortSHA = shortSHA ?? sha;
        }

        private string _shortSHA;
    }
}
