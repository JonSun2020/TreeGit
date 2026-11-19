using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Automation
{
    // Test build only. A positive executable/root marker is required; it never
    // enables native mouse/keyboard or CI fault-injection hooks on this desktop.
    [SupportedOSPlatform("windows")]
    internal static class LocalAuditHost
    {
        private static readonly Lazy<bool> Enabled = new(Validate);
        public static bool IsEnabled => Enabled.Value;
        private static bool _initialized;
        private static readonly ConditionalWeakTable<Window, object> Attached = new();

        public static void Initialize()
        {
            if (!IsEnabled || _initialized) return;
            _initialized = true;
            // WindowOpened is raised before the native Show call. Attach the
            // ordinary Opened handler so hiding happens after Show completes.
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
        }

        private static bool Validate()
        {
            var configured = Environment.GetEnvironmentVariable("TREEGIT_LOCAL_QA_ROOT");
            if (string.IsNullOrEmpty(configured)) return false;
            var root = Path.GetFullPath(configured);
            var executable = Path.GetFullPath(Environment.ProcessPath!);
            if (Environment.GetEnvironmentVariable("CI") != "true" || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ||
                !Path.GetFileName(root).StartsWith("TreeGit isolated UI runtime ", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(executable), root, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A positively owned local QA runtime is required.");
            var marker = Path.Combine(root, ".treegit-local-qa.json");
            if (new FileInfo(marker).Length > 8192) throw new InvalidOperationException("Invalid local QA marker.");
            using var data = JsonDocument.Parse(File.ReadAllText(marker));
            using var stream = File.OpenRead(executable);
            if (data.RootElement.GetProperty("purpose").GetString() != "TreeGit local QA" ||
                !string.Equals(data.RootElement.GetProperty("executableSha256").GetString(), Convert.ToHexString(SHA256.HashData(stream)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Local QA executable does not match its marker.");
            return true;
        }

        public static void Attach(Window window)
        {
            if (!IsEnabled) return;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            if (Attached.TryGetValue(window, out _)) return;
            Attached.Add(window, new object());
            window.Opened += (_, _) => MakePrivate(window);
            window.Activated += (_, _) => MakePrivate(window);
            window.PropertyChanged += (_, change) =>
            {
                if (change.Property == Window.WindowStateProperty)
                    Dispatcher.UIThread.Post(() => { if (window.IsVisible) MakePrivate(window); }, DispatcherPriority.Send);
            };
        }

        private static void MakePrivate(Window window)
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var owner) == 0 || owner != Environment.ProcessId)
                throw new InvalidOperationException("Only this QA process's own windows can be hidden.");
            // Hide only the native surface. Keep the Avalonia visual tree live
            // so routed controls and RenderTargetBitmap can still be verified.
            ShowWindow(handle, 0);
            if (IsWindowVisible(handle)) throw new Win32Exception("The owned QA window was not hidden.");
        }

        public static bool AllWindowsPrivate(System.Collections.Generic.IEnumerable<Window> windows) => windows.Where(w => w.IsVisible).All(w =>
            w.TryGetPlatformHandle()?.Handle is { } handle && !IsWindowVisible(handle) && !w.ShowActivated);

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    }
}
