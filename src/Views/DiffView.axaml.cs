using System;
using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class DiffView : UserControl
    {
        public static readonly StyledProperty<ViewModels.IDiffViewSettings> SettingsProperty = AvaloniaProperty.Register<DiffView, ViewModels.IDiffViewSettings>(nameof(Settings));
        public ViewModels.IDiffViewSettings Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
        private ViewModels.IDiffViewSettings _observedSettings;
        private bool _settingsActive;

        public DiffView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => BindViewSettings();
        }

        private void BindViewSettings()
        {
            var settings = (DataContext as ViewModels.DiffContext)?.Settings ?? ViewModels.Preferences.Instance;
            Settings = settings;
            if (_observedSettings == settings && _settingsActive) return;
            if (_observedSettings != null) _observedSettings.PropertyChanged -= OnViewSettingsChanged;
            _observedSettings = _settingsActive ? settings : null;
            if (_observedSettings != null) _observedSettings.PropertyChanged += OnViewSettingsChanged;
        }

        private void OnViewSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(ViewModels.IDiffViewSettings.UseSideBySideDiff) or nameof(ViewModels.IDiffViewSettings.UseFullTextDiff) or
                nameof(ViewModels.IDiffViewSettings.UseSyntaxHighlighting) or nameof(ViewModels.IDiffViewSettings.EnableDiffViewWordWrap) or
                nameof(ViewModels.IDiffViewSettings.IgnoreWhitespaceChangesInDiff) or nameof(ViewModels.IDiffViewSettings.IgnoreCRAtEOLInDiff) or
                nameof(ViewModels.IDiffViewSettings.ShowHiddenSymbolsInDiffView))) return;
            (DataContext as ViewModels.DiffContext)?.CheckSettings();
            ViewModels.Preferences.Instance.Save();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _settingsActive = true;
            BindViewSettings();
            ViewModels.Preferences.Instance.PropertyChanged += OnRepositoryEncodingChanged;
            BtnOpenExternalMergeTool.IsVisible = TopLevel.GetTopLevel(this) is not InternalDiffWindow;
            App.LocaleChanged += OnLocaleChanged;
            OnLocaleChanged();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _settingsActive = false;
            ViewModels.Preferences.Instance.PropertyChanged -= OnRepositoryEncodingChanged;
            if (_observedSettings != null) _observedSettings.PropertyChanged -= OnViewSettingsChanged;
            _observedSettings = null;
            App.LocaleChanged -= OnLocaleChanged;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnLocaleChanged() => (DataContext as ViewModels.DiffContext)?.RefreshLocalizedText();

        private void OnRepositoryEncodingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.Preferences.RepositoryDisplayEncodings))
                (DataContext as ViewModels.DiffContext)?.RefreshDisplayEncoding();
        }

        public void ToggleHotkeyBindings(bool enabled)
        {
            var isMacOS = OperatingSystem.IsMacOS();
            if (enabled)
            {
                BtnGotoFirstChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Home" : "Ctrl+Alt+Home");
                BtnGotoPrevChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Up" : "Ctrl+Alt+Up");
                BtnGotoNextChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+Down" : "Ctrl+Alt+Down");
                BtnGotoLastChange.HotKey = KeyGesture.Parse(isMacOS ? "Cmd+Alt+End" : "Ctrl+Alt+End");
                BtnOpenExternalMergeTool.HotKey = BtnOpenExternalMergeTool.IsVisible ? KeyGesture.Parse(isMacOS ? "Cmd+Shift+D" : "Ctrl+Shift+D") : null;
            }
            else
            {
                BtnGotoFirstChange.HotKey = null;
                BtnGotoPrevChange.HotKey = null;
                BtnGotoNextChange.HotKey = null;
                BtnGotoLastChange.HotKey = null;
                BtnOpenExternalMergeTool.HotKey = null;
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();

            ToggleHotkeyBindings(IsEffectivelyVisible);
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            ToggleHotkeyBindings(false);
        }

        private void OnGotoFirstChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.First);
            e.Handled = true;
        }

        private void OnGotoPrevChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Prev);
            e.Handled = true;
        }

        private void OnGotoNextChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Next);
            e.Handled = true;
        }

        private void OnGotoLastChange(object _, RoutedEventArgs e)
        {
            this.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Last);
            e.Handled = true;
        }

        private void OnToggleButtonPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToggleButton.IsCheckedProperty && DataContext is ViewModels.DiffContext vm)
                vm.CheckSettings();
        }

        private void OnOpenSubmoduleRevisionCompare(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.SubmoduleDiff diff } && diff.CanOpenDetails)
            {
                var vm = new ViewModels.SubmoduleRevisionCompare(diff);
                this.ShowWindow(vm);
            }
        }

        private async void OnOpenBinaryFileViewer(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.BinaryDiff diff } && diff.NewSize > 0)
            {
                await this.ShowDialogAsync(new ViewModels.BinaryFileViewer(diff.Repository, diff.FilePath, diff.NewRevision));
                e.Handled = true;
            }
        }
    }
}
