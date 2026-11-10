using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Stage all changed keys in a sibling copy, then publish through Git's lock.
    // Cancel and preparation failures never alter the real configuration file.
    public sealed class GlobalConfigEdit : IDisposable
    {
        private string _target;
        private string _temporary;
        private string _lockPath;
        private FileStream _lock;
        private byte[] _original;
        private bool _existed;
        private bool _ownsLock;
        private Dictionary<string, string> _baseline;
        private string[] _keys;
        private static readonly HashSet<string> BooleanKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "core.autocrlf", "fetch.prune", "commit.gpgsign", "tag.gpgsign", "http.sslverify",
        };

        private void EnsureUnchanged(Dictionary<string, string> values)
        {
            foreach (var key in _keys)
            {
                _baseline.TryGetValue(key, out var before);
                values.TryGetValue(key, out var now);
                if (before != now) throw new InvalidOperationException(App.Text("Preferences.Save.Changed"));
            }
        }

        private sealed class ConfigCommand : Command
        {
            public ConfigCommand(string args)
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Args = args;
                RaiseError = false;
            }
            public Result Read() => ReadToEnd();
            public Task<Result> ReadAsync() => ReadToEndAsync();
        }

        private static Task<Command.Result> Run(string args) => new ConfigCommand(args).ReadAsync();

        private static void AddValues(Dictionary<string, string> values, Command.Result result)
        {
            if (!result.IsSuccess && result.ExitCode != 1) throw new InvalidOperationException(App.Text("Preferences.Save.ReadFailed"));
            foreach (var record in result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = record.IndexOf('\n');
                var key = index < 0 ? record : record[..index];
                values[key] = index < 0 ? BooleanKeys.Contains(key) ? "true" : string.Empty : record[(index + 1)..];
            }
        }
        public static Dictionary<string, string> ReadEffective()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { "--system", "--global" })
                AddValues(values, new ConfigCommand("config " + scope + " --includes --null --get-regexp .").Read());
            return values;
        }
        private static async Task<Dictionary<string, string>> ReadEffectiveAsync()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { "--system", "--global" })
                AddValues(values, await Run("config " + scope + " --includes --null --get-regexp ."));
            return values;
        }

        public static async Task<GlobalConfigEdit> PrepareAsync(Dictionary<string, string> changes, Dictionary<string, string> baseline)
        {
            var edit = new GlobalConfigEdit();
            if (changes.Count == 0) return edit;
            try
            {
                edit._keys = changes.Keys.ToArray();
                edit._baseline = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
                edit.EnsureUnchanged(await ReadEffectiveAsync());
                var pathsResult = await Run("var GIT_CONFIG_GLOBAL");
                var candidates = pathsResult.IsSuccess ? pathsResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : [];
                if (candidates.Length == 0)
                {
                    var forced = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
                    var home = Environment.GetEnvironmentVariable("HOME");
                    if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                    if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(home, ".config");
                    candidates = !string.IsNullOrEmpty(forced) ? [forced] : [Path.Combine(home, ".gitconfig"), Path.Combine(xdg, "git", "config")];
                }
                var selected = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
                if (selected == "/dev/null" || selected.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(App.Text("Preferences.Save.ReadFailed"));
                edit._target = Path.GetFullPath(selected);
                var parent = Path.GetDirectoryName(edit._target);
                // Preserve linked dotfiles rather than replacing the link itself.
                for (var part = new FileInfo(edit._target) as FileSystemInfo; part != null; part = part is FileInfo f ? f.Directory : ((DirectoryInfo)part).Parent)
                    if (part.LinkTarget != null || (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0))
                        throw new InvalidOperationException(App.Text("Preferences.Save.LinkedConfig"));
                Directory.CreateDirectory(parent);
                edit._lockPath = edit._target + ".lock";
                try { edit._lock = new FileStream(edit._lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (File.Exists(edit._lockPath))
                {
                    throw new InvalidOperationException(App.Text("Preferences.Save.Locked"));
                }
                edit._ownsLock = true;
                edit.EnsureUnchanged(await ReadEffectiveAsync());
                edit._existed = File.Exists(edit._target);
                if (edit._existed && new FileInfo(edit._target).Length > 1024 * 1024)
                    throw new InvalidOperationException(App.Text("Preferences.Save.LargeConfig"));
                edit._original = edit._existed ? File.ReadAllBytes(edit._target) : [];
                edit._temporary = Path.Combine(parent, ".treegit-settings-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(edit._temporary, edit._original);
                foreach (var (key, value) in changes)
                {
                    var unset = string.IsNullOrWhiteSpace(value);
                    var args = $"config --file {edit._temporary.Quoted()} " + (unset ? $"--unset-all {key}" : $"--replace-all {key} {value.Quoted()}");
                    var result = await Run(args);
                    if (!result.IsSuccess && !(unset && result.ExitCode == 5))
                        throw new InvalidOperationException(App.Text("Preferences.Save.Failed"));
                }
                var stagedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddValues(stagedValues, await Run($"config --file {edit._temporary.Quoted()} --includes --null --get-regexp ."));
                foreach (var (key, value) in changes)
                    if (!string.IsNullOrWhiteSpace(value) && (!stagedValues.TryGetValue(key, out var effectiveValue) || effectiveValue != value))
                        throw new InvalidOperationException(App.Text("Preferences.Save.Overridden"));
                var bytes = await File.ReadAllBytesAsync(edit._temporary);
                await edit._lock.WriteAsync(bytes);
                edit._lock.Flush(true);
                return edit;
            }
            catch { edit.Dispose(); throw; }
        }

        public void Commit()
        {
            if (_target == null) return;
            EnsureUnchanged(ReadEffective());
            if (File.Exists(_target) != _existed || (_existed && !File.ReadAllBytes(_target).SequenceEqual(_original)))
                throw new InvalidOperationException(App.Text("Preferences.Save.Changed"));
            _lock.Dispose(); _lock = null;
            if (!OperatingSystem.IsWindows() && _existed) File.SetUnixFileMode(_lockPath, File.GetUnixFileMode(_target));
            File.Move(_lockPath, _target, true);
            _ownsLock = false;
        }
        public void Dispose()
        {
            _lock?.Dispose(); _lock = null;
            if (_ownsLock) { try { File.Delete(_lockPath); } catch { } _ownsLock = false; }
            if (_temporary != null)
            {
                try { File.Delete(_temporary); } catch { }
                try { File.Delete(_temporary + ".lock"); } catch { }
            }
        }
    }
}
