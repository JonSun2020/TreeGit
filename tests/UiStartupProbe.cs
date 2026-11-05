using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SourceGit.Tests
{
    internal static class UiStartupProbe
    {
        public static int Main(string[] args)
        {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var checks = new List<string>();
            void Check(string name, bool valid) { if (!valid) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
            Check("an incomplete extraction is detected", !Native.UiStartup.IsComplete(root));
            foreach (var name in Models.TreeGitRelease.WindowsNativeLibraries) File.WriteAllBytes(Path.Combine(root, name), [1]);
            Check("all required UI libraries are accepted", Native.UiStartup.IsComplete(root));
            var library = Path.Combine(root, Models.TreeGitRelease.WindowsNativeLibraries[0]);
            File.WriteAllBytes(library, []);
            Check("an empty library is detected", !Native.UiStartup.IsComplete(root));
            File.Delete(library);
            Check("a deleted library is detected", !Native.UiStartup.IsComplete(root));
            var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var nativeRoot = Path.Combine(root, "runtimes", "win-" + architecture, "native");
            Directory.CreateDirectory(nativeRoot);
            foreach (var name in Models.TreeGitRelease.WindowsNativeLibraries) File.WriteAllBytes(Path.Combine(nativeRoot, name), [1]);
            Check("framework-dependent native asset directories are accepted", Native.UiStartup.IsComplete(root));
            File.WriteAllBytes(library, []);
            Check("a corrupt root library cannot be hidden by a runtime fallback", !Native.UiStartup.IsComplete(root));
            File.Delete(library);
            Directory.CreateDirectory(Path.Combine(root, "data"));
            var preference = Path.Combine(root, "data/preference.json");
            File.WriteAllText(preference, "{\"Locale\":\"zh_CN\"}");
            var message = Native.UiStartup.Message(root, true);
            Check("Chinese extraction guidance avoids filenames and stack traces", message.Contains("完整解压", StringComparison.Ordinal) && !message.Contains(".dll", StringComparison.Ordinal) && !message.Contains("Exception", StringComparison.Ordinal));
            Check("permission failures explain how to continue", Native.UiStartup.Message(root, false, new UnauthorizedAccessException()).Contains("写入权限", StringComparison.Ordinal));
            File.WriteAllText(preference, "{\"Locale\":\"en_US\"}");
            Check("the selected English locale is respected", Native.UiStartup.Message(root, true).Contains("Extract the complete package", StringComparison.Ordinal));
            foreach (var malformed in new[] { "[", "[]", "null", "{\"Locale\":123}" })
            {
                File.WriteAllText(preference, malformed);
                Check("error reporting survives malformed preferences: " + malformed, !string.IsNullOrEmpty(Native.UiStartup.Message(root, true)));
            }
            File.WriteAllText(Path.Combine(root, "ui-startup-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }));
            return 0;
        }
    }
}
