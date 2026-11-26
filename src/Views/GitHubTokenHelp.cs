using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace SourceGit.Views
{
    public sealed class GitHubTokenHelp : StackPanel
    {
        public GitHubTokenHelp()
        {
            Spacing = 4;
            Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Credentials.GitHubTokenHelp"),
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Brush.FG2"),
            });
            var link = new Button
            {
                Classes = { "icon_button" },
                Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 1.5,
                    UseLayoutRounding = false,
                    Children =
                    {
                        new TextBlock
                        {
                            [!TextBlock.TextProperty] = App.TextBinding("TreeGit.CreateRepository.TokenHelp"),
                            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Brush.ExternalLink"),
                        },
                        new Border { Height = .5, UseLayoutRounding = false, [!Border.BackgroundProperty] = new DynamicResourceExtension("Brush.ExternalLink") },
                    },
                },
            };
            link.Click += (_, _) => Native.OS.OpenBrowser("https://github.com/settings/personal-access-tokens/new");
            Children.Add(link);
        }
    }
}
