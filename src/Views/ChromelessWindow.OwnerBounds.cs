using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class ChromelessWindow
    {
        private Window _movementOwner;
        private bool _restrictingOwnedWindow;
        private bool _ownerClampPending;
        private double _requestedMaxWidth, _requestedMaxHeight, _requestedMinWidth, _requestedMinHeight;
        private bool _movementConstraintsCaptured;
        private PixelPoint _lastOwnerPosition;
        private IPointer _windowDragPointer;
        private PixelPoint _windowDragStart, _windowDragPointerStart;
        private Func<PixelRect?> _externalMovementBounds;
        private DispatcherTimer _externalOwnerTimer;
        private Rect? _ownedRestoreRectangle;

        internal void SetExternalMovementBounds(Func<PixelRect?> bounds) => _externalMovementBounds = bounds;

        internal PixelRect? OwnedMovementBounds
        {
            get
            {
                if (_movementOwner?.WindowState == WindowState.Minimized) return null;
                PixelRect? bounds;
                if (_movementOwner?.IsVisible != true) bounds = _externalMovementBounds?.Invoke();
                else
                {
                    var size = _movementOwner.FrameSize ?? _movementOwner.Bounds.Size;
                    bounds = new PixelRect(_movementOwner.Position, PixelSize.FromSize(size, _movementOwner.RenderScaling));
                }
                if (bounds is not { } area) return null;
                // A parent can extend beyond the working area, especially with
                // scaling or while being moved. Its child must remain reachable.
                var screen = _movementOwner?.Screens.ScreenFromWindow(_movementOwner) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen == null) return area;
                var visible = screen.WorkingArea;
                var left = Math.Max(area.X, visible.X); var top = Math.Max(area.Y, visible.Y);
                var right = Math.Min(area.Right, visible.Right); var bottom = Math.Min(area.Bottom, visible.Bottom);
                return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : area;
            }
        }

        private void InitializeOwnedWindowDragging()
        {
            if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(this, OwnedWindowProc);
            PositionChanged += (_, _) => RestrictToMovementOwner();
            SizeChanged += (_, _) => ScheduleOwnerClamp();
            AddHandler(PointerMovedEvent, OnOwnedWindowPointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnOwnedWindowPointerReleased, RoutingStrategies.Tunnel);
            PointerCaptureLost += (_, _) => EndOwnedWindowDrag();
            Closed += (_, _) =>
            {
                if (OperatingSystem.IsWindows()) Win32Properties.RemoveWndProcHookCallback(this, OwnedWindowProc);
                EndOwnedWindowDrag();
                DetachMovementOwner();
                _externalMovementBounds = null;
            };
        }

        private void AttachMovementOwner()
        {
            if (this is Launcher) return;
            DetachMovementOwner();
            var owner = Owner as Window;
            if (owner == null && App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime && lifetime.MainWindow is Launcher launcher)
                owner = launcher;
            if (owner == this || owner == null && _externalMovementBounds == null) return;
            _movementOwner = owner;
            if (!_movementConstraintsCaptured)
            {
                _requestedMaxWidth = MaxWidth; _requestedMaxHeight = MaxHeight;
                _requestedMinWidth = MinWidth; _requestedMinHeight = MinHeight;
                _movementConstraintsCaptured = true;
            }
            if (owner != null)
            {
                _lastOwnerPosition = owner.Position;
                owner.PositionChanged += OnMovementOwnerPositionChanged;
                owner.SizeChanged += OnMovementOwnerSizeChanged;
                owner.PropertyChanged += OnMovementOwnerPropertyChanged;
            }
            else
            {
                _externalOwnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _externalOwnerTimer.Tick += (_, _) => RestrictToMovementOwner();
                _externalOwnerTimer.Start();
            }
            RestrictToMovementOwner();
            ScheduleOwnerClamp();
        }

        private void DetachMovementOwner()
        {
            if (_movementOwner != null)
            {
                _movementOwner.PositionChanged -= OnMovementOwnerPositionChanged;
                _movementOwner.SizeChanged -= OnMovementOwnerSizeChanged;
                _movementOwner.PropertyChanged -= OnMovementOwnerPropertyChanged;
            }
            _movementOwner = null;
            _externalOwnerTimer?.Stop();
            _externalOwnerTimer = null;
        }

        private void OnMovementOwnerPositionChanged(object sender, PixelPointEventArgs e)
        {
            var previous = _lastOwnerPosition;
            _lastOwnerPosition = e.Point;
            Position = new PixelPoint(Position.X + e.Point.X - previous.X, Position.Y + e.Point.Y - previous.Y);
            RestrictToMovementOwner();
        }

        private void OnMovementOwnerSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleOwnerClamp();

        private void OnMovementOwnerPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == BoundsProperty || e.Property == WindowStateProperty || e.Property.Name == "RenderScaling") ScheduleOwnerClamp();
        }

        private void UpdateOwnedWindowBounds(AvaloniaPropertyChangedEventArgs change)
        {
            if (_movementOwner == null && _externalMovementBounds == null || _restrictingOwnedWindow) return;
            if (change.Property == WindowStateProperty)
            {
                if (change.GetOldValue<WindowState>() == WindowState.Normal && change.GetNewValue<WindowState>() == WindowState.Maximized)
                    _ownedRestoreRectangle = new Rect(Position.X, Position.Y, Bounds.Width, Bounds.Height);
                else if (change.GetOldValue<WindowState>() == WindowState.Maximized && change.GetNewValue<WindowState>() == WindowState.Normal && _ownedRestoreRectangle is { } restore)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsVisible || WindowState != WindowState.Normal) return;
                        Width = Math.Min(restore.Width, MaxWidth); Height = Math.Min(restore.Height, MaxHeight);
                        Position = new PixelPoint((int)restore.X, (int)restore.Y);
                        ScheduleOwnerClamp();
                    }, DispatcherPriority.Loaded);
            }
            if (change.Property == MaxWidthProperty) _requestedMaxWidth = MaxWidth;
            else if (change.Property == MaxHeightProperty) _requestedMaxHeight = MaxHeight;
            else if (change.Property == MinWidthProperty) _requestedMinWidth = MinWidth;
            else if (change.Property == MinHeightProperty) _requestedMinHeight = MinHeight;
            else if (change.Property != WindowStateProperty && change.Property.Name != "RenderScaling") return;
            RestrictToMovementOwner();
        }

        private void ScheduleOwnerClamp()
        {
            if (_movementOwner == null && _externalMovementBounds == null || _ownerClampPending) return;
            _ownerClampPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _ownerClampPending = false;
                RestrictToMovementOwner();
            }, DispatcherPriority.Loaded);
        }

        private void RestrictToMovementOwner()
        {
            if (_restrictingOwnedWindow || !IsVisible || WindowState == WindowState.Minimized || OwnedMovementBounds is not { } area) return;
            _restrictingOwnedWindow = true;
            try
            {
                var frame = FrameSize ?? Bounds.Size;
                var extraWidth = Math.Max(0, frame.Width - Bounds.Width);
                var extraHeight = Math.Max(0, frame.Height - Bounds.Height);
                var maxWidth = Math.Max(1, area.Width / RenderScaling - extraWidth);
                var maxHeight = Math.Max(1, area.Height / RenderScaling - extraHeight);
                MinWidth = Math.Min(_requestedMinWidth, maxWidth);
                MinHeight = Math.Min(_requestedMinHeight, maxHeight);
                MaxWidth = Math.Min(_requestedMaxWidth, maxWidth);
                MaxHeight = Math.Min(_requestedMaxHeight, maxHeight);
                if (WindowState == WindowState.Maximized)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Keep the native maximized state and restore rectangle.
                        // Resizing to a normal window here loses both of them.
                        FitMaximizedOwnedWindow(area);
                        return;
                    }
                    WindowState = WindowState.Normal;
                    Width = MaxWidth; Height = MaxHeight;
                }
                if (Bounds.Width > MaxWidth) Width = MaxWidth;
                if (Bounds.Height > MaxHeight) Height = MaxHeight;
                var position = ClampOwnedPosition(Position, area, frame);
                if (Position != position) Position = position;
            }
            finally { _restrictingOwnedWindow = false; }
        }

        private PixelRect MaximizedOwnedRectangle(PixelRect area)
        {
            var frame = FrameSize ?? Bounds.Size;
            var extraWidth = Math.Max(0, frame.Width - Bounds.Width);
            var extraHeight = Math.Max(0, frame.Height - Bounds.Height);
            var width = Math.Clamp((_requestedMaxWidth + extraWidth) * RenderScaling, 1, area.Width);
            var height = Math.Clamp((_requestedMaxHeight + extraHeight) * RenderScaling, 1, area.Height);
            return new PixelRect(area.Position, new PixelSize((int)width, (int)height));
        }

        private IntPtr OwnedWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x24 /* WM_GETMINMAXINFO */ || OwnedMovementBounds is not { } area) return IntPtr.Zero;
            var rectangle = MaximizedOwnedRectangle(area);
            var monitor = Screens.ScreenFromWindow(this)?.Bounds.Position ?? default;
            var info = Marshal.PtrToStructure<OwnedMinMaxInfo>(lParam);
            info.MaxX = rectangle.Width; info.MaxY = rectangle.Height;
            info.PositionX = rectangle.X - monitor.X; info.PositionY = rectangle.Y - monitor.Y;
            info.MinTrackX = Math.Min(info.MinTrackX, rectangle.Width);
            info.MinTrackY = Math.Min(info.MinTrackY, rectangle.Height);
            info.MaxTrackX = rectangle.Width; info.MaxTrackY = rectangle.Height;
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
            return IntPtr.Zero;
        }

        private void FitMaximizedOwnedWindow(PixelRect area)
        {
            if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
            var target = MaximizedOwnedRectangle(area);
            if (GetWindowRect(handle, out var current) && current.Left == target.X && current.Top == target.Y &&
                current.Right - current.Left == target.Width && current.Bottom - current.Top == target.Height) return;
            SetWindowPos(handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, 0x14 /* NOZORDER | NOACTIVATE */);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OwnedMinMaxInfo
        {
            public int ReservedX, ReservedY, MaxX, MaxY, PositionX, PositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OwnedNativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out OwnedNativeRect rectangle);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        private PixelPoint ClampOwnedPosition(PixelPoint position, PixelRect area, Size frame) => new(
            (int)Math.Clamp(position.X, area.X, Math.Max(area.X, Math.Floor(area.Right - frame.Width * RenderScaling))),
            (int)Math.Clamp(position.Y, area.Y, Math.Max(area.Y, Math.Floor(area.Bottom - frame.Height * RenderScaling))));

        private bool BeginOwnedWindowDrag(PointerPressedEventArgs e)
        {
            if (OwnedMovementBounds == null) return false;
            _windowDragStart = Position;
            _windowDragPointerStart = this.PointToScreen(e.GetPosition(this));
            _windowDragPointer = e.Pointer;
            e.Pointer.Capture(this);
            e.Handled = true;
            return true;
        }

        private void OnOwnedWindowPointerMoved(object sender, PointerEventArgs e)
        {
            if (_windowDragPointer != e.Pointer || e.Pointer.Captured != this || OwnedMovementBounds is not { } area) return;
            var current = this.PointToScreen(e.GetPosition(this));
            var next = new PixelPoint(_windowDragStart.X + current.X - _windowDragPointerStart.X, _windowDragStart.Y + current.Y - _windowDragPointerStart.Y);
            Position = ClampOwnedPosition(next, area, FrameSize ?? Bounds.Size);
            e.Handled = true;
        }

        private void OnOwnedWindowPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            if (_windowDragPointer != e.Pointer) return;
            EndOwnedWindowDrag();
            e.Handled = true;
        }

        private void EndOwnedWindowDrag()
        {
            var pointer = _windowDragPointer;
            _windowDragPointer = null;
            if (pointer?.Captured == this) pointer.Capture(null);
        }
    }

    // Inspectors retain their native caption, with the same owner containment.
    public class OwnedNativeWindow : ChromelessWindow
    {
        public OwnedNativeWindow()
        {
            Classes.Remove("custom_window_frame");
            ExtendClientAreaToDecorationsHint = false;
            SystemDecorations = SystemDecorations.Full;
        }
    }
}
