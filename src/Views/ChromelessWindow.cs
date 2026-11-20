using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class ChromelessWindow : Window
    {
        public bool UseSystemWindowFrame
        {
            get => Native.OS.UseSystemWindowFrame;
        }

        public bool CloseOnESC
        {
            get;
            set;
        } = false;

        protected override Type StyleKeyOverride => typeof(Window);

        public ChromelessWindow()
        {
            if (this is not Views.Launcher) Classes.Add("dialog_selections");
            Focusable = true;
            Native.OS.SetupForWindow(this);
            InitializeOwnedWindowDragging();
            PositionChanged += (_, _) => UpdateEdgeSnapInsets();
            Resized += (_, _) =>
            {
                UpdateEdgeSnapInsets();
                ScheduleSecondaryWindowPosition();
            };
        }

        public void BeginMoveWindow(object _, PointerPressedEventArgs e)
        {
            if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (BeginOwnedWindowDrag(e)) return;
                BeginMoveDrag(e);
            }

            e.Handled = true;
        }

        public new void Activate()
        {
#if INTEGRATION_TESTS
            if (OperatingSystem.IsWindows() && Automation.LocalAuditHost.IsEnabled) return;
#endif
            base.Activate();
        }

        public void MaximizeOrRestoreWindow(object _, TappedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;

            e.Handled = true;
        }

        protected static void KeepDropdownAnchor(ComboBox dropdown)
        {
            var keyboardFocus = false;
            dropdown.GotFocus += (_, args) => keyboardFocus = args.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
            // Handle the focus request before the surrounding scroll viewer moves the open dropdown.
            dropdown.AddHandler(Control.RequestBringIntoViewEvent, (_, args) =>
            {
                if (dropdown.IsDropDownOpen || !keyboardFocus) args.Handled = true;
            }, RoutingStrategies.Bubble);
        }

        protected override void OnOpened(EventArgs e)
        {
            DialogButtons.Apply(this);
            base.OnOpened(e);
            Dispatcher.UIThread.Post(() => { if (IsVisible) DialogButtons.Apply(this); }, DispatcherPriority.Loaded);
            AttachMovementOwner();
            if (!CanResize || this is Launcher) return;
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            // Reserve room for native captions/borders as well as custom frames.
            var width = Math.Max(1, area.Width / scale - 32);
            var height = Math.Max(1, area.Height / scale - 64);
            MinWidth = Math.Min(MinWidth, width);
            MinHeight = Math.Min(MinHeight, height);
            if (Bounds.Width > width) Width = width;
            if (Bounds.Height > height) Height = height;
            ScheduleSecondaryWindowPosition();
        }

        private bool _secondaryPositionPending;
        private OverlayLayer _popupLayer;
        private Border _windowSurface;

        internal void RepaintPopupBackground() => _windowSurface?.InvalidateVisual();

        private void OnOverlayChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Repaint the underlying surface when an overlay is removed. Its
            // antialiased edge can extend beyond the last layout rectangle.
            if (e.OldItems != null || e.Action == NotifyCollectionChangedAction.Reset)
                RepaintPopupBackground();
        }

        private void ScheduleSecondaryWindowPosition()
        {
            if (!CanResize || this is Launcher || _secondaryPositionPending) return;
            _secondaryPositionPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _secondaryPositionPending = false;
                if (!IsVisible || WindowState != WindowState.Normal) return;
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen == null) return;
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                Position = new PixelPoint(
                    (int)Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - Bounds.Width * scale)),
                    (int)Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - Bounds.Height * scale)));
            }, DispatcherPriority.Background);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            if (_popupLayer != null) _popupLayer.Children.CollectionChanged -= OnOverlayChildrenChanged;
            _windowSurface = e.NameScope.Find<Border>("PART_ContentRoot");
            var presenter = e.NameScope.Find<Control>("PART_ContentPresenter");
            _popupLayer = presenter == null ? null : OverlayLayer.GetOverlayLayer(presenter);
            if (_popupLayer != null) _popupLayer.Children.CollectionChanged += OnOverlayChildrenChanged;

            if (Classes.Contains("custom_window_frame") && CanResize)
            {
                string[] borderNames = [
                    "PART_BorderTopLeft",
                    "PART_BorderTop",
                    "PART_BorderTopRight",
                    "PART_BorderLeft",
                    "PART_BorderRight",
                    "PART_BorderBottomLeft",
                    "PART_BorderBottom",
                    "PART_BorderBottomRight",
                ];

                foreach (var name in borderNames)
                {
                    var border = e.NameScope.Find<Border>(name);
                    if (border != null)
                    {
                        border.PointerPressed -= OnWindowBorderPointerPressed;
                        border.PointerPressed += OnWindowBorderPointerPressed;
                    }
                }
            }
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (OperatingSystem.IsWindows() && this is not OwnedNativeWindow)
                Native.Win64Utilities.FixWindowFrame(this);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            UpdateOwnedWindowBounds(change);
            base.OnPropertyChanged(change);

            if (OperatingSystem.IsWindows() && this is not OwnedNativeWindow &&
                (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty))
                Native.Win64Utilities.FixWindowFrame(this);

            if (change.Property == WindowStateProperty)
                UpdateEdgeSnapInsets();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled)
                return;

            if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && CloseOnESC)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                if (e.Key == Key.OemPlus)
                {
                    var zoom = Math.Min(ViewModels.Preferences.Instance.Zoom + 0.05, 2.5);
                    ViewModels.Preferences.Instance.Zoom = zoom;
                    e.Handled = true;
                }
                else if (e.Key == Key.OemMinus)
                {
                    var zoom = Math.Max(ViewModels.Preferences.Instance.Zoom - 0.05, 1);
                    ViewModels.Preferences.Instance.Zoom = zoom;
                    e.Handled = true;
                }
                else if (e.Key == Key.W)
                {
                    Close();
                    e.Handled = true;
                }
            }
        }

        private void OnWindowBorderPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (sender is Border { Tag: WindowEdge edge } && CanResize)
                BeginResizeDrag(edge, e);
        }

        // The transparent padding around the window holds the self-drawn shadow
        // (keep the value in sync with "Window.custom_window_frame" style).
        // Collapse it on the side touching a screen edge so the content sits flush.
        private void UpdateEdgeSnapInsets()
        {
            if (!Classes.Contains("custom_window_frame"))
                return;

            if (WindowState != WindowState.Normal)
            {
                // Let the "WindowState=Maximized" style take over
                ClearValue(PaddingProperty);
                return;
            }

            var screen = Screens.ScreenFromWindow(this);
            if (screen == null)
                return;

            var workArea = screen.WorkingArea;
            var right = Position.X + Bounds.Width;
            var bottom = Position.Y + Bounds.Height;

            const double tolerance = 2.0;
            var snapTop = Position.Y <= workArea.Y + tolerance;
            var snapLeft = Position.X <= workArea.X + tolerance;
            var snapRight = right >= workArea.Right - tolerance;
            var snapBottom = bottom >= workArea.Bottom - tolerance;

            if (!snapTop && !snapLeft && !snapRight && !snapBottom)
            {
                ClearValue(PaddingProperty);
                return;
            }

            Padding = new Thickness(
                snapLeft ? 0 : 12,
                snapTop ? 0 : 12,
                snapRight ? 0 : 12,
                snapBottom ? 0 : 12);
        }
    }
}
