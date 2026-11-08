using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // A hard reset/restore can remove an untracked directory which obstructs a
    // tracked file, and can write through a directory junction. Inspect the actual
    // target tree/index with NUL records before allowing either operation.
    public sealed class CheckRestoreSafety : Command
    {
        public string Obstruction { get; private set; }

        public CheckRestoreSafety(string repo, bool fromHead, HashSet<string> paths = null)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = fromHead ? "ls-tree -rz HEAD" : "ls-files --stage -z";
            _fromHead = fromHead;
            _paths = paths;
        }

        public async Task<bool> GetResultAsync()
        {
            try
            {
                if (_fromHead)
                {
                    Args = "rev-parse --verify --quiet HEAD";
                    var head = await ReadToEndAsync().ConfigureAwait(false);
                    if (!head.IsSuccess && head.ExitCode != 1) return false;
                    Args = head.IsSuccess ? "ls-tree -rz HEAD" : "ls-files --stage -z";
                }
                using var process = new Process { StartInfo = CreateGitStartInfo(true) };
                Native.ProcessLauncher.Start(process);
                process.BeginErrorReadLine();
                var buffer = new char[16384];
                var record = new StringBuilder();
                var safe = true;
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] != '\0')
                        {
                            // A malformed stream must not allocate without limit.
                            if (record.Length < 65536) record.Append(buffer[i]);
                            else safe = false;
                            continue;
                        }
                        if (safe && !IsSafe(record.ToString())) safe = false;
                        record.Clear();
                    }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                return safe && record.Length == 0 && process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Native.OS.LogException(ex);
                return false;
            }
        }

        private bool IsSafe(string record)
        {
            var separator = record.IndexOf('\t');
            if (separator < 6) return false;
            var path = record[(separator + 1)..];
            if (_paths != null && !_paths.Contains(path)) return true;
            // Resetting the parent index must not recurse into a submodule.
            if (record.StartsWith("160000 ", StringComparison.Ordinal)) return true;
            var root = Path.GetFullPath(WorkingDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(root, path));
            if (!Path.IsPathFullyQualified(fullPath) || !fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            var current = root;
            var parts = Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar);
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
                var directory = (attributes & FileAttributes.Directory) != 0;
                var link = (attributes & FileAttributes.ReparsePoint) != 0;
                if ((i < parts.Length - 1 && (!directory || link)) ||
                    (i == parts.Length - 1 && directory && (link || HasContents(current))))
                {
                    Obstruction = path;
                    return false;
                }
            }
            return true;
        }

        private static bool HasContents(string directory)
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            return entries.MoveNext();
        }

        private readonly HashSet<string> _paths;
        private readonly bool _fromHead;
    }
}
