using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class Launcher
    {
        private int _languagePreviewRevision;
        private Control _languagePopupRoot;
        private Popup _languagePopup;

        private void OnTreeGitLanguageChanged(object sender, RoutedEventArgs e)
        {
            _languagePreviewRevision++;
            if (sender is MenuItem { Name: { } name } && name.StartsWith("Language_", System.StringComparison.Ordinal))
            {
                var key = name["Language_".Length..];
                var preferences = ViewModels.Preferences.Instance;
                var previous = preferences.Locale;
                try
                {
                    preferences.Locale = key;
                    App.SetLocale(key);
                    preferences.Save(requireSuccess: true);
                }
                catch (System.Exception error)
                {
                    preferences.Locale = previous;
                    App.SetLocale(previous);
                    Native.OS.LogException(error);
                    Models.Notification.Send(string.Empty, App.Text("TreeGit.Language.SaveFailed"), true);
                }
                PrimaryMenu.Close();
            }
            e.Handled = true;
        }

        private void OnLanguageMenuPointerEntered(object sender, PointerEventArgs e)
        {
            RestoreLanguagePreview();
            PositionLanguagePopup();
            LanguageMenu.IsSubMenuOpen = true;
        }

        private void OnLanguageOptionPointerEntered(object sender, PointerEventArgs e)
        {
            if (LanguageMenu.IsSubMenuOpen && sender is MenuItem { Name: { } name } && name.StartsWith("Language_", System.StringComparison.Ordinal))
            {
                _languagePreviewRevision++;
                App.SetLocale(name["Language_".Length..]);
            }
        }

        private void RestoreLanguagePreview(bool afterClose = false)
        {
            var revision = ++_languagePreviewRevision;
            // Replacing localized header content while Avalonia recursively
            // closes its menu children invalidates that enumeration.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (revision == _languagePreviewRevision &&
                    (!afterClose || !PrimaryMenu.IsOpen || !LanguageMenu.IsSubMenuOpen))
                    App.SetLocale(ViewModels.Preferences.Instance.Locale);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }

        public static readonly Avalonia.StyledProperty<bool> IsRepositorySettingsOpenProperty = Avalonia.AvaloniaProperty.Register<Launcher, bool>(nameof(IsRepositorySettingsOpen));
        public bool IsRepositorySettingsOpen { get => GetValue(IsRepositorySettingsOpenProperty); set => SetValue(IsRepositorySettingsOpenProperty, value); }
        public async System.Threading.Tasks.Task OpenRepositorySettingsAsync(ViewModels.Repository repo)
        {
            IsRepositorySettingsOpen = true;
            try
            {
                var settings = new RepositorySettings(repo);
                settings.FitToOwner(this);
                await settings.ShowDialog(this);
            }
            finally { IsRepositorySettingsOpen = false; }
        }

        private void InitializeTitleChrome()
        {
            TitleChrome.AddHandler(PointerPressedEvent, OnTitleChromePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            LanguageMenu.TemplateApplied += (_, args) =>
            {
                if (_languagePopupRoot != null)
                    _languagePopupRoot.PointerExited -= OnLanguagePopupPointerExited;
                var popup = args.NameScope.Find<Popup>("PART_Popup");
                _languagePopup = popup;
                if (popup != null)
                {
                    popup.ShouldUseOverlayLayer = true;
                    popup.PlacementTarget = this;
                    popup.Placement = PlacementMode.RightEdgeAlignedTop;
                    popup.HorizontalOffset = 0;
                    popup.VerticalOffset = 0;
                }
                _languagePopupRoot = popup?.Child;
                if (_languagePopupRoot != null)
                    _languagePopupRoot.PointerExited += OnLanguagePopupPointerExited;
            };
            Deactivated += (_, _) => ClosePrimaryMenus();
            Closing += (_, _) => ClosePrimaryMenus();
            LanguageMenu.LayoutUpdated += (_, _) =>
            {
                if (LanguageMenu.IsSubMenuOpen) PositionLanguagePopup();
            };
            LanguageMenu.PropertyChanged += (_, change) =>
            {
                if (change.Property == MenuItem.IsSubMenuOpenProperty)
                {
                    if (LanguageMenu.IsSubMenuOpen) PositionLanguagePopup();
                    else RestoreLanguagePreview(afterClose: true);
                }
            };
            PrimaryMenu.PropertyChanged += (_, change) =>
            {
                if (change.Property == MenuBase.IsOpenProperty && !PrimaryMenu.IsOpen)
                    RestoreLanguagePreview(afterClose: true);
            };
        }

        private void PositionLanguagePopup()
        {
            // Nested overlay hosts have their own visual origin. Anchor to the
            // launcher coordinates so the submenu remains beside its row.
            if (_languagePopup != null && LanguageMenu.TranslatePoint(default, this) is { } point)
            {
                var rect = new Rect(point, LanguageMenu.Bounds.Size);
                if (_languagePopup.PlacementRect != rect) _languagePopup.PlacementRect = rect;
            }
        }

        private void OnLanguagePopupPointerExited(object sender, PointerEventArgs e)
        {
            if (_languagePopupRoot?.IsPointerOver != true)
                RestoreLanguagePreview();
        }

        private void ClosePrimaryMenus()
        {
            PrimaryMenu?.Close();
            RestoreLanguagePreview(afterClose: true);
        }

        private void OnPrimaryMenuHeaderPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.Source is not Avalonia.Visual source)
                return;

            var header = source as MenuItem ?? source.FindAncestorOfType<MenuItem>();
            if (header != null)
            {
                if (header.Parent == PrimaryMenu && header.IsSubMenuOpen && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    ClosePrimaryMenus();
                    e.Handled = true;
                }
                return;
            }

            if (PrimaryMenu?.IsOpen == true && !PrimaryMenu.GetVisualDescendants().OfType<Popup>()
                .Any(popup => popup.IsOpen && popup.Child != null && (popup.Child == source || popup.Child.IsVisualAncestorOf(source))))
                ClosePrimaryMenus();
        }

        private void OnTitleChromePointerPressed(object sender, PointerPressedEventArgs e)
        {
            // The entire lower row belongs to tabs, including its empty space.
            // Only the menu/caption row can move or maximize the window.
            if (e.GetPosition(TitleChrome).Y >= TitleChrome.RowDefinitions[0].ActualHeight - .1) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            for (var control = e.Source as Control; control != null && control != TitleChrome; control = control.GetVisualParent() as Control)
                if (control is Button or MenuItem or ListBoxItem or ScrollBar or TextBox)
                    return;
            if (e.ClickCount >= 2)
            {
                ToggleMaximizeRestore();
                e.Handled = true;
            }
            else
                BeginMoveWindow(sender, e);
        }

        private void OnTabMaskPointerPressed(object sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnTreeGitExtraAction(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is MenuItem { Name: { } name } && name.StartsWith("GitAction_", System.StringComparison.Ordinal) && TreeGitRepository is { } repo)
                this.GetVisualDescendants().OfType<RepositoryToolbar>().FirstOrDefault(v => v.DataContext == repo)?.ExecuteMenuAction(name["GitAction_".Length..], TitleChrome);
        }

        public void ToggleMaximizeRestore()
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
