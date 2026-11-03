using System.Collections.Generic;

namespace SourceGit.Models
{
    public class CRLFMode(string name, string value, string desc)
    {
        private string ResourceKey => "Options.CRLFMode." + Value;
        public string DisplayName => App.Text(ResourceKey + ".Name");
        public string DisplayDescription => App.Text(ResourceKey + ".Desc");

        public string Name { get; set; } = name;
        public string Value { get; set; } = value;
        public string Desc { get; set; } = desc;

        public static readonly List<CRLFMode> Supported = new List<CRLFMode>() {
            new CRLFMode("TRUE", "true", "Commit as LF, checkout as CRLF"),
            new CRLFMode("INPUT", "input", "Only convert for commit"),
            new CRLFMode("FALSE", "false", "Do NOT convert"),
        };
    }
}
