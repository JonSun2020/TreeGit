using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class Version
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonPropertyName("assets")]
        public List<ReleaseAsset> Assets { get; set; } = [];

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonIgnore]
        public System.Version CurrentVersion { get; }

        [JsonIgnore]
        public string CurrentVersionStr => "TreeGit " + CurrentVersion.ToString(3);

        [JsonIgnore]
        public bool IsNewVersion => TagName != null && System.Version.TryParse(TagName.StartsWith("treegit-v", StringComparison.Ordinal) ? TagName.Substring(9) : TagName.TrimStart('v'), out var release) && CurrentVersion.CompareTo(release) < 0;

        [JsonIgnore]
        public string ReleaseDateStr => DateTimeFormat.Format(PublishedAt, true);

        public Version()
        {
            CurrentVersion = new System.Version(TreeGitRelease.Version);
        }
    }

    public class AlreadyUpToDate;

    public sealed class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }
        [JsonPropertyName("digest")]
        public string Digest { get; set; }
        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class SelfUpdateFailed
    {
        public string Reason
        {
            get;
            private set;
        }

        public SelfUpdateFailed(Exception e)
        {
            if (e.InnerException is { } inner)
                Reason = inner.Message;
            else
                Reason = e.Message;
        }
    }
}
