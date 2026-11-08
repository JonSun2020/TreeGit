using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // The ort merge path can overwrite ignored files even with
    // --no-overwrite-ignore. Detect existing untracked/ignored obstructions before
    // invoking merge or its autostash. Directory listings stay collapsed and the
    // incoming trees are streamed rather than retained in memory.
    public sealed class CheckMergeSafety : Command
    {
        public string Obstruction { get; private set; }

        public CheckMergeSafety(string repo, IEnumerable<string> sources)
        {
            WorkingDirectory = repo;
            Context = repo;
            _sources = sources;
            _untracked = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        public async Task<bool> GetResultAsync()
        {
            Obstruction = null;
            _untracked.Clear();
            Args = "ls-files --others --directory -z";
            if (!await ReadRecordsAsync(path => { _untracked.Add(path); return true; }).ConfigureAwait(false)) return false;
            if (_untracked.Count == 0) return true;
            foreach (var source in _sources)
            {
                Args = $"ls-tree -rz {source.Quoted()}";
                if (!await ReadRecordsAsync(CheckIncoming).ConfigureAwait(false)) return false;
            }
            return true;
        }

        private bool CheckIncoming(string record)
        {
            var separator = record.IndexOf('\t');
            if (separator < 6) return false;
            if (record.StartsWith("160000 ", StringComparison.Ordinal)) return true;
            var path = record[(separator + 1)..];
            var matches = _untracked.Contains(path) || _untracked.Contains(path + "/");
            for (var i = path.IndexOf('/'); !matches && i >= 0; i = path.IndexOf('/', i + 1))
                matches = _untracked.Contains(path[..i]) || _untracked.Contains(path[..(i + 1)]);
            if (!matches) return true;

            var root = Path.GetFullPath(WorkingDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(root, path));
            if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
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
                if (!directory || link || i == parts.Length - 1 ||
                    Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
                {
                    Obstruction = path;
                    return false;
                }
            }
            return true;
        }

        private async Task<bool> ReadRecordsAsync(Func<string, bool> accept)
        {
            try
            {
                using var process = new Process { StartInfo = CreateGitStartInfo(true) };
                Native.ProcessLauncher.Start(process);
                process.BeginErrorReadLine();
                var buffer = new char[16384];
                var record = new StringBuilder();
                var valid = true;
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] != '\0')
                        {
                            if (record.Length < 65536) record.Append(buffer[i]);
                            else valid = false;
                        }
                        else
                        {
                            if (valid && !accept(record.ToString())) valid = false;
                            record.Clear();
                        }
                    }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                return valid && record.Length == 0 && process.ExitCode == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Native.OS.LogException(ex);
                return false;
            }
        }

        private readonly IEnumerable<string> _sources;
        private readonly HashSet<string> _untracked;
    }
}
