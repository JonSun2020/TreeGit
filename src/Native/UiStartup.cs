using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SourceGit.Native
{
    internal static class UiStartup
    {
        public static bool IsComplete(string root)
        {
            if (!OperatingSystem.IsWindows()) return true;
            try
            {
                foreach (var name in Models.TreeGitRelease.WindowsNativeLibraries)
                {
                    var file = new FileInfo(Path.Combine(root, name));
                    if (file.Exists)
                    {
                        if (file.Length == 0) return false;
                        continue;
                    }
                    // Framework-dependent developer builds use .NET's RID-specific native assets.
                    var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                    var runtimeFile = new FileInfo(Path.Combine(root, "runtimes", "win-" + architecture, "native", name));
                    if (!runtimeFile.Exists || runtimeFile.Length == 0) return false;
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }

        public static string Message(string root, bool incomplete, Exception error = null)
        {
            if (incomplete)
                return Models.Locale.BootstrapText("TreeGit.Startup.Incomplete", root);
            for (var cause = error; cause != null; cause = cause.InnerException)
                if (cause is UnauthorizedAccessException)
                    return Models.Locale.BootstrapText("TreeGit.Startup.NotWritable", root);
            return Models.Locale.BootstrapText("TreeGit.Startup.OpenFailed", root);
        }

        public static void Show(string root, bool incomplete, Exception error = null)
        {
            var text = Message(root, incomplete, error);
#if INTEGRATION_TESTS
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TREEGIT_LOCAL_QA_ROOT")))
            {
                Console.Error.WriteLine(text);
                return;
            }
#endif
            if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, text, "TreeGit", 0x10);
            else Console.Error.WriteLine(text);
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
    }
}
