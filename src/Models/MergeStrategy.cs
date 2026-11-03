using System.Collections.Generic;

namespace SourceGit.Models
{
    public class MergeStrategy
    {
        private string ResourceKey => "Options.MergeStrategy." + (string.IsNullOrEmpty(Arg) ? "default" : Arg.Replace("--", "").Replace(" ", "_"));
        public string DisplayName => App.Text(ResourceKey + ".Name");
        public string DisplayDescription => App.Text(ResourceKey + ".Desc");

        public string Name { get; internal set; }
        public string Desc { get; internal set; }
        public string Arg { get; internal set; }

        public static List<MergeStrategy> ForMultiple { get; private set; } = [
            new MergeStrategy("Default", "Let Git automatically select a strategy", string.Empty),
            new MergeStrategy("Octopus", "Attempt merging multiple heads", "octopus"),
            new MergeStrategy("Ours", "Record the merge without modifying the tree", "ours"),
        ];

        public MergeStrategy(string n, string d, string a)
        {
            Name = n;
            Desc = d;
            Arg = a;
        }
    }
}
