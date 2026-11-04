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
        private readonly ViewModels.RepositoryConfigure _configuration;
        private readonly DataGrid _remotes;
        private readonly ListBox _links;
        private readonly CheckBox _global;
        private readonly TextBox _name;
        private readonly TextBox _email;
        private readonly CheckBox _autoRefresh;
        private readonly CheckBox _backgroundFetch;
        private readonly ComboBox _defaultRemote;
        private bool _updatingRemoteChoices;
        private readonly TextBlock _error;
        private readonly Action<bool> _selectPage;
        private Window _screenOwner;
        private bool _advanced;
        public void SelectPage(bool advanced) => _selectPage(advanced);

        public void FitToOwner(Window owner)
        {
            _screenOwner = owner;
            FitToScreen();
        }

        private void FitToScreen()
        {
            if (_screenOwner == null) return;
            var screen = IsVisible ? Screens.ScreenFromWindow(this) : _screenOwner.Screens.ScreenFromWindow(_screenOwner);
            screen ??= _screenOwner.Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var zoom = ViewModels.Preferences.Instance.Zoom;
            var centerX = IsVisible ? Position.X + Bounds.Width * scale / 2 : _screenOwner.Position.X + _screenOwner.Bounds.Width * scale / 2;
            var centerY = IsVisible ? Position.Y + Bounds.Height * scale / 2 : _screenOwner.Position.Y + _screenOwner.Bounds.Height * scale / 2;
            MaxWidth = Math.Max(1, area.Width / scale - 32);
            MaxHeight = Math.Max(1, area.Height / scale - 32);
            Width = Math.Min(640 * zoom, MaxWidth);
            Height = Math.Min((_advanced ? 620 : 420) * zoom, MaxHeight);
            var x = Math.Clamp(centerX - Width * scale / 2, area.X + 8, Math.Max(area.X + 8, area.Right - Width * scale - 8));
            var y = Math.Clamp(centerY - Height * scale / 2, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - Height * scale - 8));
            Position = new PixelPoint((int)x, (int)y);
        }

        public RepositorySettings(ViewModels.Repository repo)
        {
            _repo = repo;
            _configuration = new ViewModels.RepositoryConfigure(repo);
            Title = "仓库设置";
            Width = 640;
            SizeToContent = SizeToContent.Manual;
            CanResize = false;
            CloseOnESC = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var root = new Grid { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
            var title = new Border { Background = new SolidColorBrush(Color.Parse("#9D9D9D")), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 8), Height = 24, Child = new TextBlock { Text = Title, Foreground = Brushes.White, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            title.PointerPressed += BeginMoveWindow;
            root.Children.Add(title);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -1), ZIndex = 1 };
            var remoteTab = Tab("远程仓库", "Icons.Remotes");
            var advancedTab = Tab("高级", "Icons.Settings");
            tabs.Children.Add(remoteTab); tabs.Children.Add(advancedTab); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
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

            var advancedPage = new StackPanel { Spacing = 6 };
            var ignored = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var ignorePath = Path.Combine(repo.FullPath, ".gitignore");
            ignored.Children.Add(new TextBlock { Text = ignorePath, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var ignoreEdit = Action("编辑", () => { if (!File.Exists(ignorePath)) File.WriteAllText(ignorePath, ""); Native.OS.OpenWithDefaultEditor(ignorePath); return Task.CompletedTask; });
            Grid.SetColumn(ignoreEdit, 1); ignored.Children.Add(ignoreEdit); advancedPage.Children.Add(Group("仓库指定忽略列表", ignored));
            var identity = new StackPanel { Spacing = 4 };
            _global = new CheckBox { Name = "UseGlobalIdentity", Content = "使用全局用户配置", IsChecked = _configuration.UseGlobalIdentity };
            identity.Children.Add(_global);
            _name = new TextBox { Name = "CommitterName", Text = _configuration.UserName, Height = 26 };
            _email = new TextBox { Name = "CommitterEmail", Text = _configuration.UserEmail, Height = 26 };
            identity.Children.Add(Field("全名：", _name)); identity.Children.Add(Field("电子邮件地址：", _email));
            void EnableIdentity() => _name.IsEnabled = _email.IsEnabled = _global.IsChecked != true;
            _global.IsCheckedChanged += (_, _) => EnableIdentity(); EnableIdentity();
            advancedPage.Children.Add(Group("提交者信息", identity));
            var links = new StackPanel { Spacing = 6 };
            _links = new ListBox { Height = 64, ItemsSource = _configuration.IssueTrackers, DisplayMemberBinding = new Binding("Name"), Background = this.FindResource("Brush.Contents") as IBrush, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            links.Children.Add(_links);
            var linkActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            linkActions.Children.Add(Action("添加", async () => await EditLinkAsync(null)));
            linkActions.Children.Add(Action("编辑", async () => { if (_links.SelectedItem is Models.IssueTracker tracker) await EditLinkAsync(tracker); }));
            linkActions.Children.Add(Action("删除", () => { if (_links.SelectedItem is Models.IssueTracker tracker) _configuration.IssueTrackers.Remove(tracker); return Task.CompletedTask; }));
            links.Children.Add(linkActions); advancedPage.Children.Add(Optional("提交文本链接", links));
            var sync = new StackPanel { Spacing = 4 };
            var remoteNames = _configuration.Remotes.ToList();
            if (!string.IsNullOrEmpty(_configuration.DefaultRemote) && !remoteNames.Contains(_configuration.DefaultRemote)) remoteNames.Add(_configuration.DefaultRemote);
            _defaultRemote = new ComboBox { Name = "DefaultRemote", ItemsSource = new[] { "自动选择" }.Concat(remoteNames).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = Math.Max(0, remoteNames.IndexOf(_configuration.DefaultRemote) + 1) };
            _defaultRemote.SelectionChanged += (_, _) =>
            {
                if (!_updatingRemoteChoices && _defaultRemote.SelectedIndex >= 0)
                    _configuration.DefaultRemote = _defaultRemote.SelectedIndex > 0 ? _defaultRemote.SelectedItem as string : string.Empty;
            };
            sync.Children.Add(Field("默认远程：", _defaultRemote));
            var merge = new ComboBox { Name = "MergeMode", ItemsSource = Models.MergeMode.Supported.Select(m => m.DisplayName).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = Math.Clamp(_configuration.PreferredMergeMode, 0, Models.MergeMode.Supported.Length - 1) };
            merge.SelectionChanged += (_, _) => _configuration.PreferredMergeMode = merge.SelectedIndex;
            sync.Children.Add(Field("合并方式：", merge));
            var proxy = new TextBox { Name = "HttpProxy", Height = 28, Text = _configuration.HttpProxy, Watermark = "留空继承全局代理" };
            proxy.TextChanged += (_, _) => _configuration.HttpProxy = proxy.Text ?? string.Empty;
            sync.Children.Add(Field("HTTP 代理：", proxy));
            sync.Children.Add(Option("PruneOnFetch", "获取时清理已删除的远程分支引用", _configuration.EnablePruneOnFetch, value => _configuration.EnablePruneOnFetch = value));
            _autoRefresh = Option("AutoRefresh", "自动刷新文件状态", _configuration.AutoRefreshEnabled, value => _configuration.AutoRefreshEnabled = value);
            _backgroundFetch = Option("BackgroundFetch", "允许后台获取远程状态（需全局开启）", _configuration.BackgroundFetchEnabled, value => _configuration.BackgroundFetchEnabled = value);
            sync.Children.Add(_autoRefresh); sync.Children.Add(_backgroundFetch);
            advancedPage.Children.Add(Group("同步与合并", sync));
            var signing = new StackPanel { Spacing = 4 };
            var signingKey = new TextBox { Name = "SigningKey", Height = 28, Text = _configuration.GPGUserSigningKey, Watermark = "留空使用全局签名密钥" };
            signingKey.TextChanged += (_, _) => _configuration.GPGUserSigningKey = signingKey.Text ?? string.Empty;
            signing.Children.Add(Field("签名密钥：", signingKey));
            signing.Children.Add(Option("SignCommits", "签名提交", _configuration.GPGCommitSigningEnabled, value => _configuration.GPGCommitSigningEnabled = value));
            signing.Children.Add(Option("SignTags", "签名标签", _configuration.GPGTagSigningEnabled, value => _configuration.GPGTagSigningEnabled = value));
            advancedPage.Children.Add(Optional("提交与标签签名", signing));
            var submodules = new StackPanel { Spacing = 4 };
            submodules.Children.Add(Option("AskSubmodules", "自动更新子模块前询问", _configuration.AskBeforeAutoUpdatingSubmodules, value => _configuration.AskBeforeAutoUpdatingSubmodules = value));
            submodules.Children.Add(Option("RecursiveSubmodules", "递归更新子模块", _configuration.EnableRecursiveWhenAutoUpdatingSubmodules, value => _configuration.EnableRecursiveWhenAutoUpdatingSubmodules = value));
            advancedPage.Children.Add(Optional("子模块更新", submodules));
            var templates = new StackPanel { Spacing = 4 };
            var templateList = new ListBox { Name = "CommitTemplates", Height = 72, ItemsSource = _configuration.CommitTemplates, DisplayMemberBinding = new Binding("Name"), Background = this.FindResource("Brush.Contents") as IBrush };
            templates.Children.Add(templateList);
            async Task EditTemplate(Models.CommitTemplate template)
            {
                var editor = new RepositoryForm(template == null ? "添加提交模板" : "编辑提交模板", ["名称", "内容"], [template?.Name ?? "", template?.Content ?? ""], 1);
                if (!await editor.ShowDialog<bool>(this) || string.IsNullOrWhiteSpace(editor.Values[0])) return;
                if (template == null) _configuration.CommitTemplates.Add(new Models.CommitTemplate { Name = editor.Values[0], Content = editor.Values[1] });
                else { template.Name = editor.Values[0]; template.Content = editor.Values[1]; }
            }
            var templateActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            templateActions.Children.Add(Action("添加", () => EditTemplate(null)));
            var editTemplate = Action("编辑", () => templateList.SelectedItem is Models.CommitTemplate t ? EditTemplate(t) : Task.CompletedTask);
            var removeTemplate = Action("删除", () => { if (templateList.SelectedItem is Models.CommitTemplate t) _configuration.CommitTemplates.Remove(t); return Task.CompletedTask; });
            editTemplate.IsEnabled = removeTemplate.IsEnabled = false;
            templateList.SelectionChanged += (_, _) => editTemplate.IsEnabled = removeTemplate.IsEnabled = templateList.SelectedItem != null;
            templateActions.Children.Add(editTemplate); templateActions.Children.Add(removeTemplate); templates.Children.Add(templateActions);
            advancedPage.Children.Add(Optional("提交模板", templates));
            var scroll = new ScrollViewer { Content = remotePage, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            border.Child = scroll; Grid.SetRow(border, 2); root.Children.Add(border);
            void Select(bool advanced)
            {
                _advanced = advanced;
                scroll.Content = advanced ? advancedPage : remotePage;
                scroll.Offset = new Vector(0, 0);
                SetTabAppearance(remoteTab, !advanced);
                SetTabAppearance(advancedTab, advanced);
                FitToScreen();
            }
            _selectPage = Select;
            remoteTab.Click += (_, _) => Select(false); advancedTab.Click += (_, _) => Select(true); Select(false);
            _error = new TextBlock { Name = "RepositorySettingsError", Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            Grid.SetRow(_error, 3); root.Children.Add(_error);
            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            footer.Children.Add(Action("编辑配置文件…", () => { Native.OS.OpenWithDefaultEditor(Path.Combine(repo.GitDir, "config")); return Task.CompletedTask; }));
            var ok = Action("确定", SaveAsync); ok.Name = "ConfirmRepositorySettings"; Grid.SetColumn(ok, 2); footer.Children.Add(ok);
            var cancel = new Button { Content = "取消", Classes = { "flat" }, MinWidth = 74, Margin = new Thickness(6, 0, 0, 0) }; cancel.Click += (_, _) => Close(); Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
            Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
            Opened += async (_, _) =>
            {
                FitToScreen(); await RefreshRemotesAsync();
#if INTEGRATION_TESTS
                Tests.RepositorySettingsProbe.OnOpened(this, _configuration, _repo, SaveAsync);
#endif
            };
        }

        private async Task SaveAsync()
        {
            try
            {
                _error.IsVisible = false;
                _configuration.UserName = _name.Text ?? "";
                _configuration.UserEmail = _email.Text ?? "";
                _configuration.UseGlobalIdentity = _global.IsChecked == true;
                await _configuration.SaveAsync();
                _repo.RefreshAll();
                Close();
            }
            catch (Exception ex)
            {
                _error.Text = ex.Message;
                _error.IsVisible = true;
            }
        }

        private async Task RefreshRemotesAsync()
        {
            var remotes = await new Commands.QueryRemotes(_repo.FullPath).GetResultAsync();
            _remotes.ItemsSource = new AvaloniaList<Models.Remote>(remotes);
            _configuration.Remotes.Clear();
            _configuration.Remotes.AddRange(remotes.Select(r => r.Name));
            var names = _configuration.Remotes.ToList();
            var selected = _configuration.DefaultRemote;
            if (!string.IsNullOrEmpty(selected) && !names.Contains(selected)) names.Add(selected);
            _updatingRemoteChoices = true;
            try
            {
                _defaultRemote.ItemsSource = new[] { "自动选择" }.Concat(names).ToArray();
                _defaultRemote.SelectedIndex = Math.Max(0, names.IndexOf(selected) + 1);
            }
            finally { _updatingRemoteChoices = false; }
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
            var button = new Button { Content = caption, IsDefault = caption == "确定", Classes = { "flat" }, MinWidth = 70, Padding = new Thickness(10, 2) };
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

        private static CheckBox Option(string name, string caption, bool value, Action<bool> update)
        {
            var option = new CheckBox { Name = name, Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
            option.IsCheckedChanged += (_, _) => update(option.IsChecked == true);
            return option;
        }

        private static Control Optional(string title, Control content) => new Expander
        {
            Header = title, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new Border { Padding = new Thickness(6), Child = content },
        };

        private static Control Group(string title, Control content)
            => new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title }, new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#CED8DF")), CornerRadius = new CornerRadius(3), Padding = new Thickness(6), Child = content } } };

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
        public RepositoryForm(string title, string[] labels, string[] values, int multilineIndex = -1)
        {
            Title = title; Width = 510; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; CanResize = false; CloseOnESC = true;
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold });
            _fields = values.Select((v, i) => new TextBox { Text = v, Watermark = labels[i], Height = 28 }).ToArray();
            if (multilineIndex >= 0 && multilineIndex < _fields.Length)
            {
                _fields[multilineIndex].AcceptsReturn = true;
                _fields[multilineIndex].Height = 140;
                _fields[multilineIndex].TextWrapping = TextWrapping.Wrap;
                _fields[multilineIndex].VerticalContentAlignment = VerticalAlignment.Top;
            }
            if (values.Length == 0) panel.Children.Add(new TextBlock { Text = labels[0], TextWrapping = TextWrapping.Wrap });
            for (var i = 0; i < _fields.Length; i++) { panel.Children.Add(new TextBlock { Text = labels[i] }); panel.Children.Add(_fields[i]); }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(RepositorySettings.Action("确定", () => { Close(true); return Task.CompletedTask; }));
            buttons.Children.Add(RepositorySettings.Action("取消", () => { Close(false); return Task.CompletedTask; }));
            RepositoryDialogLayout.Apply(this, panel, buttons, 510);
        }
    }
}
