using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Media;

namespace SourceGit.Models
{
    public sealed record FontOption(string Family, string Name)
    {
        public static IReadOnlyList<FontOption> Create(string current, string defaultName)
        {
            var options = new List<FontOption> { new(string.Empty, defaultName) };
            options.AddRange(_installed.Value.Select(name => new FontOption(name, name)));
            if (!string.IsNullOrEmpty(current) && !options.Any(option => option.Family.Equals(current, StringComparison.OrdinalIgnoreCase)))
                options.Insert(1, new(current, current));
            return options;
        }

        // Only enumerated when the preferences window requests the list.
        private static readonly Lazy<string[]> _installed = new(() => FontManager.Current.SystemFonts
            .Select(font => font.Name).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
    }
}
