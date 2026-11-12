using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class CommitBranchHeader : UserControl
    {
        public CommitBranchHeader()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => ObserveContext();
            AttachedToVisualTree += (_, _) => ObserveContext();
            DetachedFromVisualTree += (_, _) => { CloseBranches(); if (_observed != null) _observed.PropertyChanged -= OnContextChanged; _observed = null; };
        }

        public Control BranchesSurface => _flyout?.IsOpen == true ? _flyout.Content as Control : null;
        public object BranchPopupState => _flyout?.IsOpen == true && _flyout.Content is SearchBranches { DataContext: ViewModels.SearchBranches context }
            ? new { sha = context.Commit.SHA, total = context.Commit.SearchBranches.Count, rows = context.Rows.Where(r => r.IsBranch).Take(128).Select(r => r.Branch.FullName).ToArray() } : null;

        public bool ShowBranches()
        {
            CloseBranches();
            if (DataContext is not ViewModels.CommitDetail vm || vm.BranchDetails is not { HasMore: true } details || details.Commit.SHA != vm.Commit?.SHA) return false;
            var zoom = ViewModels.Preferences.Instance.Zoom;
            var size = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1024, 768);
            var context = new ViewModels.SearchBranches(details.Commit, true);
            var content = new SearchBranches { DataContext = context, Width = Math.Min(380, Math.Max(220, size.Width / zoom - 48)), MaxHeight = Math.Min(420, Math.Max(140, size.Height / zoom - 80)) };
            var flyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedLeft };
            _flyout = flyout;
            context.BranchChosen += async branch => { await this.CopyTextAsync(branch.FriendlyName); flyout.Hide(); };
            flyout.Closed += (_, _) => { if (_flyout == flyout) _flyout = null; };
            flyout.ShowAt(MoreCommitBranches);
            return true;
        }

        private void OnShowContainingBranches(object sender, RoutedEventArgs e) { ShowBranches(); e.Handled = true; }
        private void CloseBranches() => _flyout?.Hide();
        private void OnContextChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(ViewModels.CommitDetail.BranchDetails) or nameof(ViewModels.CommitDetail.Commit))) return;
            if (_flyout?.IsOpen == true && _flyout.Content is SearchBranches { DataContext: ViewModels.SearchBranches previous } content &&
                sender is ViewModels.CommitDetail vm && vm.Commit?.SHA == previous.Commit.SHA && vm.BranchDetails is { HasMore: true } details)
            {
                var context = new ViewModels.SearchBranches(details.Commit, true) { Filter = previous.Filter };
                var flyout = _flyout;
                context.BranchChosen += async branch => { await this.CopyTextAsync(branch.FriendlyName); flyout.Hide(); };
                content.DataContext = context;
                return;
            }
            CloseBranches();
        }
        private void ObserveContext()
        {
            CloseBranches();
            if (_observed != null) _observed.PropertyChanged -= OnContextChanged;
            _observed = DataContext as ViewModels.CommitDetail;
            if (_observed != null) _observed.PropertyChanged += OnContextChanged;
        }

        private ViewModels.CommitDetail _observed;
        private Flyout _flyout;
    }
}
