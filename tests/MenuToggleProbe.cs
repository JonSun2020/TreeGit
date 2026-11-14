using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Automation
{
    // Routed input only: this probe never moves the OS pointer or invokes a
    // repository command. It is absent from normal and portable builds.
    internal static class MenuToggleProbe
    {
        private static readonly Avalonia.Input.Pointer Pointer = new(7, PointerType.Mouse, true);
        private static readonly List<object> Events = [];
        private static Menu _menu;
        private static string _phase;

        public static object Execute(Window window, JsonElement args)
        {
            if (Environment.GetEnvironmentVariable("TREEGIT_TEST_MENU_PROBE") != "1")
                throw new InvalidOperationException("An isolated menu fixture is required.");
            var menu = window.GetVisualDescendants().OfType<Menu>().Single(m => m.Name == "PrimaryMenu");
            var headers = menu.Items.OfType<MenuItem>().ToArray();
            if (_menu != menu)
            {
                _menu = menu;
                foreach (var item in headers)
                {
                    var header = item;
                    item.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == MenuItem.IsSubMenuOpenProperty)
                            Events.Add(new { phase = _phase, header = header.Header?.ToString(), open = header.IsSubMenuOpen });
                    };
                }
            }
            var action = args.TryGetProperty("text", out var text) ? text.GetString() : "inspect";
            var native = action.StartsWith("native_", StringComparison.Ordinal);
            if (native) action = action[7..];
            // Keep the real desktop pointer from racing synthetic header-entry
            // events when popup layout causes Avalonia to refresh hit testing.
            // Routed events below still exercise the normal menu handlers.
            window.IsHitTestVisible = native || action == "finish";
            _phase = action;
            var index = args.TryGetProperty("value", out var value) ? value.GetInt32() : 0;
            if (index < 0 || index >= headers.Length) throw new ArgumentOutOfRangeException(nameof(index));
            var selected = headers[index];
            var source = selected.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_HeaderPresenter") ?? selected;
            var point = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), window) ?? default;
            if (action == "reset") { menu.Close(); Events.Clear(); }
            else if (action == "press")
                source.RaiseEvent(new PointerPressedEventArgs(source, Pointer, window, point, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            else if (action == "release")
                source.RaiseEvent(new PointerReleasedEventArgs(source, Pointer, window, point, (ulong)Environment.TickCount64,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            else if (action == "enter") selected.RaiseEvent(new RoutedEventArgs(MenuItem.PointerEnteredItemEvent));
            else if (action is "escape" or "down" or "right") selected.RaiseEvent(new KeyEventArgs
                { RoutedEvent = InputElement.KeyDownEvent, Key = action == "escape" ? Key.Escape : action == "down" ? Key.Down : Key.Right });
            else if (action == "outside") window.RaiseEvent(new PointerPressedEventArgs(window, Pointer, window,
                new Point(window.Bounds.Width / 2, window.Bounds.Height - 40), (ulong)Environment.TickCount64,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            else if (action is not ("inspect" or "finish")) throw new ArgumentException("Unsupported menu fixture action.");
            return new
            {
                menuOpen = menu.IsOpen,
                headers = headers.Select((h, i) => new { index = i, text = h.Header?.ToString(), open = h.IsSubMenuOpen, selected = h.IsSelected, childSelected = h.SelectedIndex,
                    popup = (typeof(MenuItem).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(h) as Popup)?.IsOpen,
                    outlineVisible = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.IsVisible,
                    outlineBrush = h.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "MenuHeaderOutline")?.BorderBrush?.ToString(),
                    bounds = h.Bounds.ToString(),
                    screenX = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).X,
                    screenY = h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2)).Y }).ToArray(),
                events = Events.ToArray(),
            };
        }
    }
}
