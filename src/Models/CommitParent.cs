namespace SourceGit.Models
{
    public sealed class CommitParent
    {
        public string SHA { get; }
        public string ShortSHA { get; }

        public CommitParent(string sha, string shortSHA = null)
        {
            SHA = sha;
            // A complete ID remains safe if the repository cannot be queried.
            ShortSHA = shortSHA ?? sha;
        }
    }
}
