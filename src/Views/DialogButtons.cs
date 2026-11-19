using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    internal static class DialogButtons
    {
        private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        private static readonly IDataTemplate CaptionTemplate = new FuncDataTemplate<object>((value, _) =>
        {
            var caption = value?.ToString() ?? string.Empty;
            foreach (var (preferred, alternatives) in new[] { ("Sure", new[] { "Sure", "TreeGit.Ok" }), ("Cancel", new[] { "Cancel", "TreeGit.Cancel" }), ("Close", new[] { "Close", "TreeGit.Close" }) })
                if (alternatives.Any(key => Compact(App.Text(key)) == Compact(caption))) { caption = App.Text(preferred); break; }
            return new TextBlock { Classes = { "dialog_caption" }, Text = caption, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        });

        public static void Apply(Control root)
        {
            var roles = new[]
            {
                ("dialog_confirm", "Sure", new[] { "Sure", "TreeGit.Ok" }),
                ("dialog_cancel", "Cancel", new[] { "Cancel", "TreeGit.Cancel" }),
                ("dialog_close", "Close", new[] { "Close", "TreeGit.Close" }),
                ("dialog_save", "TreeGit.Save", new[] { "Save", "TreeGit.Save" }),
            };
            foreach (var button in root.GetVisualDescendants().OfType<Button>().Concat(root.GetLogicalDescendants().OfType<Button>()).Distinct())
            {
                if (button.Classes.Contains("icon_button") || button.Classes.Contains("caption_button")) continue;
                var text = button.Content as string;
                var role = roles.FirstOrDefault(candidate => button.Classes.Contains(candidate.Item1) ||
                    text != null && candidate.Item3.Any(key => Compact(App.Text(key)) == Compact(text)));
                if (role.Item1 == null && !button.Classes.Contains("dialog_action")) continue;
                if (role.Item1 != null && !button.Classes.Contains(role.Item1))
                {
                    button.Classes.Add(role.Item1);
                }
                // Preserve the original Content binding: update/auth dialogs
                // change actions as their state changes. Format presentation only.
                if (button.Content == null || button.Content is string) button.ContentTemplate = CaptionTemplate;
                if (!button.Classes.Contains("flat")) button.Classes.Add("flat");
                if (!button.Classes.Contains("dialog_action")) button.Classes.Add("dialog_action");
                var primaryClose = role.Item1 == "dialog_close" && button.Classes.Contains("primary");
                if (primaryClose && !button.Classes.Contains("primary_close")) button.Classes.Add("primary_close");
                if (role.Item1 is "dialog_confirm" or "dialog_save" || primaryClose) { if (!button.Classes.Contains("primary")) button.Classes.Add("primary"); }
                else if (role.Item1 != null) button.Classes.Remove("primary");
                var compact = button.Classes.Contains("compact_dialog_action");
                button.MinWidth = primaryClose ? 80 : compact ? 60 : 120;
                button.Width = double.NaN;
                button.MaxWidth = double.PositiveInfinity;
                button.Height = button.MinHeight = 28;
                button.Padding = new Thickness(compact ? 4 : 10, 2);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
            }
        }
    }
}
