using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace SourceGit.Views
{
    public sealed class RepositorySettings : ChromelessWindow
    {
        private readonly ViewModels.Repository _repo;
        private ViewModels.RepositoryConfigure _configuration;
        private readonly DataGrid _remotes;
        private readonly ListBox _links;
        private readonly CheckBox _global;
        private readonly TextBox _name;
        private readonly TextBox _email;
        private readonly CheckBox _autoRefresh;
        private readonly CheckBox _backgroundFetch;
        private readonly TextBlock _error;
        private readonly Action<bool> _selectPage;
        public void SelectPage(bool advanced) => _selectPage(advanced);

        public RepositorySettings(ViewModels.Repository repo)
        {
            _repo = repo;
            _configuration = new ViewModels.RepositoryConfigure(repo);
            Title = "仓库设置";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            CloseOnESC = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new StackPanel { Margin = new Thickness(8), Spacing = 0 };
            var title = new Border { Background = new SolidColorBrush(Color.Parse("#9D9D9D")), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 8), Height = 24, Child = new TextBlock { Text = Title, Foreground = Brushes.White, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            title.PointerPressed += BeginMoveWindow;
            root.Children.Add(title);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -1), ZIndex = 1 };
            var remoteTab = Tab("远程仓库", "Icons.Remotes");
            var advancedTab = Tab("高级", "Icons.Settings");
            tabs.Children.Add(remoteTab); tabs.Children.Add(advancedTab); root.Children.Add(tabs);
            var border = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = this.FindResource("Brush.Contents") as IBrush, Padding = new Thickness(8) };
            var remotePage = new StackPanel { Spacing = 8 };
            remotePage.Children.Add(new TextBlock { Text = "远程仓库路径" });
            _remotes = new DataGrid { Height = 200, AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 26, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#AEC8DF")), SelectionMode = DataGridSelectionMode.Single };
            _remotes.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding("Name"), Width = new DataGridLength(150) });
            _remotes.Columns.Add(new DataGridTextColumn { Header = "路径", Binding = new Binding("URL"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            remotePage.Children.Add(_remotes);
            var remoteActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var add = Action("添加", async () => await EditRemoteAsync(null));
            var edit = Action("编辑", async () => { if (_remotes.SelectedItem is Models.Remote r) await EditRemoteAsync(r); });
            var remove = Action("移除", RemoveRemoteAsync);
            edit.IsEnabled = remove.IsEnabled = false;
            _remotes.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = _remotes.SelectedItem != null;
            _remotes.DoubleTapped += async (_, _) => { if (_remotes.SelectedItem is Models.Remote r) await EditRemoteAsync(r); };
            remoteActions.Children.Add(add); remoteActions.Children.Add(edit); remoteActions.Children.Add(remove); remotePage.Children.Add(remoteActions);

            var advancedPage = new StackPanel { Spacing = 12 };
            var ignored = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var ignorePath = Path.Combine(repo.FullPath, ".gitignore");
            ignored.Children.Add(new TextBlock { Text = ignorePath, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var ignoreEdit = Action("编辑", () => { if (!File.Exists(ignorePath)) File.WriteAllText(ignorePath, ""); Native.OS.OpenWithDefaultEditor(ignorePath); return Task.CompletedTask; });
            Grid.SetColumn(ignoreEdit, 1); ignored.Children.Add(ignoreEdit); advancedPage.Children.Add(Group("仓库指定忽略列表", ignored));
            var identity = new StackPanel { Spacing = 8 };
            _global = new CheckBox { Content = "使用全局用户配置", IsChecked = string.IsNullOrEmpty(LocalConfig("user.name")) && string.IsNullOrEmpty(LocalConfig("user.email")) };
            identity.Children.Add(_global);
            _name = new TextBox { Text = _configuration.UserName, Height = 26 };
            _email = new TextBox { Text = _configuration.UserEmail, Height = 26 };
            identity.Children.Add(Field("全名：", _name)); identity.Children.Add(Field("电子邮件地址：", _email));
            void EnableIdentity() => _name.IsEnabled = _email.IsEnabled = _global.IsChecked != true;
            _global.IsCheckedChanged += (_, _) => EnableIdentity(); EnableIdentity();
            advancedPage.Children.Add(Group("用户信息", identity));
            var links = new StackPanel { Spacing = 6 };
            _links = new ListBox { Height = 92, ItemsSource = _configuration.IssueTrackers, DisplayMemberBinding = new Binding("Name"), Background = this.FindResource("Brush.Contents") as IBrush, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            links.Children.Add(_links);
            var linkActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            linkActions.Children.Add(Action("添加", async () => await EditLinkAsync(null)));
            linkActions.Children.Add(Action("编辑", async () => { if (_links.SelectedItem is Models.IssueTracker tracker) await EditLinkAsync(tracker); }));
            linkActions.Children.Add(Action("删除", () => { if (_links.SelectedItem is Models.IssueTracker tracker) _configuration.IssueTrackers.Remove(tracker); return Task.CompletedTask; }));
            links.Children.Add(linkActions); advancedPage.Children.Add(Group("提交文本链接", links));
            var miscellaneous = new StackPanel { Spacing = 6 };
            _autoRefresh = new CheckBox { Content = "自动刷新（关闭后需手动刷新此仓库）", IsChecked = repo.Settings.AutoRefreshEnabled };
            _backgroundFetch = new CheckBox { Content = "在后台刷新远端状态（在全局设置中启用时）", IsChecked = repo.Settings.BackgroundFetchEnabled };
            miscellaneous.Children.Add(_autoRefresh); miscellaneous.Children.Add(_backgroundFetch);
            var original = Action("其他配置…", async () =>
            {
                await new RepositoryConfigure { DataContext = new ViewModels.RepositoryConfigure(repo) }.ShowDialog(this);
                _configuration = new ViewModels.RepositoryConfigure(repo);
                _name.Text = _configuration.UserName; _email.Text = _configuration.UserEmail;
                _global.IsChecked = string.IsNullOrEmpty(LocalConfig("user.name")) && string.IsNullOrEmpty(LocalConfig("user.email"));
                _links.ItemsSource = _configuration.IssueTrackers;
            });
            miscellaneous.Children.Add(original); advancedPage.Children.Add(Group("杂项", miscellaneous));
            border.Child = remotePage; root.Children.Add(border);
            void Select(bool advanced)
            {
                border.Child = advanced ? advancedPage : remotePage;
                SetTabAppearance(remoteTab, !advanced);
                SetTabAppearance(advancedTab, advanced);
            }
            _selectPage = Select;
            remoteTab.Click += (_, _) => Select(false); advancedTab.Click += (_, _) => Select(true); Select(false);
            _error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            root.Children.Add(_error);
            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            footer.Children.Add(Action("编辑配置文件…", () => { Native.OS.OpenWithDefaultEditor(Path.Combine(repo.GitDir, "config")); return Task.CompletedTask; }));
            var ok = Action("确定", SaveAsync); Grid.SetColumn(ok, 2); footer.Children.Add(ok);
            var cancel = new Button { Content = "取消", Classes = { "flat" }, MinWidth = 74, Margin = new Thickness(6, 0, 0, 0) }; cancel.Click += (_, _) => Close(); Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
            root.Children.Add(footer); Content = root;
            Opened += async (_, _) => await RefreshRemotesAsync();
        }

        private string LocalConfig(string key) => new LocalConfigQuery(_repo.FullPath, key).ReadValue();
        private sealed class LocalConfigQuery : Commands.Command
        {
            public LocalConfigQuery(string repo, string key)
            {
                WorkingDirectory = repo; Context = repo; Args = "config --local --get " + key; RaiseError = false;
            }
            public string ReadValue() => ReadToEnd().StdOut.Trim();
        }

        private async Task SaveAsync()
        {
            _configuration.UserName = _name.Text ?? ""; _configuration.UserEmail = _email.Text ?? "";
            await _configuration.SaveAsync();
            if (_global.IsChecked == true)
            {
                var config = new Commands.Config(_repo.FullPath) { RaiseError = false };
                await config.SetAsync("user.name", null); await config.SetAsync("user.email", null);
            }
            _repo.Settings.AutoRefreshEnabled = _autoRefresh.IsChecked == true;
            _repo.Settings.BackgroundFetchEnabled = _backgroundFetch.IsChecked == true;
            _repo.Settings.Save(); _repo.RefreshAll(); Close();
        }

        private async Task RefreshRemotesAsync()
        {
            var remotes = await new Commands.QueryRemotes(_repo.FullPath).GetResultAsync();
            _remotes.ItemsSource = new AvaloniaList<Models.Remote>(remotes);
        }

        private async Task EditRemoteAsync(Models.Remote remote)
        {
            var editor = new RepositoryRemoteEditor(_repo, remote);
            if (await editor.ShowDialog<bool>(this)) { await RefreshRemotesAsync(); _repo.RefreshAll(); }
        }

        private async Task RemoveRemoteAsync()
        {
            if (_remotes.SelectedItem is not Models.Remote remote) return;
            var confirm = new RepositoryForm("移除远程仓库", ["将移除远程配置 “" + remote.Name + "”。远端仓库及本地提交不会被删除。"], []);
            if (!await confirm.ShowDialog<bool>(this)) return;
            if (await new Commands.Remote(_repo.FullPath).DeleteAsync(remote.Name.Quoted())) { await RefreshRemotesAsync(); _repo.RefreshAll(); }
        }

        private async Task EditLinkAsync(Models.IssueTracker tracker)
        {
            var form = new RepositoryForm(tracker == null ? "添加提交文本链接" : "编辑提交文本链接", ["名称", "匹配表达式", "链接模板"], [tracker?.Name ?? "", tracker?.RegexString ?? @"#(\d+)", tracker?.URLTemplate ?? "https://example.com/issues/$1"]);
            if (!await form.ShowDialog<bool>(this)) return;
            if (tracker == null) _configuration.AddIssueTracker(form.Values[0], form.Values[1], form.Values[2]);
            else { tracker.Name = form.Values[0]; tracker.RegexString = form.Values[1]; tracker.URLTemplate = form.Values[2]; }
            _links.ItemsSource = null; _links.ItemsSource = _configuration.IssueTrackers;
        }

        internal static Button Action(string caption, Func<Task> action)
        {
            var button = new Button { Content = caption, Classes = { "flat" }, MinWidth = 70, Padding = new Thickness(10, 2) };
            button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception error) { await new RepositoryForm("操作未完成", [error.Message], []).ShowDialog(TopLevel.GetTopLevel(button) as Window); } finally { button.IsEnabled = true; } };
            return button;
        }

        private void SetTabAppearance(Button button, bool selected)
        {
            button.Background = selected ? this.FindResource("Brush.Contents") as IBrush : new SolidColorBrush(Color.Parse("#E1E5E8"));
            button.BorderThickness = new Thickness(1, 1, 1, selected ? 0 : 1);
            if (button.Content is StackPanel content)
                foreach (var child in content.Children)
                {
                    if (child is TextBlock text) { text.FontWeight = selected ? FontWeight.Bold : FontWeight.Normal; text.Foreground = this.FindResource(selected ? "Brush.FG1" : "Brush.FG2") as IBrush; }
                    else if (child is Avalonia.Controls.Shapes.Path icon) icon.Fill = new SolidColorBrush(Color.Parse(selected ? "#61768A" : "#9DA6AE"));
                }
        }

        private static Button Tab(string caption, string icon)
        {
            var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Avalonia.Controls.Shapes.Path { Width = 22, Height = 22, Data = App.Current.FindResource(icon) as Geometry, Fill = new SolidColorBrush(Color.Parse("#7A869A")), HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center });
            // Keep the selected page visible while the pointer is over a tab, too.
            // The generic button theme otherwise replaces its background on hover.
            return new Button
            {
                Content = content, Width = 84, Height = 58,
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                Padding = new Thickness(4),
                Template = new FuncControlTemplate<Button>((button, _) =>
                {
                    var frame = new Border
                    {
                        Child = new ContentPresenter
                        {
                            Content = button.Content,
                            HorizontalContentAlignment = HorizontalAlignment.Center,
                            VerticalContentAlignment = VerticalAlignment.Center,
                        },
                    };
                    frame.Bind(Border.BackgroundProperty, button.GetObservable(BackgroundProperty));
                    frame.Bind(Border.BorderBrushProperty, button.GetObservable(BorderBrushProperty));
                    frame.Bind(Border.BorderThicknessProperty, button.GetObservable(BorderThicknessProperty));
                    frame.Bind(Border.PaddingProperty, button.GetObservable(PaddingProperty));
                    return frame;
                }),
            };
        }

        private static Control Group(string title, Control content)
            => new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title }, new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#CED8DF")), CornerRadius = new CornerRadius(3), Padding = new Thickness(10), Child = content } } };

        private static Control Field(string label, Control editor)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*") };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(editor, 1); row.Children.Add(editor); return row;
        }
    }

    internal sealed class RepositoryForm : ChromelessWindow
    {
        private readonly TextBox[] _fields;
        public string[] Values => _fields.Select(f => f.Text ?? "").ToArray();
        public RepositoryForm(string title, string[] labels, string[] values)
        {
            Title = title; Width = 510; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; CanResize = false; CloseOnESC = true;
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold });
            _fields = values.Select((v, i) => new TextBox { Text = v, Watermark = labels[i], Height = 28 }).ToArray();
            if (values.Length == 0) panel.Children.Add(new TextBlock { Text = labels[0], TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < _fields.Length; i++) { panel.Children.Add(new TextBlock { Text = labels[i] }); panel.Children.Add(_fields[i]); }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(RepositorySettings.Action("确定", () => { Close(true); return Task.CompletedTask; }));
            buttons.Children.Add(RepositorySettings.Action("取消", () => { Close(false); return Task.CompletedTask; })); panel.Children.Add(buttons); Content = panel;
        }
    }
}
