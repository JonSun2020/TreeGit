using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class Repository : UserControl
    {
        public static readonly DirectProperty<Repository, bool> IsCommitSearchPanelVisibleProperty =
            AvaloniaProperty.RegisterDirect<Repository, bool>(
                nameof(IsCommitSearchPanelVisible),
                static o => o.IsCommitSearchPanelVisible,
                static (o, v) => o.IsCommitSearchPanelVisible = v);

        public bool IsCommitSearchPanelVisible
        {
            get => _isCommitSearchPanelVisible;
            set
            {
                if (SetAndRaise(IsCommitSearchPanelVisibleProperty, ref _isCommitSearchPanelVisible, value))
                    CalculateSidebarWidth();
            }
        }

        public static readonly DirectProperty<Repository, bool> IsNormalSidebarVisibleProperty =
            AvaloniaProperty.RegisterDirect<Repository, bool>(
                nameof(IsNormalSidebarVisible),
                static o => o.IsNormalSidebarVisible,
                static (o, v) => o.IsNormalSidebarVisible = v);

        public bool IsNormalSidebarVisible
        {
            get => _isNormalSidebarVisible;
            set
            {
                if (SetAndRaise(IsNormalSidebarVisibleProperty, ref _isNormalSidebarVisible, value))
                {
                    CalculateSidebarWidth();
                    if (value)
                        UpdateLeftSidebarLayout();
                }
            }
        }

        public static readonly DirectProperty<Repository, double> SidebarMinWidthProperty =
            AvaloniaProperty.RegisterDirect<Repository, double>(
                nameof(SidebarMinWidth),
                static o => o.SidebarMinWidth,
                static (o, v) => o.SidebarMinWidth = v);

        public double SidebarMinWidth
        {
            get => _sidebarMinWidth;
            set => SetAndRaise(SidebarMinWidthProperty, ref _sidebarMinWidth, value);
        }

        public static readonly DirectProperty<Repository, double> SidebarMaxWidthProperty =
            AvaloniaProperty.RegisterDirect<Repository, double>(nameof(SidebarMaxWidth),
                static o => o.SidebarMaxWidth);
        public double SidebarMaxWidth => _sidebarMaxWidth;

        public static readonly DirectProperty<Repository, GridLength> SidebarWidthProperty =
            AvaloniaProperty.RegisterDirect<Repository, GridLength>(
                nameof(SidebarWidth),
                static o => o.SidebarWidth,
                static (o, v) => o.SidebarWidth = v);

        public GridLength SidebarWidth
        {
            get => _sidebarWidth;
            set
            {
                if (SetAndRaise(SidebarWidthProperty, ref _sidebarWidth, value))
                {
                    if (!_applyingSidebarConstraint)
                    {
                        var layout = ViewModels.Preferences.Instance.Layout;
                        if (_isCommitSearchPanelVisible)
                            layout.RepositorySearchCommitWidth = value.Value;
                        else if (_isNormalSidebarVisible)
                            layout.RepositorySidebarWidth = value.Value;
                    }
                }
            }
        }

        public Repository()
        {
            _sidebarWidth = new GridLength(ViewModels.Preferences.Instance.Layout.RepositorySidebarWidth);
            InitializeComponent();
            SizeChanged += (_, _) => ConstrainSidebar();
        }

        private void ConstrainSidebar()
        {
            if (Bounds.Width <= 0) return;
            SetAndRaise(SidebarMaxWidthProperty, ref _sidebarMaxWidth, Math.Max(49, Bounds.Width - 321));
            SidebarMinWidth = _isNormalSidebarVisible || _isCommitSearchPanelVisible ? Math.Min(200, _sidebarMaxWidth) : 49;
            var layout = ViewModels.Preferences.Instance.Layout;
            var desired = _isCommitSearchPanelVisible ? layout.RepositorySearchCommitWidth : _isNormalSidebarVisible ? layout.RepositorySidebarWidth : 49;
            if (!double.IsFinite(desired)) desired = SidebarMinWidth;
            _applyingSidebarConstraint = true;
            try { SidebarWidth = new GridLength(Math.Clamp(desired, SidebarMinWidth, _sidebarMaxWidth)); }
            finally { _applyingSidebarConstraint = false; }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            BindHistoryBranchSelection();
            UpdateLeftSidebarLayout();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            BindHistoryBranchSelection();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            if (_historySelectionSource != null)
            {
                _historySelectionSource.HistoryBranchSelectionRequested -= OnHistoryBranchSelection;
                _historySelectionSource.PropertyChanged -= OnNavigationPropertyChanged;
            }
            _historySelectionSource = null;
            base.OnUnloaded(e);
        }

        private void BindHistoryBranchSelection()
        {
            if (_historySelectionSource != null)
            {
                _historySelectionSource.HistoryBranchSelectionRequested -= OnHistoryBranchSelection;
                _historySelectionSource.PropertyChanged -= OnNavigationPropertyChanged;
            }
            _historySelectionSource = IsLoaded ? DataContext as ViewModels.Repository : null;
            if (_historySelectionSource != null)
            {
                _historySelectionSource.HistoryBranchSelectionRequested += OnHistoryBranchSelection;
                _historySelectionSource.PropertyChanged += OnNavigationPropertyChanged;
            }
        }

        private void OnNavigationPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.Repository.SelectedViewIndex) or nameof(ViewModels.Repository.IsFileStatusTabSelected)
                or nameof(ViewModels.Repository.IsHistoryTabSelected) or nameof(ViewModels.Repository.IsSearchTabSelected))
                PageViewport.Offset = default;
        }

        private void OnHistoryBranchSelection(Models.Branch branch)
        {
            LocalBranchTree.UnselectAll();
            RemoteBranchTree.UnselectAll();
            TagsList.UnselectAll();
            if (branch == null || DataContext is not ViewModels.Repository repo) return;
            if (branch.IsLocal)
            {
                repo.IsLocalBranchGroupExpanded = true;
                LocalBranchTree.Select(branch, false);
            }
            else
            {
                repo.IsRemoteGroupExpanded = true;
                RemoteBranchTree.Select(branch, false);
            }
        }

        private ViewModels.Repository _historySelectionSource;

        private void OnShowWorkingCopyOverview(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository { IsBare: false } repo)
            {
                repo.ShowWorkingCopyOverview();
            }
        }

        private void OnShowHistory(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                repo.ShowHistoryPage();
            }
        }

        private void OnShowSearch(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                repo.SelectedViewIndex = 0;
                repo.Histories.IsSearchingCommits = true;
            }
        }

        private void OnShowStashes(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository { IsBare: false } repo) repo.SelectedViewIndex = 2;
        }

        private void OnSidebarStashSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo && repo.StashesPage.SelectedStash != null)
                repo.SelectedViewIndex = 2;
            e.Handled = true;
        }

        private void OnSidebarStashTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control { DataContext: Models.Stash stash })
            {
                repo.StashesPage.SelectedStash = stash;
                repo.SelectedViewIndex = 2;
            }
            e.Handled = true;
        }

        private void OnSidebarStashDoubleTapped(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control { DataContext: Models.Stash stash })
                repo.StashesPage.Apply(stash);
            e.Handled = true;
        }

        private void OnSidebarStashContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control { DataContext: Models.Stash stash } control)
                StashesPage.OpenStashContextMenu(control, repo.StashesPage, stash);
            e.Handled = true;
        }

        private async void OnSidebarStashKeyDown(object sender, KeyEventArgs e)
        {
            if (DataContext is ViewModels.Repository { StashesPage.SelectedStash: { } stash } repo)
            {
                if (e.Key is Key.Delete or Key.Back) { repo.StashesPage.Drop(stash); e.Handled = true; }
                else if (e.Key == Key.C && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
                { await this.CopyTextAsync(stash.Message); e.Handled = true; }
            }
        }

        private async void OnOpenConfigure(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await ((Launcher)TopLevel.GetTopLevel(this)).OpenRepositorySettingsAsync(repo);
                e.Handled = true;
            }
        }

        private async void OnSkipInProgress(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
                await repo.SkipMergeAsync();

            e.Handled = true;
        }

        private void OnResolveInProgress(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
                repo.SelectedViewIndex = 1;

            e.Handled = true;
        }

        private async void OnAbortInProgress(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
                await repo.AbortMergeAsync();

            e.Handled = true;
        }

        private void OnToggleSidebarFilter(object _, RoutedEventArgs e)
        {
            FilterBox.Focus();
            e.Handled = true;
        }

        private void OnLocalBranchTreeSelectionChanged(object _1, RoutedEventArgs _2)
        {
            RemoteBranchTree.UnselectAll();
            TagsList.UnselectAll();
        }

        private void OnRemoteBranchTreeSelectionChanged(object _1, RoutedEventArgs _2)
        {
            LocalBranchTree.UnselectAll();
            TagsList.UnselectAll();
        }

        private void OnTagsSelectionChanged(object _1, RoutedEventArgs _2)
        {
            LocalBranchTree.UnselectAll();
            RemoteBranchTree.UnselectAll();
        }

        private void OnWorktreeContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is Control { DataContext: ViewModels.Worktree worktree } ctrl && DataContext is ViewModels.Repository repo)
            {
                var menu = new ContextMenu();

                var switchTo = new MenuItem();
                switchTo.Header = App.Text("Worktree.Open");
                switchTo.Icon = this.CreateMenuIcon("Icons.Folder.Open");
                switchTo.Click += (_, ev) =>
                {
                    repo.OpenWorktree(worktree);
                    ev.Handled = true;
                };
                menu.Items.Add(switchTo);
                menu.Items.Add(new MenuItem() { Header = "-" });

                if (worktree.IsLocked)
                {
                    var unlock = new MenuItem();
                    unlock.Header = App.Text("Worktree.Unlock");
                    unlock.Icon = this.CreateMenuIcon("Icons.Unlock");
                    unlock.Click += async (_, ev) =>
                    {
                        await repo.UnlockWorktreeAsync(worktree);
                        ev.Handled = true;
                    };
                    menu.Items.Add(unlock);
                }
                else
                {
                    var loc = new MenuItem();
                    loc.Header = App.Text("Worktree.Lock");
                    loc.Icon = this.CreateMenuIcon("Icons.Lock");
                    loc.IsEnabled = !worktree.IsMain;
                    loc.Click += async (_, ev) =>
                    {
                        await repo.LockWorktreeAsync(worktree);
                        ev.Handled = true;
                    };
                    menu.Items.Add(loc);
                }

                var remove = new MenuItem();
                remove.Header = App.Text("Worktree.Remove");
                remove.Icon = this.CreateMenuIcon("Icons.Clear");
                remove.IsEnabled = !worktree.IsCurrent && !worktree.IsMain;
                remove.Click += (_, ev) =>
                {
                    if (repo.CanCreatePopup())
                        repo.ShowPopup(new ViewModels.RemoveWorktree(repo, worktree));
                    ev.Handled = true;
                };
                menu.Items.Add(remove);

                var copy = new MenuItem();
                copy.Header = App.Text("Worktree.CopyPath");
                copy.Icon = this.CreateMenuIcon("Icons.Copy");
                copy.Click += async (_, ev) =>
                {
                    await this.CopyTextAsync(worktree.FullPath);
                    ev.Handled = true;
                };
                menu.Items.Add(new MenuItem() { Header = "-" });
                menu.Items.Add(copy);
                menu.Open(ctrl);
            }

            e.Handled = true;
        }

        private void OnWorktreeDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is Control { DataContext: ViewModels.Worktree worktree } && DataContext is ViewModels.Repository repo)
                repo.OpenWorktree(worktree);

            e.Handled = true;
        }

        private void OnWorktreeListPropertyChanged(object _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ItemsControl.ItemsSourceProperty || e.Property == IsVisibleProperty)
                UpdateLeftSidebarLayout();
        }

        private void OnLeftSidebarRowsChanged(object _, RoutedEventArgs e)
        {
            UpdateLeftSidebarLayout();
            e.Handled = true;
        }

        private void OnLeftSidebarSizeChanged(object _, SizeChangedEventArgs e)
        {
            if (e.HeightChanged)
                UpdateLeftSidebarLayout();
        }

        private void UpdateLeftSidebarLayout()
        {
            if (!IsLoaded || _isCommitSearchPanelVisible)
                return;

            if (DataContext is not ViewModels.Repository { UIStates: { } } vm)
                return;

            var leftHeight = LeftSidebarGroups.Bounds.Height - 28.0 * 6;
            if (leftHeight <= 0)
                return;

            var stashHeight = vm.IsStashGroupExpanded ? Math.Min(vm.StashesPage.VisibleStashes.Count * 24.0, Math.Max(24, leftHeight * .3)) : 0;
            StashList.Height = stashHeight;
            leftHeight -= stashHeight;

            var localBranchRows = vm.IsLocalBranchGroupExpanded ? LocalBranchTree.Rows.Count : 0;
            var remoteBranchRows = vm.IsRemoteGroupExpanded ? RemoteBranchTree.Rows.Count : 0;
            var desiredBranches = (localBranchRows + remoteBranchRows) * 24.0;
            var desiredTag = vm.IsTagGroupExpanded ? 24.0 * TagsList.Rows : 0;
            var desiredSubmodule = vm.IsSubmoduleGroupExpanded ? 24.0 * SubmoduleList.Rows : 0;
            var desiredWorktree = vm.IsWorktreeGroupExpanded ? 24.0 * vm.Worktrees.Count : 0;
            var desiredOthers = desiredTag + desiredSubmodule + desiredWorktree;
            var hasOverflow = (desiredBranches + desiredOthers > leftHeight);

            if (vm.IsWorktreeGroupExpanded)
            {
                var height = desiredWorktree;
                if (hasOverflow)
                {
                    var test = leftHeight - desiredBranches - desiredTag - desiredSubmodule;
                    if (test < 0)
                        height = Math.Min(120, height);
                    else
                        height = Math.Max(120, test);
                }

                leftHeight -= height;
                WorktreeList.Height = height;
                hasOverflow = (desiredBranches + desiredTag + desiredSubmodule) > leftHeight;
            }

            if (vm.IsSubmoduleGroupExpanded)
            {
                var height = desiredSubmodule;
                if (hasOverflow)
                {
                    var test = leftHeight - desiredBranches - desiredTag;
                    if (test < 0)
                        height = Math.Min(120, height);
                    else
                        height = Math.Max(120, test);
                }

                leftHeight -= height;
                SubmoduleList.Height = height;
                hasOverflow = (desiredBranches + desiredTag) > leftHeight;
            }

            if (vm.IsTagGroupExpanded)
            {
                var height = desiredTag;
                if (hasOverflow)
                {
                    var test = leftHeight - desiredBranches;
                    if (test < 0)
                        height = Math.Min(120, height);
                    else
                        height = Math.Max(120, test);
                }

                leftHeight -= height;
                TagsList.Height = height;
            }

            if (leftHeight > 0 && desiredBranches > leftHeight)
            {
                var local = localBranchRows * 24.0;
                var remote = remoteBranchRows * 24.0;
                var half = leftHeight / 2;
                if (vm.IsLocalBranchGroupExpanded)
                {
                    if (vm.IsRemoteGroupExpanded)
                    {
                        if (local < half)
                        {
                            LocalBranchTree.Height = local;
                            RemoteBranchTree.Height = leftHeight - local;
                        }
                        else if (remote < half)
                        {
                            RemoteBranchTree.Height = remote;
                            LocalBranchTree.Height = leftHeight - remote;
                        }
                        else
                        {
                            LocalBranchTree.Height = half;
                            RemoteBranchTree.Height = half;
                        }
                    }
                    else
                    {
                        LocalBranchTree.Height = leftHeight;
                    }
                }
                else if (vm.IsRemoteGroupExpanded)
                {
                    RemoteBranchTree.Height = leftHeight;
                }
            }
            else
            {
                if (vm.IsLocalBranchGroupExpanded)
                {
                    var height = localBranchRows * 24;
                    LocalBranchTree.Height = height;
                }

                if (vm.IsRemoteGroupExpanded)
                {
                    var height = remoteBranchRows * 24;
                    RemoteBranchTree.Height = height;
                }
            }
        }

        private void OnOpenSortLocalBranchMenu(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
            {
                var isSortByName = repo.IsSortingLocalBranchByName;
                var byNameAsc = new MenuItem();
                byNameAsc.Header = App.Text("Repository.BranchSort.ByName");
                if (isSortByName)
                    byNameAsc.Icon = this.CreateMenuIcon("Icons.Check");
                byNameAsc.Click += (_, ev) =>
                {
                    if (!isSortByName)
                        repo.IsSortingLocalBranchByName = true;
                    ev.Handled = true;
                };

                var byCommitterDate = new MenuItem();
                byCommitterDate.Header = App.Text("Repository.BranchSort.ByCommitterDate");
                if (!isSortByName)
                    byCommitterDate.Icon = this.CreateMenuIcon("Icons.Check");
                byCommitterDate.Click += (_, ev) =>
                {
                    if (isSortByName)
                        repo.IsSortingLocalBranchByName = false;
                    ev.Handled = true;
                };

                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                menu.Items.Add(byNameAsc);
                menu.Items.Add(byCommitterDate);
                menu.Open(button);
            }

            e.Handled = true;
        }

        private void OnOpenSortRemoteBranchMenu(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
            {
                var isSortByName = repo.IsSortingRemoteBranchByName;
                var byNameAsc = new MenuItem();
                byNameAsc.Header = App.Text("Repository.BranchSort.ByName");
                if (isSortByName)
                    byNameAsc.Icon = this.CreateMenuIcon("Icons.Check");
                byNameAsc.Click += (_, ev) =>
                {
                    if (!isSortByName)
                        repo.IsSortingRemoteBranchByName = true;
                    ev.Handled = true;
                };

                var byCommitterDate = new MenuItem();
                byCommitterDate.Header = App.Text("Repository.BranchSort.ByCommitterDate");
                if (!isSortByName)
                    byCommitterDate.Icon = this.CreateMenuIcon("Icons.Check");
                byCommitterDate.Click += (_, ev) =>
                {
                    if (isSortByName)
                        repo.IsSortingRemoteBranchByName = false;
                    ev.Handled = true;
                };

                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                menu.Items.Add(byNameAsc);
                menu.Items.Add(byCommitterDate);
                menu.Open(button);
            }

            e.Handled = true;
        }

        private void OnOpenSortTagMenu(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
            {
                var isSortByName = repo.IsSortingTagsByName;
                var byCreatorDate = new MenuItem();
                byCreatorDate.Header = App.Text("Repository.Tags.OrderByCreatorDate");
                if (!isSortByName)
                    byCreatorDate.Icon = this.CreateMenuIcon("Icons.Check");
                byCreatorDate.Click += (_, ev) =>
                {
                    if (isSortByName)
                        repo.IsSortingTagsByName = false;
                    ev.Handled = true;
                };

                var byName = new MenuItem();
                byName.Header = App.Text("Repository.Tags.OrderByName");
                if (isSortByName)
                    byName.Icon = this.CreateMenuIcon("Icons.Check");
                byName.Click += (_, ev) =>
                {
                    if (!isSortByName)
                        repo.IsSortingTagsByName = true;
                    ev.Handled = true;
                };

                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                menu.Items.Add(byName);
                menu.Items.Add(byCreatorDate);
                menu.Open(button);
            }

            e.Handled = true;
        }

        private async void OnPruneWorktrees(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
                await repo.PruneWorktreesAsync();

            e.Handled = true;
        }

        private void OnRemoveSelectedHistoryFilter(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo && sender is Button { DataContext: Models.HistoryFilter filter })
                repo.RemoveHistoryFilter(filter);

            e.Handled = true;
        }

        private async void OnBisectCommand(object sender, RoutedEventArgs e)
        {
            if (sender is Button button &&
                DataContext is ViewModels.Repository { IsBisectCommandRunning: false } repo &&
                repo.CanCreatePopup())
                await repo.ExecBisectCommandAsync(button.Tag as string);

            e.Handled = true;
        }

        private void OnRightPagePropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Control.IsVisibleProperty && sender is Control page)
            {
                var diffViewer = page.FindDescendantOfType<DiffView>();
                diffViewer?.ToggleHotkeyBindings(page.IsVisible);
            }
        }

        private void CalculateSidebarWidth()
        {
            var layout = ViewModels.Preferences.Instance.Layout;
            if (_isCommitSearchPanelVisible)
            {
                SidebarMinWidth = 200;
                SidebarWidth = new GridLength(layout.RepositorySearchCommitWidth, GridUnitType.Pixel);
            }
            else if (_isNormalSidebarVisible)
            {
                SidebarMinWidth = 200;
                SidebarWidth = new GridLength(layout.RepositorySidebarWidth, GridUnitType.Pixel);
            }
            else
            {
                // The workspace navigation now shares the branch sidebar.
                // Preserve the original compact navigation when branches are collapsed.
                SidebarMinWidth = 49;
                SidebarWidth = new GridLength(49, GridUnitType.Pixel);
            }
            ConstrainSidebar();
        }

        private bool _isNormalSidebarVisible = true;
        private bool _isCommitSearchPanelVisible = false;
        private double _sidebarMinWidth = 200;
        private double _sidebarMaxWidth = double.PositiveInfinity;
        private bool _applyingSidebarConstraint;
        private GridLength _sidebarWidth;
    }
}
