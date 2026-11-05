using System;
using System.IO;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public class LauncherTabSizeBox : Border
    {
        public static readonly DirectProperty<LauncherTabSizeBox, bool> UseFixedWidthProperty =
            AvaloniaProperty.RegisterDirect<LauncherTabSizeBox, bool>(
                nameof(UseFixedWidth),
                static o => o.UseFixedWidth,
                static (o, v) => o.UseFixedWidth = v);

        public bool UseFixedWidth
        {
            get => _useFixedWidth;
            set => SetAndRaise(UseFixedWidthProperty, ref _useFixedWidth, value);
        }

        public LauncherTabSizeBox()
        {
            Width = 200;
        }

        protected override Type StyleKeyOverride => typeof(Border);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == UseFixedWidthProperty)
            {
                if (_useFixedWidth)
                    Width = 200;
                else
                    Width = double.NaN;
            }
        }

        private bool _useFixedWidth = true;
    }

    public partial class LauncherTabBar : UserControl
    {
        public static readonly DirectProperty<LauncherTabBar, bool> IsScrollButtonVisibleProperty =
            AvaloniaProperty.RegisterDirect<LauncherTabBar, bool>(
                nameof(IsScrollButtonVisible),
                static o => o.IsScrollButtonVisible);

        public bool IsScrollButtonVisible
        {
            get => _isScrollButtonVisible;
            set => SetAndRaise(IsScrollButtonVisibleProperty, ref _isScrollButtonVisible, value);
        }

        public LauncherTabBar()
        {
            InitializeComponent();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property.Name == nameof(ActualThemeVariant) && change.NewValue != null)
                InvalidateVisual();
        }

        private void ScrollTabs(object _, PointerWheelEventArgs e)
        {
            if (Math.Abs(e.Delta.X) < Math.Abs(e.Delta.Y))
            {
                var x = LauncherTabsScroller.Offset.X;
                var extent = LauncherTabsScroller.Extent.Width;
                var viewport = LauncherTabsScroller.Viewport.Width;
                var delta = e.Delta.Y;

                if (extent > viewport)
                {
                    x += -delta * 64; // Use the same logic with vertical scrolling in `ScrollContentPresenter`
                    x = Math.Min(Math.Max(x, 0), extent - viewport);
                }

                LauncherTabsScroller.Offset = new Vector(x, 0);
                e.Handled = true;
            }
        }

        private void ScrollTabsLeft(object _, RoutedEventArgs e)
        {
            LauncherTabsScroller.Offset -= _scrollStep;
            e.Handled = true;
        }

        private void ScrollTabsRight(object _, RoutedEventArgs e)
        {
            LauncherTabsScroller.Offset += _scrollStep;
            e.Handled = true;
        }

        public ContextMenu TabListMenu { get; private set; }

        public void ActivateTab(ViewModels.LauncherPage page)
        {
            if (DataContext is ViewModels.Launcher launcher && launcher.Pages.Contains(page))
            {
                launcher.ActivePage = page;
                LauncherTabsList.ScrollIntoView(page);
                TabListMenu?.Close();
            }
        }

        public void OpenTabList()
        {
            if (DataContext is not ViewModels.Launcher launcher) return;
            TabListMenu?.Close();
            var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, MinWidth = 240, MaxWidth = 420, MaxHeight = 420 };
            MenuItem active = null;
            foreach (var page in launcher.Pages)
            {
                var selected = page == launcher.ActivePage;
                var name = page.Node.IsRepository ? page.Node.Name : "TreeGit";
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = name, MaxWidth = 350, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? FontWeight.Bold : FontWeight.Normal },
                    Icon = this.CreateMenuIcon(selected ? "Icons.Check" : "Icons.Repositories"),
                };
                ToolTip.SetTip(item, page.Data is ViewModels.Repository repo ? repo.FullPath : name);
                item.Click += (_, e) => { ActivateTab(page); e.Handled = true; };
                menu.Items.Add(item);
                if (selected) active = item;
            }
            menu.Opened += (_, _) => Dispatcher.UIThread.Post(() => active?.BringIntoView(), DispatcherPriority.Background);
            menu.Closed += (_, _) => { if (TabListMenu == menu) TabListMenu = null; };
            TabListMenu = menu;
            menu.Open(IsScrollButtonVisible ? OverflowTabListButton : NormalTabListButton);
        }

        private void OnOpenTabList(object _, RoutedEventArgs e)
        {
            OpenTabList();
            e.Handled = true;
        }

        private void OnTabsLayoutUpdated(object _1, EventArgs _2)
        {
            IsScrollButtonVisible = LauncherTabsScroller.Extent.Width > LauncherTabsScroller.Viewport.Width;
            InvalidateVisual();
        }

        private void OnTabsSelectionChanged(object _1, SelectionChangedEventArgs _2)
        {
            if (LauncherTabsList.SelectedItem == null && DataContext is ViewModels.Launcher launcher &&
                launcher.ActivePage is { } page && launcher.Pages.Contains(page))
                LauncherTabsList.SelectedItem = page;
            InvalidateVisual();
        }

        private void OnPointerPressedTab(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border border)
            {
                var point = e.GetCurrentPoint(border);
                if (point.Properties.IsMiddleButtonPressed && border.DataContext is ViewModels.LauncherPage page)
                {
                    (DataContext as ViewModels.Launcher)?.CloseTab(page);
                    e.Handled = true;
                }
                else if (point.Properties.IsLeftButtonPressed)
                {
                    _pressedTabEvent = e;
                    _startDragTab = false;
                }
                else
                {
                    _pressedTabEvent = null;
                    _startDragTab = false;
                }
            }
        }

        private void OnPointerReleasedTab(object _1, PointerReleasedEventArgs _2)
        {
            _pressedTabEvent = null;
            _startDragTab = false;
        }

        private async void OnPointerMovedOverTab(object sender, PointerEventArgs e)
        {
            if (_pressedTabEvent != null && !_startDragTab && sender is Border { DataContext: ViewModels.LauncherPage page } border)
            {
                var delta = e.GetPosition(border) - _pressedTabEvent.GetPosition(border);
                var sizeSquired = delta.X * delta.X + delta.Y * delta.Y;
                if (sizeSquired < 64)
                    return;

                _startDragTab = true;

                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(_dndMainTabFormat, page.Node.Id));
                await DragDrop.DoDragDropAsync(_pressedTabEvent, data, DragDropEffects.Move);
            }
            e.Handled = true;
        }

        private void DropTab(object sender, DragEventArgs e)
        {
            if (e.DataTransfer.TryGetValue(_dndMainTabFormat) is not { Length: > 0 } id)
                return;

            if (DataContext is not ViewModels.Launcher launcher)
                return;

            ViewModels.LauncherPage target = null;
            foreach (var page in launcher.Pages)
            {
                if (page.Node.Id.Equals(id, StringComparison.Ordinal))
                {
                    target = page;
                    break;
                }
            }

            if (target == null)
                return;

            if (sender is not Border { DataContext: ViewModels.LauncherPage to })
                return;

            if (target == to)
                return;

            launcher.MoveTab(target, to);

            _pressedTabEvent = null;
            _startDragTab = false;
            e.Handled = true;
        }

        private void OnTabContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (sender is Border { DataContext: ViewModels.LauncherPage page } border &&
                DataContext is ViewModels.Launcher vm)
            {
                var menu = new ContextMenu();

                if (vm.ActivePage.Data is ViewModels.Repository repo)
                {
                    var explore = new MenuItem();
                    explore.Header = App.Text("Repository.Explore");
                    explore.Icon = this.CreateMenuIcon("Icons.Explore");
                    explore.Tag = OperatingSystem.IsMacOS() ? "⌘+E" : "Ctrl+E";
                    explore.Click += (_, e) =>
                    {
                        Native.OS.OpenInFileManager(repo.FullPath);
                        e.Handled = true;
                    };
                    menu.Items.Add(explore);

                    var refresh = new MenuItem();
                    refresh.Header = App.Text("PageTabBar.Tab.Refresh");
                    refresh.Icon = this.CreateMenuIcon("Icons.Loading");
                    refresh.Tag = "F5";
                    refresh.Click += (_, ev) =>
                    {
                        repo.RefreshAll();
                        ev.Handled = true;
                    };
                    menu.Items.Add(refresh);

                    var copyPath = new MenuItem();
                    copyPath.Header = App.Text("PageTabBar.Tab.CopyPath");
                    copyPath.Icon = this.CreateMenuIcon("Icons.Copy");
                    copyPath.Click += async (_, ev) =>
                    {
                        var dir = new DirectoryInfo(repo.FullPath);
                        await this.CopyTextAsync(dir.FullName);
                        ev.Handled = true;
                    };
                    menu.Items.Add(copyPath);
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    var edit = new MenuItem();
                    edit.Header = App.Text("PageTabBar.Tab.Edit");
                    edit.Icon = this.CreateMenuIcon("Icons.Edit");
                    edit.Click += (_, ev) =>
                    {
                        page.Node.Edit();
                        ev.Handled = true;
                    };
                    menu.Items.Add(edit);

                    var workspaces = ViewModels.Preferences.Instance.Workspaces;
                    if (workspaces.Count > 1)
                    {
                        var moveTo = new MenuItem();
                        moveTo.Header = App.Text("PageTabBar.Tab.MoveToWorkspace");
                        moveTo.Icon = this.CreateMenuIcon("Icons.MoveTo");

                        foreach (var ws in workspaces)
                        {
                            var dupWs = ws;
                            var isCurrent = dupWs == vm.ActiveWorkspace;
                            var icon = this.CreateMenuIcon(isCurrent ? "Icons.Check" : "Icons.Workspace");
                            icon.Fill = dupWs.Brush;

                            var target = new MenuItem();
                            target.Header = ws.Name;
                            target.Icon = icon;
                            target.Click += (_, ev) =>
                            {
                                if (!isCurrent)
                                {
                                    vm.CloseTab(page);
                                    dupWs.Repositories.Add(repo.FullPath);
                                }

                                ev.Handled = true;
                            };
                            moveTo.Items.Add(target);
                        }

                        menu.Items.Add(moveTo);
                    }

                    menu.Items.Add(new MenuItem() { Header = "-" });
                }

                var close = new MenuItem();
                close.Header = App.Text("PageTabBar.Tab.Close");
                close.Tag = OperatingSystem.IsMacOS() ? "⌘+W" : "Ctrl+W";
                close.Click += (_, ev) =>
                {
                    vm.CloseTab(page);
                    ev.Handled = true;
                };
                menu.Items.Add(close);

                var closeOthers = new MenuItem();
                closeOthers.Header = App.Text("PageTabBar.Tab.CloseOther");
                closeOthers.Click += (_, ev) =>
                {
                    vm.CloseOtherTabs();
                    ev.Handled = true;
                };
                menu.Items.Add(closeOthers);

                var closeRight = new MenuItem();
                closeRight.Header = App.Text("PageTabBar.Tab.CloseRight");
                closeRight.Click += (_, ev) =>
                {
                    vm.CloseRightTabs();
                    ev.Handled = true;
                };
                menu.Items.Add(closeRight);
                menu.Open(border);
            }

            e.Handled = true;
        }

        private void OnCloseTab(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && DataContext is ViewModels.Launcher vm)
                vm.CloseTab(btn.DataContext as ViewModels.LauncherPage);

            e.Handled = true;
        }

        private bool _isScrollButtonVisible = false;
        private readonly Vector _scrollStep = new(64, 0);
        private PointerPressedEventArgs _pressedTabEvent = null;
        private bool _startDragTab = false;
        private readonly DataFormat<string> _dndMainTabFormat = DataFormat.CreateStringApplicationFormat("sourcegit-dnd-main-tab");
    }
}
