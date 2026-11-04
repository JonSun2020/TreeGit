using System.Collections.Generic;

namespace SourceGit.Models
{
    public class Stash
    {
        public string Name { get; set; } = "";
        public string SHA { get; set; } = "";
        public List<string> Parents { get; set; } = [];
        public ulong Time { get; set; } = 0;
        public string Message { get; set; } = "";
        public string Subject => Message.Split('\n', 2)[0].Trim();
        public string DisplayName => $"{(Name.StartsWith("stash@{", System.StringComparison.Ordinal) ? Name[7..].TrimEnd('}') : Name)}: {Subject}";
        public string UntrackedParent => EmptyTreeHash.Guess(SHA);
    }
}
