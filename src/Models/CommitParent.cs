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
            // Show the normal width immediately while Git checks uniqueness.
            // Navigation and complete-SHA copying always use SHA, never this prefix.
            _shortSHA = shortSHA ?? sha[..System.Math.Min(10, sha.Length)];
        }

        private string _shortSHA;
    }
}
