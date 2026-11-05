using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.Win32;

namespace SourceGit.Native
{
    internal static class GitExecutableLocator
    {
        public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");

        public static bool IsBundled(string path) => IsWithin(path, Path.Combine(AppContext.BaseDirectory, "git"));

        public static bool IsManagedBundle(string path)
        {
            if (IsBundled(path)) return true;
            var root = RuntimeRoot(path);
            var host = root == null ? null : Path.GetDirectoryName(root);
            return host != null && File.Exists(Path.Combine(root, "runtime-manifest.json")) &&
                (File.Exists(Path.Combine(host, "TreeGit.exe")) || File.Exists(Path.Combine(host, "SourceGit.exe")));
        }

        [SupportedOSPlatform("windows")]
        public static string FindWindowsGit()
        {
            string selected;
#if INTEGRATION_TESTS
            if (Environment.GetEnvironmentVariable("TREEGIT_TEST_NO_SYSTEM_GIT") == "1")
                selected = Resolve([], BundledPath);
            else
#endif
                selected = Resolve(GetWindowsCandidates(), BundledPath);
            if (selected != null && IsBundled(selected))
            {
                var root = Path.Combine(AppContext.BaseDirectory, "git");
                foreach (var relative in new[] { "usr/bin/ssh.exe", "usr/bin/sh.exe", "ucrt64/etc/ssl/certs/ca-bundle.crt",
                    "ucrt64/libexec/git-core/git-lfs.exe", "ucrt64/libexec/git-core/git-flow.exe" })
                    if (!File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
                        return null;
            }
            return selected;
        }

        // Kept separate from registry enumeration so absence, stale installations
        // and broken executables can be tested without modifying the user's OS.
        internal static string Resolve(IEnumerable<string> systemCandidates, string bundledPath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bundledRoot = Path.GetDirectoryName(Path.GetDirectoryName(bundledPath));
            foreach (var candidate in systemCandidates)
            {
                var path = Normalize(candidate);
                if (path == null || IsWithin(path, bundledRoot) || !seen.Add(path))
                    continue;
                if (IsUsable(path))
                    return path;
            }
            return IsUsable(bundledPath) ? Path.GetFullPath(bundledPath) : null;
        }

        internal static bool IsUsable(string path)
        {
            return TryReadVersion(path, out _, out var version) && version >= Models.GitVersions.MINIMAL;
        }

        internal static bool TryReadVersion(string path, out string text, out Version version)
        {
            text = null;
            version = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            try
            {
                if (OperatingSystem.IsWindows() && !HasExecutableHeader(path)) return false;
                using var process = new Process { StartInfo = new ProcessStartInfo(path, "--version")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                }};
                uint previousMode = 0;
                var changedMode = OperatingSystem.IsWindows() && SetThreadErrorMode(GetThreadErrorMode() | 1, out previousMode);
                try { process.Start(); }
                finally { if (changedMode) SetThreadErrorMode(previousMode, out _); }
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    process.WaitForExit(1000);
                    return false;
                }
                if (process.ExitCode != 0 || !Task.WhenAll(stdout, stderr).Wait(1000))
                    return false;
                text = stdout.Result.Trim();
                var match = Regex.Match(text, @"^git version (\d+)\.(\d+)\.(\d+)");
                return match.Success && Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out version);
            }
            catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception ||
                e is InvalidOperationException || e is UnauthorizedAccessException || e is AggregateException)
            {
                return false;
            }
        }

        internal static string RuntimeRoot(string executable)
        {
            var path = Normalize(executable);
            if (path == null)
                return null;
            var directory = Path.GetDirectoryName(path);
            for (var i = 0; i < 4 && directory != null; i++, directory = Path.GetDirectoryName(directory))
                if (File.Exists(Path.Combine(directory, "cmd", "git.exe")) &&
                    (Directory.Exists(Path.Combine(directory, "ucrt64")) || Directory.Exists(Path.Combine(directory, "mingw64"))))
                    return directory;
            return null;
        }

        public static string ConfigureProcess(ProcessStartInfo start, string executable)
        {
            var root = RuntimeRoot(executable);
            if (root == null)
                return null;
            var paths = new List<string>();
            foreach (var relative in new[] { "cmd", "ucrt64/bin", "mingw64/bin", "usr/bin" })
            {
                var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(directory)) paths.Add(directory);
            }
            start.Environment.TryGetValue("PATH", out var previous);
            start.Environment["PATH"] = string.Join(Path.PathSeparator, paths) + Path.PathSeparator + previous;
            var ssh = Path.Combine(root, "usr", "bin", "ssh.exe");
            return File.Exists(ssh) ? ssh : null;
        }

        [SupportedOSPlatform("windows")]
        private static List<string> GetWindowsCandidates()
        {
            var candidates = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var registry = RegistryKey.OpenBaseKey(hive, view);
                        using var git = registry.OpenSubKey(@"SOFTWARE\GitForWindows");
                        if (git?.GetValue("InstallPath") is string install)
                        {
                            candidates.Add(Path.Combine(install, "cmd", "git.exe"));
                            candidates.Add(Path.Combine(install, "bin", "git.exe"));
                        }
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException ||
                        e is System.Security.SecurityException || e is ArgumentException) { }
                }
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    try { candidates.Add(Path.Combine(directory.Trim().Trim('"'), "git.exe")); }
                    catch (ArgumentException) { }
                }
            foreach (var directory in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            })
                if (!string.IsNullOrWhiteSpace(directory))
                    candidates.Add(Path.Combine(directory, "Git", "cmd", "git.exe"));
            return candidates;
        }

        private static bool IsWithin(string path, string directory)
        {
            var full = Normalize(path);
            var root = Normalize(directory);
            return full != null && root != null && full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
        }

        private static bool HasExecutableHeader(string path)
        {
            using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
            reader.BaseStream.Position = 0x3C;
            var offset = reader.ReadInt32();
            if (offset < 64 || offset > reader.BaseStream.Length - 24) return false;
            reader.BaseStream.Position = offset;
            return reader.ReadUInt32() == 0x4550;
        }

        [DllImport("kernel32.dll")] private static extern uint GetThreadErrorMode();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadErrorMode(uint mode, out uint previous);
    }
}
