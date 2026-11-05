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
                    if (!file.Exists || file.Length == 0) return false;
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }

        public static string Message(string root, bool incomplete, Exception error = null)
        {
            var chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            try
            {
                var preference = Path.Combine(root, "data", "preference.json");
                if (File.Exists(preference) && new FileInfo(preference).Length < 1024 * 1024)
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(preference));
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Locale", out var locale) && locale.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(locale.GetString()))
                        chinese = locale.GetString()!.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { }
            if (incomplete)
                return chinese ? "TreeGit 文件不完整。请完整解压软件包后重新打开。原有配置和仓库不会被删除。" : "Some TreeGit files are missing or incomplete. Extract the complete package and try again. Your settings and repositories will be kept.";
            for (var cause = error; cause != null; cause = cause.InnerException)
                if (cause is UnauthorizedAccessException)
                    return chinese ? "TreeGit 无法写入所需文件。请将完整的软件文件夹移到有写入权限的位置后重新打开。" : "TreeGit cannot save the files it needs. Move the complete application folder to a writable location and try again.";
            return chinese ? "TreeGit 未能正常打开。请重新打开后再试；如果仍有问题，请保留 data 文件夹并重新完整解压最新版。" : "TreeGit could not open normally. Try opening it again. If the problem continues, keep your data folder and extract a fresh copy of the latest package.";
        }

        public static void Show(string root, bool incomplete, Exception error = null)
        {
            var text = Message(root, incomplete, error);
            if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, text, "TreeGit", 0x10 | 0x10000 | 0x40000);
            else Console.Error.WriteLine(text);
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
    }
}
