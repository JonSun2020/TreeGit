using System;
using System.Security.Cryptography;
using System.Text;

using Avalonia.Styling;

namespace SourceGit.Models
{
    public static class TreeGitThemes
    {
        public static ThemeVariant Blue { get; } = new("Blue", ThemeVariant.Light);

        // Keep pre-Blue saved preferences compatible without publishing retired names.
        private static bool IsLegacyBlue(string theme) => theme != null &&
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(theme.ToUpperInvariant()))) ==
            "001F14A355657C41D6CED47567847ACB1A48AC2A5CE975FDACF2B0197C257637";

        public static string Normalize(string theme)
        {
            if (string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase))
                return "Default";
            if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
                return "Dark";
            if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
                return "Light";
            if (string.Equals(theme, "Blue", StringComparison.OrdinalIgnoreCase) || IsLegacyBlue(theme))
                return "Blue";
            return "Light";
        }
    }
}
