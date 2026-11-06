using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class CommitSearchView : UserControl
    {
        public CommitSearchView()
        {
            InitializeComponent();
        }

        private void OnResultDoubleTapped(object sender, TappedEventArgs e)
        {
            var row = e.Source as DataGridRow ?? (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
            if (row?.DataContext is Models.Commit commit && DataContext is ViewModels.SearchCommitContext context)
            {
                context.OpenInHistory(commit);
                e.Handled = true;
            }
        }

        private void OnResultKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is ViewModels.SearchCommitContext { Selected: { } commit } context)
            {
                context.OpenInHistory(commit);
                e.Handled = true;
            }
        }

        private void OnResultsContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is ViewModels.SearchCommitContext { Selected: { } commit })
            {
                this.FindAncestorOfType<Histories>()?.OpenSearchCommitMenu(SearchResults, commit);
                e.Handled = true;
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsVisibleProperty && IsVisible)
                Dispatcher.UIThread.Post(() => TxtSearchCommitsBox.Focus(NavigationMethod.Directional), DispatcherPriority.Background);
        }

        private void OnSearchBoxKeyDown(object _, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.SearchCommitContext ctx)
                return;

            if (e.Key == Key.Enter)
            {
                ctx.StartSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (ctx.Suggestions is { Count: > 0 })
                {
                    SearchSuggestionBox.Focus(NavigationMethod.Tab);
                    SearchSuggestionBox.SelectedIndex = 0;
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ctx.ClearSuggestions();
                e.Handled = true;
            }
        }

        private void OnSearchSuggestionBoxKeyDown(object _, KeyEventArgs e)
        {
            if (DataContext is not ViewModels.SearchCommitContext ctx)
                return;

            if (e.Key == Key.Escape)
            {
                ctx.ClearSuggestions();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var selected = SearchSuggestionBox.SelectedItem;
                if (selected is string content)
                {
                    ctx.Filter = content;
                    TxtSearchCommitsBox.CaretIndex = content.Length;
                }
                else if (selected is Models.User user)
                {
                    var apply = user.ToString().EscapeForBRE();
                    ctx.Filter = apply;
                    TxtSearchCommitsBox.CaretIndex = apply.Length;
                }

                ctx.StartSearch();
                e.Handled = true;
            }
        }

        private void OnSearchSuggestionTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is not ViewModels.SearchCommitContext ctx)
                return;

            var selected = (sender as Control)?.DataContext;
            if (selected is string content)
            {
                ctx.Filter = content;
                TxtSearchCommitsBox.CaretIndex = content.Length;
            }
            else if (selected is Models.User user)
            {
                var apply = user.ToString().EscapeForBRE();
                ctx.Filter = apply;
                TxtSearchCommitsBox.CaretIndex = apply.Length;
            }

            ctx.StartSearch();
            e.Handled = true;
        }
    }
}
