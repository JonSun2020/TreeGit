using System.Collections.Generic;

namespace SourceGit.Models
{
    public class DealWithChangesAfterStashing(string key)
    {
        public string Label => App.Text("Stash.After." + key);
        public string Desc => App.Text("Stash.After." + key + ".Description");
        public static readonly List<DealWithChangesAfterStashing> Supported = [new("Remove"), new("KeepIndex"), new("KeepAll")];
    }
}
