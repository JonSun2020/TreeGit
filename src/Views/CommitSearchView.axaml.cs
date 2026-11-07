using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class CommitSearchView : UserControl
    {
        public CommitSearchView()
        {
            InitializeComponent();
            DetachedFromVisualTree += (_, _) => CloseContainingBranches();
        }

        public object ContainingBranchesState => _branchFlyout?.IsOpen == true && _branchPopup?.DataContext is ViewModels.SearchBranches context
            ? new { sha = context.Commit.SHA, filter = context.Filter, total = context.Commit.SearchBranches.Count,
                bounds = PopupBounds(),
                background = _branchPopup.Background?.ToString(),
                foreground = _branchPopup.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "CoverageTitle")?.Foreground?.ToString(),
                branchForeground = _branchPopup.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "BranchName")?.Foreground?.ToString(),
                rows = context.Rows.Select(r => new { reference = r.Branch.FullName, name = r.Branch.FriendlyName, local = r.Branch.IsLocal, current = r.Branch.IsCurrent, group = r.Group }).ToArray() }
            : null;

        public Control ContainingBranchesSurface => _branchFlyout?.IsOpen == true ? _branchPopup : null;

        private object PopupBounds()
        {
            if (_branchPopup.GetVisualRoot() == null || _branchPopup.Bounds.Height < 1) return null;
            var start = _branchPopup.PointToScreen(default);
            var end = _branchPopup.PointToScreen(new Point(_branchPopup.Bounds.Width, _branchPopup.Bounds.Height));
            var window = TopLevel.GetTopLevel(this) as Window;
            var area = (window?.Screens.ScreenFromWindow(window) ?? window?.Screens.Primary)?.WorkingArea;
            return new { x = start.X, y = start.Y, right = end.X, bottom = end.Y,
                screenX = area?.X, screenY = area?.Y, screenRight = area?.Right, screenBottom = area?.Bottom };
        }

        public void ShowContainingBranches(Models.Commit commit)
        {
            SearchResults.ScrollIntoView(commit, null);
            SearchResults.UpdateLayout();
            var button = SearchResults.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SearchBranchButton" && b.DataContext == commit);
            if (button == null) throw new InvalidOperationException("该搜索结果当前不可见，请先滚动到对应行。");
            ShowContainingBranches(button, commit);
        }

        public void FilterContainingBranches(string filter)
        {
            if (_branchPopup?.DataContext is not ViewModels.SearchBranches context || _branchFlyout?.IsOpen != true)
                throw new InvalidOperationException("请先打开包含分支列表。");
            context.Filter = filter;
        }

        public void CloseContainingBranches() => _branchFlyout?.Hide();

        private void ShowContainingBranches(Button button, Models.Commit commit)
        {
            CloseContainingBranches();
            if (commit.SearchBranches == null || DataContext is not ViewModels.SearchCommitContext search) return;
            search.Selected = commit;
            var context = new ViewModels.SearchBranches(commit);
            var zoom = ViewModels.Preferences.Instance.Zoom;
            var size = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1024, 768);
            _branchPopup = new SearchBranches { DataContext = context, Width = Math.Min(380, Math.Max(220, size.Width / zoom - 48)), MaxHeight = Math.Min(420, Math.Max(140, size.Height / zoom - 80)) };
            var flyout = new Flyout { Content = _branchPopup, Placement = PlacementMode.BottomEdgeAlignedLeft };
            _branchFlyout = flyout;
            void SearchChanged(object _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(search.Results) || (e.PropertyName == nameof(search.Selected) && search.Selected != commit)) flyout.Hide();
            }
            search.PropertyChanged += SearchChanged;
            flyout.Closed += (_, _) =>
            {
                search.PropertyChanged -= SearchChanged;
                if (_branchFlyout == flyout) { _branchFlyout = null; _branchPopup = null; }
            };
            context.BranchChosen += branch =>
            {
                flyout.Hide();
                if (search.Results?.Contains(commit) == true) search.OpenInHistory(commit, branch.FullName);
            };
            var origin = button.TranslatePoint(default, SearchResults) ?? default;
            BranchPopupAnchor.Margin = new Thickness(origin.X, origin.Y, 0, 0);
            BranchPopupAnchor.Width = button.Bounds.Width;
            BranchPopupAnchor.Height = button.Bounds.Height;
            BranchPopupAnchor.UpdateLayout();
            flyout.ShowAt(BranchPopupAnchor);
        }

        private void OnContainingBranchesClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.Commit commit } button) ShowContainingBranches(button, commit);
            e.Handled = true;
        }

        private void OnBranchCellDoubleTapped(object sender, TappedEventArgs e) => e.Handled = true;

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
            if ((change.Property == IsVisibleProperty && !IsVisible) || change.Property == DataContextProperty) CloseContainingBranches();
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

        private Flyout _branchFlyout;
        private SearchBranches _branchPopup;
    }
}
