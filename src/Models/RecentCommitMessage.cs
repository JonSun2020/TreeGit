namespace SourceGit.Models
{
    public sealed record RecentCommitMessage(string Message, long CommitTime)
    {
        public string DisplayMessage => Message.Length > 16384 ? Message[..16384] + "…" : Message;
    }
}
