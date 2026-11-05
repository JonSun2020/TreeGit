using System.Text.RegularExpressions;

using System.Globalization;

namespace SourceGit.Models
{
    public partial class LFSObject
    {
        [GeneratedRegex(@"^version https://git-lfs.github.com/spec/v1\r?\noid sha256:([0-9a-f]{64})\r?\nsize (\d+)[\r\n]*$")]
        private static partial Regex REG_FORMAT();

        public string Oid { get; set; } = string.Empty;
        public long Size { get; set; } = 0;

        public static LFSObject Parse(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            var match = REG_FORMAT().Match(content);
            if (match.Success && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                return new() { Oid = match.Groups[1].Value, Size = size };

            return null;
        }

        internal static bool IsOid(string value)
        {
            if (value?.Length != 64) return false;
            foreach (var character in value)
                if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
            return true;
        }
    }
}
