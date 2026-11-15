using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;

namespace SourceGit.Views
{
    // AvaloniaEdit supplies search counts in its own resource language. Keep
    // the existing search behavior while presenting those captions in TreeGit's
    // selected language, including a live language preview.
    public sealed class LocalizedSearchMessage : TextBlock
    {
        private string _source;
        private bool _refreshing;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty && !_refreshing)
            {
                _source = Text;
                Refresh();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            App.LocaleChanged += Refresh;
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            App.LocaleChanged -= Refresh;
            base.OnDetachedFromVisualTree(e);
        }

        private void Refresh()
        {
            _refreshing = true;
            try { Text = Translate(_source); }
            finally { _refreshing = false; }
        }

        internal static string Translate(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            if (source == SR.SearchNoMatchesFoundText) return App.Text("TreeGit.Search.NoMatches");
            if (source == SR.Search1Match) return App.Text("TreeGit.Search.OneMatch");
            if (source.StartsWith(SR.SearchErrorText, StringComparison.Ordinal))
                return App.Text("TreeGit.Search.Error", source[SR.SearchErrorText.Length..]);
            if (source.Length > 256) return source;
            foreach (var (format, key, arguments) in new[]
            {
                (SR.SearchXMatches, "TreeGit.Search.Matches", 1),
                (SR.SearchXOfY, "TreeGit.Search.Position", 2),
            })
            {
                var pattern = Regex.Escape(format).Replace("\\{0}", "(?<first>[0-9]+)", StringComparison.Ordinal)
                    .Replace("\\{1}", "(?<second>[0-9]+)", StringComparison.Ordinal);
                var match = Regex.Match(source, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                if (!match.Success || !int.TryParse(match.Groups["first"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first)) continue;
                if (arguments == 1) return App.Text(key, first);
                if (int.TryParse(match.Groups["second"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var second)) return App.Text(key, first, second);
            }
            return source;
        }
    }
}
