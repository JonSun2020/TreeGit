using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class RecentCommitMessages : UserControl
    {
        public RecentCommitMessages() => InitializeComponent();
        public event Action<string> MessageChosen;
        private void OnMessageChosen(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.RecentCommitMessage entry }) MessageChosen?.Invoke(entry.Message);
            e.Handled = true;
        }

        public object Inspect() => new
        {
            loading = (DataContext as ViewModels.RecentCommitMessages)?.IsLoading,
            error = (DataContext as ViewModels.RecentCommitMessages)?.HasError,
            entries = (DataContext as ViewModels.RecentCommitMessages)?.Messages.Select(message => new { text = message.Message.Length > 4096 ? message.Message[..4096] : message.Message, time = message.CommitTime }).ToArray(),
            scrollable = MessageScroll.Extent.Height > MessageScroll.Viewport.Height,
            rows = this.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("commit_message_choice")).Select(button => new
            {
                background = button.Background?.ToString(), foreground = button.Foreground?.ToString(),
                width = button.Bounds.Width, height = button.Bounds.Height,
                textHeight = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Bounds.Height,
            }).ToArray(),
        };
    }
}
