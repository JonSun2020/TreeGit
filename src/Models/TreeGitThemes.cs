using System;

using Avalonia.Styling;

namespace SourceGit.Models
{
    public static class TreeGitThemes
    {
        public static ThemeVariant SourceTree { get; } = new("Blue", ThemeVariant.Light);

        public static string Normalize(string theme)
        {
            if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
                return "Dark";
            if (string.Equals(theme, "Blue", StringComparison.OrdinalIgnoreCase) || string.Equals(theme, "SourceTree", StringComparison.OrdinalIgnoreCase))
                return "Blue";
            return "Light";
        }
    }
}
