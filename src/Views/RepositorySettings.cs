using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

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
        private bool _remoteReadFailed;
        private readonly TextBlock _error;
        private readonly Action<bool> _selectPage;
        private Window _screenOwner;
        private bool _advanced;
        public void SelectPage(bool advanced) => _selectPage(advanced);

        public void FitToOwner(Window owner)
        {
            _screenOwner = owner;
            FitToScreen(place: true);
        }

        private void FitToScreen(bool place = false)
        {
            if (_screenOwner == null) return;
            var screen = IsVisible ? Screens.ScreenFromWindow(this) : _screenOwner.Screens.ScreenFromWindow(_screenOwner);
            screen ??= _screenOwner.Screens.Primary;
            if (screen == null) return;
            var scale = IsVisible ? RenderScaling : screen.Scaling;
            var zoom = ViewModels.Preferences.Instance.Zoom;
            var ownerFrame = new PixelRect(_screenOwner.Position,
                PixelSize.FromSize(_screenOwner.FrameSize ?? _screenOwner.Bounds.Size, _screenOwner.RenderScaling));
            var area = OwnedMovementBounds ?? ownerFrame.Intersect(screen.WorkingArea);
            if (area.Width <= 0 || area.Height <= 0)
                area = screen.WorkingArea;

            const double advancedPageHeight = 580;
            var desiredHeight = (_advanced ? advancedPageHeight : 420) * zoom;
            if (place)
            {
                MaxWidth = Math.Max(1, (area.Width - 16) / scale);
                MaxHeight = Math.Max(1, (area.Height - 16) / scale);
                Width = Math.Min(640 * zoom, MaxWidth);
                Height = Math.Min(desiredHeight, MaxHeight);
                var advancedHeight = Math.Min(advancedPageHeight * zoom, MaxHeight);
                var x = area.X + (area.Width - Width * scale) / 2;
                var y = Math.Min(area.Y + (area.Height - Height * scale) / 2, area.Bottom - advancedHeight * scale - 8);
                Position = new PixelPoint((int)x, (int)Math.Max(area.Y + 8, y));
                return;
            }

            // Keep the current title-bar position, even after the dialog has been dragged.
            var frameInset = Math.Max(0, (FrameSize ?? Bounds.Size).Height - Bounds.Height);
            var available = Math.Floor((area.Bottom - Position.Y) / scale - frameInset);
            Height = Math.Min(desiredHeight, Math.Min(MaxHeight, Math.Max(1, available)));
        }

        public RepositorySettings(ViewModels.Repository repo)
        {
            _repo = repo;
            _configuration = new ViewModels.RepositoryConfigure(repo);
            App.BindText(this, TitleProperty, "TreeGit.Toolbar.Settings");
            Width = 640;
            SizeToContent = SizeToContent.Manual;
            CanResize = false;
            CloseOnESC = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var root = new Grid { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
            var title = DialogHeader.Create(this);
            title.Margin = new Thickness(-8, -8, -8, 8);
            root.Children.Add(title);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -1), ZIndex = 1 };
            var remoteTab = Tab("RepositoryRemoteTab", "TreeGit.Settings.Remotes", "Icons.Remotes");
            var advancedTab = Tab("RepositoryAdvancedTab", "TreeGit.Settings.Advanced", "Icons.Settings");
            tabs.Children.Add(remoteTab); tabs.Children.Add(advancedTab); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
            var border = new Border
            {
                [!BorderBrushProperty] = new DynamicResourceExtension("Brush.Border1"),
                [!BackgroundProperty] = new DynamicResourceExtension("Brush.Contents"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
            };
            var remotePage = new StackPanel { Spacing = 8 };
            _remotes = new DataGrid
            {
                Name = "RepositoryRemotes", Classes = { "repository_remotes" }, Height = 200, AutoGenerateColumns = false,
                IsReadOnly = true, CanUserResizeColumns = true, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 26, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#AEC8DF")),
                SelectionMode = DataGridSelectionMode.Single,
            };
            _remotes.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Remote.Column.Name") },
                CellStyleClasses = { "remote_name" },
                Binding = new Binding(nameof(Models.Remote.Name)), Width = new DataGridLength(150),
            });
            _remotes.ItemsSource = new AvaloniaList<Models.Remote>(repo.Remotes);
            _remotes.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Remote.Column.Address") },
                Binding = new Binding(nameof(Models.Remote.URL)) { Converter = Converters.PathConverters.RemoteAddress }, Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            });
            remotePage.Children.Add(_remotes);
            var remoteActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var add = Action("TreeGit.Add", async () => await EditRemoteAsync(null));
            var edit = Action("TreeGit.Edit", async () => { if (_remotes.SelectedItem is Models.Remote r) await EditRemoteAsync(r); });
            edit.Name = "EditRepositoryRemote";
            var remove = Action("TreeGit.Remove", RemoveRemoteAsync);
            edit.IsEnabled = remove.IsEnabled = false;
            _remotes.SelectionChanged += (_, _) => edit.IsEnabled = remove.IsEnabled = _remotes.SelectedItem != null;
            _remotes.DoubleTapped += async (_, _) => { if (_remotes.SelectedItem is Models.Remote r) await EditRemoteAsync(r); };
            remoteActions.Children.Add(add); remoteActions.Children.Add(edit); remoteActions.Children.Add(remove); remotePage.Children.Add(remoteActions);
            var credentials = Action("TreeGit.Credentials.Action", async () =>
                await new RepositoryCredentials(repo, (_remotes.SelectedItem as Models.Remote)?.Name).ShowDialog(this));
            credentials.Name = "RepositorySettingsCredentials";
            remoteActions.Children.Add(credentials);

            var advancedPage = new StackPanel { Name = "AdvancedRepositorySettings", Spacing = 6, Margin = new Thickness(0, 0, 18, 0) };
            var advancedLayout = new AdvancedFieldLayout();
            advancedLayout.Children.Add(advancedPage);
            Grid AddField(StackPanel group, string label, TemplatedControl editor)
            {
                var row = Field(label, editor);
                group.Children.Add(row);
                advancedLayout.TrackCaption(label, (TextBlock)row.Children[0], row.ColumnDefinitions[0]);
                return row;
            }
            var ignored = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var ignorePath = Path.Combine(repo.FullPath, ".gitignore");
            ignored.Children.Add(new TextBlock { Name = "RepositoryIgnorePath", Text = Native.OS.FormatLocalPath(ignorePath), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var ignoreEdit = Action("TreeGit.Edit", () => { if (!File.Exists(ignorePath)) File.WriteAllText(ignorePath, ""); Native.OS.OpenWithDefaultEditor(ignorePath); return Task.CompletedTask; });
            Grid.SetColumn(ignoreEdit, 1); ignored.Children.Add(ignoreEdit); advancedPage.Children.Add(Group("TreeGit.Settings.Ignore", ignored));
            var identity = new StackPanel { Spacing = 4 };
            _global = new CheckBox { Name = "UseGlobalIdentity", [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Settings.GlobalIdentity"), IsChecked = _configuration.UseGlobalIdentity };
            identity.Children.Add(_global);
            _name = new TextBox { Name = "CommitterName", Text = _configuration.UserName };
            _email = new TextBox { Name = "CommitterEmail", Text = _configuration.UserEmail };
            var nameRow = AddField(identity, "TreeGit.Settings.FullName", _name);
            var emailRow = AddField(identity, "TreeGit.Settings.Email", _email);
            void EnableIdentity() => nameRow.IsEnabled = emailRow.IsEnabled = _global.IsChecked != true;
            _global.IsCheckedChanged += (_, _) => EnableIdentity(); EnableIdentity();
            advancedPage.Children.Add(Group("TreeGit.Settings.Identity", identity, horizontalPadding: 22));
            var links = new StackPanel { Spacing = 6 };
            _links = new ListBox { Height = 64, ItemsSource = _configuration.IssueTrackers, DisplayMemberBinding = new Binding("Name"), Background = this.FindResource("Brush.Contents") as IBrush, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
            links.Children.Add(_links);
            var linkActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            linkActions.Children.Add(Action("TreeGit.Add", async () => await EditLinkAsync(null)));
            linkActions.Children.Add(Action("TreeGit.Edit", async () => { if (_links.SelectedItem is Models.IssueTracker tracker) await EditLinkAsync(tracker); }));
            linkActions.Children.Add(Action("TreeGit.Delete", () => { if (_links.SelectedItem is Models.IssueTracker tracker) _configuration.IssueTrackers.Remove(tracker); return Task.CompletedTask; }));
            links.Children.Add(linkActions); advancedPage.Children.Add(Optional("TreeGit.Settings.CommitLinks", links));
            var sync = new StackPanel { Spacing = 4 };
            _defaultRemote = new ComboBox
            {
                Name = "DefaultRemote", Padding = new Thickness(8, 0),
                ItemTemplate = new FuncDataTemplate<string>((remote, _) => remote == string.Empty
                    ? new TextBlock { [!TextBlock.TextProperty] = App.TextBinding("TreeGit.Settings.Automatic") }
                    : new TextBlock { Text = remote }),
            };
            UpdateDefaultRemoteChoices();
            _defaultRemote.SelectionChanged += (_, _) =>
            {
                if (!_updatingRemoteChoices && _defaultRemote.SelectedItem is string name)
                    _configuration.DefaultRemote = name;
            };
            AddField(sync, "TreeGit.Settings.DefaultRemote", _defaultRemote);
            var merge = new ComboBox { Name = "MergeMode", ItemsSource = Models.MergeMode.Supported,
                ItemTemplate = new FuncDataTemplate<Models.MergeMode>((mode, _) => new TextBlock { [!TextBlock.TextProperty] = App.TextBinding(mode.NameResourceKey) }),
                Padding = new Thickness(8, 0), SelectedIndex = Math.Clamp(_configuration.PreferredMergeMode, 0, Models.MergeMode.Supported.Length - 1) };
            merge.SelectionChanged += (_, _) => _configuration.PreferredMergeMode = merge.SelectedIndex;
            AddField(sync, "TreeGit.Settings.MergeMode", merge);
            var proxy = new TextBox { Name = "HttpProxy", Text = _configuration.HttpProxy, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Settings.ProxyHint") };
            proxy.TextChanged += (_, _) => _configuration.HttpProxy = proxy.Text ?? string.Empty;
            AddField(sync, "TreeGit.Settings.HttpProxy", proxy);
            _autoRefresh = Option("AutoRefresh", "TreeGit.Settings.AutoRefresh", _configuration.AutoRefreshEnabled, value => _configuration.AutoRefreshEnabled = value);
            _backgroundFetch = Option("BackgroundFetch", "TreeGit.Settings.BackgroundFetch", _configuration.BackgroundFetchEnabled, value => _configuration.BackgroundFetchEnabled = value);
            sync.Children.Add(new StackPanel
            {
                Children =
                {
                    Option("PruneOnFetch", "TreeGit.Settings.Prune", _configuration.EnablePruneOnFetch, value => _configuration.EnablePruneOnFetch = value),
                    _autoRefresh, _backgroundFetch,
                },
            });
            advancedPage.Children.Add(Optional("TreeGit.Settings.SyncMerge", sync, endsWithOptions: true));
            var signing = new StackPanel { Spacing = 4 };
            var signingKey = new TextBox { Name = "SigningKey", Text = _configuration.GPGUserSigningKey, [!Avalonia.Controls.TextBox.WatermarkProperty] = App.TextBinding("TreeGit.Settings.SigningKeyHint") };
            signingKey.TextChanged += (_, _) => _configuration.GPGUserSigningKey = signingKey.Text ?? string.Empty;
            AddField(signing, "TreeGit.Settings.SigningKey", signingKey);
            signing.Children.Add(new StackPanel
            {
                Children =
                {
                    Option("SignCommits", "TreeGit.Settings.SignCommits", _configuration.GPGCommitSigningEnabled, value => _configuration.GPGCommitSigningEnabled = value),
                    Option("SignTags", "TreeGit.Settings.SignTags", _configuration.GPGTagSigningEnabled, value => _configuration.GPGTagSigningEnabled = value),
                },
            });
            advancedPage.Children.Add(Optional("TreeGit.Settings.Signing", signing, endsWithOptions: true));
            var submodules = new StackPanel();
            submodules.Children.Add(Option("AskSubmodules", "TreeGit.Settings.ConfirmSubmodule", _configuration.AskBeforeAutoUpdatingSubmodules, value => _configuration.AskBeforeAutoUpdatingSubmodules = value));
            submodules.Children.Add(Option("RecursiveSubmodules", "TreeGit.Settings.RecursiveSubmodule", _configuration.EnableRecursiveWhenAutoUpdatingSubmodules, value => _configuration.EnableRecursiveWhenAutoUpdatingSubmodules = value));
            advancedPage.Children.Add(Optional("TreeGit.Settings.SubmoduleUpdate", submodules, endsWithOptions: true));
            // Preserve existing template data, while removing its orphaned editor:
            // the current commit page uses branch history rather than this picker.
            var scroll = new ScrollViewer { Name = "RepositorySettingsScroll", Content = remotePage, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            KeepDropdownAnchor(_defaultRemote);
            KeepDropdownAnchor(merge);
            border.Child = scroll; Grid.SetRow(border, 2); root.Children.Add(border);
            void Select(bool advanced)
            {
                _advanced = advanced;
                scroll.Content = advanced ? advancedLayout : remotePage;
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
            footer.Children.Add(Action("TreeGit.Settings.EditConfig", async () =>
            {
                var directory = await new Commands.QueryGitCommonDir(repo.FullPath).GetResultAsync();
                if (string.IsNullOrEmpty(directory))
                {
                    App.BindText(_error, TextBlock.TextProperty, "TreeGit.Config.ReadFailed");
                    _error.IsVisible = true;
                    return;
                }
                Native.OS.OpenWithDefaultEditor(Path.Combine(directory, "config"));
            }));
            var ok = Action("TreeGit.Ok", SaveAsync); ok.Name = "ConfirmRepositorySettings"; Grid.SetColumn(ok, 2); footer.Children.Add(ok);
            var cancel = new Button { [!Avalonia.Controls.ContentControl.ContentProperty] = App.TextBinding("TreeGit.Cancel"), Classes = { "flat" }, MinWidth = 74, Margin = new Thickness(6, 0, 0, 0) }; cancel.Click += (_, _) => Close(); Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
            title.Children.OfType<CaptionButtons>().Single().Bind(Control.IsEnabledProperty, cancel.GetObservable(Control.IsEnabledProperty));
            Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
            if (!_configuration.ReadSucceeded)
            {
                border.IsEnabled = false;
                ok.IsEnabled = false;
                App.BindText(_error, TextBlock.TextProperty, "TreeGit.Config.ReadFailed");
                _error.IsVisible = true;
            }
            Opened += async (_, _) =>
            {
                await RefreshRemotesAsync();
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
            var query = new Commands.QueryRemotes(_repo.FullPath);
            var remotes = await query.GetResultAsync();
            if (!query.Succeeded)
            {
                // Keep the last known list and all unsaved settings. A failed
                // read is not evidence that the repository has no remotes.
                App.BindText(_error, TextBlock.TextProperty, "TreeGit.Config.ReadFailed");
                _error.IsVisible = true;
                _remoteReadFailed = true;
                if (_remotes.Parent is Control remotePage) remotePage.IsEnabled = false;
                return;
            }
            if (_remotes.Parent is Control remoteContainer) remoteContainer.IsEnabled = _configuration.ReadSucceeded;
            if (_remoteReadFailed && _configuration.ReadSucceeded) _error.IsVisible = false;
            _remoteReadFailed = false;
            var selectedRemoteName = (_remotes.SelectedItem as Models.Remote)?.Name;
            _remotes.ItemsSource = new AvaloniaList<Models.Remote>(remotes);
            if (selectedRemoteName != null)
                _remotes.SelectedItem = remotes.FirstOrDefault(r => r.Name == selectedRemoteName);
            _configuration.Remotes.Clear();
            _configuration.Remotes.AddRange(remotes.Select(r => r.Name));
            UpdateDefaultRemoteChoices();
        }

        private void UpdateDefaultRemoteChoices()
        {
            var selected = _configuration.DefaultRemote ?? string.Empty;
            var names = new List<string> { string.Empty };
            names.AddRange(_configuration.Remotes);
            if (!names.Contains(selected)) names.Add(selected);
            _updatingRemoteChoices = true;
            try
            {
                if (!_defaultRemote.Items.SequenceEqual(names))
                    _defaultRemote.ItemsSource = names;
                _defaultRemote.SelectedItem = selected;
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
            var confirm = new RepositoryForm(App.Text("TreeGit.Remote.Remove"), [App.Text("TreeGit.Remote.RemovePrefix") + remote.Name + App.Text("TreeGit.Remote.RemoveSuffix")], []);
            if (!await confirm.ShowDialog<bool>(this)) return;
            if (await new Commands.Remote(_repo.FullPath).DeleteAsync(remote.Name.Quoted())) { await RefreshRemotesAsync(); _repo.RefreshAll(); }
        }

        private async Task EditLinkAsync(Models.IssueTracker tracker)
        {
            var form = new RepositoryForm(tracker == null ? App.Text("TreeGit.Links.Add") : App.Text("TreeGit.Links.Edit"), [App.Text("TreeGit.Settings.Name"), App.Text("TreeGit.Links.Pattern"), App.Text("TreeGit.Links.UrlTemplate")], [tracker?.Name ?? "", tracker?.RegexString ?? @"#(\d+)", tracker?.URLTemplate ?? "https://example.com/issues/$1"]);
            if (!await form.ShowDialog<bool>(this)) return;
            if (tracker == null) _configuration.AddIssueTracker(form.Values[0], form.Values[1], form.Values[2]);
            else { tracker.Name = form.Values[0]; tracker.RegexString = form.Values[1]; tracker.URLTemplate = form.Values[2]; }
            _links.ItemsSource = null; _links.ItemsSource = _configuration.IssueTrackers;
        }

        internal static Button Action(string caption, Func<Task> action)
        {
            var button = new Button { [!ContentControl.ContentProperty] = App.TextBinding(caption), IsDefault = caption == "TreeGit.Ok", Classes = { "flat" }, MinWidth = 70, Padding = new Thickness(10, 2) };
            button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception error) { await new RepositoryForm(App.Text("TreeGit.OperationIncomplete"), [error.Message], []).ShowDialog(TopLevel.GetTopLevel(button) as Window); } finally { button.IsEnabled = true; } };
            return button;
        }

        private static void SetTabAppearance(Button button, bool selected)
        {
            button.Classes.Set("selected", selected);
            var content = (StackPanel)button.Content;
            ((TextBlock)content.Children[1]).FontWeight = selected ? FontWeight.Bold : FontWeight.Normal;
        }

        private static Button Tab(string name, string caption, string icon)
        {
            var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 22, Height = 22, Data = App.Current.FindResource(icon) as Geometry, HorizontalAlignment = HorizontalAlignment.Center,
                [!Avalonia.Controls.Shapes.Path.FillProperty] = new DynamicResourceExtension("Brush.FG2"),
            });
            content.Children.Add(new TextBlock
            {
                [!TextBlock.TextProperty] = App.TextBinding(caption),
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Brush.FG1"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            // Keep the selected page visible while the pointer is over a tab, too.
            // The generic button theme otherwise replaces its background on hover.
            return new Button
            {
                Name = name, Content = content, MinWidth = 84, MaxWidth = 180, Height = 58,
                Classes = { "repository_config_tab" },
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
            var option = new CheckBox { Name = name, Content = new TextBlock { [!TextBlock.TextProperty] = App.TextBinding(caption), TextWrapping = TextWrapping.Wrap }, IsChecked = value };
            option.IsCheckedChanged += (_, _) => update(option.IsChecked == true);
            return option;
        }

        private static Control Optional(string title, StackPanel content, bool endsWithOptions = false) => new Expander
        {
            Name = title.Replace(".", string.Empty), Classes = { "settings_expander" }, [!Expander.HeaderProperty] = App.TextBinding(title), IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            // Preserve the visible bottom gap after counting the checkbox hit area.
            Padding = new Thickness(16, 9.5, 16, endsWithOptions ? 2.1 : 8.5),
            UseLayoutRounding = false,
            Content = new Border { Padding = new Thickness(6, 0), UseLayoutRounding = true, Child = content },
        };

        private static Control Group(string title, Control content, double horizontalPadding = 6)
            => new StackPanel { Spacing = 2, Children = { new TextBlock { [!TextBlock.TextProperty] = App.TextBinding(title) }, new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#CED8DF")), CornerRadius = new CornerRadius(3), Padding = new Thickness(horizontalPadding, 6), Child = content } } };

        private static Grid Field(string label, TemplatedControl editor)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            var caption = RepositoryDialogLayout.FieldLabel(label);
            caption.Name = editor.Name + "Label";
            caption.Classes.Add("repository_settings_caption");
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            editor.Height = 26;
            editor.CornerRadius = new CornerRadius(3);
            row.Children.Add(caption);
            Grid.SetColumn(editor, 1); row.Children.Add(editor); return row;
        }

        private sealed class AdvancedFieldLayout : Grid
        {
            private readonly List<(string Label, TextBlock Caption, ColumnDefinition Column)> _fields = [];

            public void TrackCaption(string label, TextBlock caption, ColumnDefinition column) => _fields.Add((label, caption, column));

            protected override Size MeasureOverride(Size availableSize)
            {
                var width = 0.0;
                // Collapsed sections still contribute their localized captions to the column.
                foreach (var (label, caption, _) in _fields)
                {
                    var typeface = new Typeface(caption.FontFamily, caption.FontStyle, caption.FontWeight, caption.FontStretch);
                    var text = new FormattedText(App.Text(label), CultureInfo.CurrentUICulture, caption.FlowDirection,
                        typeface, caption.FontSize, Brushes.Black);
                    width = Math.Max(width, text.WidthIncludingTrailingWhitespace);
                }

                var columnWidth = new GridLength(Math.Ceiling(width));
                foreach (var (_, _, column) in _fields)
                    column.Width = columnWidth;

                return base.MeasureOverride(availableSize);
            }
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
            buttons.Children.Add(RepositorySettings.Action("TreeGit.Ok", () => { Close(true); return Task.CompletedTask; }));
            buttons.Children.Add(RepositorySettings.Action("TreeGit.Cancel", () => { Close(false); return Task.CompletedTask; }));
            RepositoryDialogLayout.Apply(this, panel, buttons, 510);
        }
    }
}
