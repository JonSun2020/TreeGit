using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SourceGit.Models
{
    // Git writes target.lock before replacing a configuration file. Reserve the
    // same locks, edit separate copies, and keep every reservation until all
    // replacements (or their rollback) have finished.
    internal sealed class RepositoryConfigurationTransaction : IDisposable
    {
        private sealed class Entry
        {
            public string Target;
            public string Staged;
            public byte[] Original;
            public FileStream Reservation;
            public bool Replaced;
        }

        private readonly List<Entry> _entries = [];
        private readonly Entry _config;
        private readonly Entry _sharedLinks;
        private readonly Entry _settings;
        private readonly string _id = Guid.NewGuid().ToString("N");

        public string GitConfigPath => _config.Staged;
        public string IssueTrackerPath(bool shared) => (shared ? _sharedLinks : _config).Staged;

        public RepositoryConfigurationTransaction(string repository, string settingsFile)
        {
            try
            {
                _config = Prepare(Path.Combine(Path.GetDirectoryName(settingsFile)!, "config"));
                _sharedLinks = Prepare(Path.Combine(repository, ".issuetracker"));
                _settings = Prepare(settingsFile, settingsFile + ".tmp");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private Entry Prepare(string target, string staged = null)
        {
            var entry = new Entry { Target = target, Staged = staged ?? target + ".treegit-" + _id + ".tmp" };
            // DeleteOnClose also releases our reservation if the process exits
            // unexpectedly; existing locks from other tools are never removed.
            entry.Reservation = new FileStream(target + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);
            _entries.Add(entry);
            entry.Original = File.Exists(target) ? File.ReadAllBytes(target) : null;
            File.WriteAllBytes(entry.Staged, entry.Original ?? []);
            return entry;
        }

        public void WriteSettings(string content) => File.WriteAllText(_settings.Staged, content);

        public void Commit()
        {
            // Editors which ignore Git's reservation may still change a target
            // while we edit the copies. Never overwrite that newer value.
            foreach (var entry in _entries)
            {
                var actual = File.Exists(entry.Target) ? File.ReadAllBytes(entry.Target) : null;
                if (!Same(entry.Original, actual))
                    throw new IOException("配置文件已被其他程序修改，请重新打开仓库设置。");
            }

            try
            {
                foreach (var entry in _entries)
                {
                    var content = File.ReadAllBytes(entry.Staged);
                    if (Same(entry.Original, content) || entry.Original == null && content.Length == 0)
                        continue;
                    File.Move(entry.Staged, entry.Target, true);
                    entry.Replaced = true;
                }
            }
            catch (Exception saveError)
            {
                var errors = new List<Exception> { saveError };
                foreach (var entry in _entries.AsEnumerable().Reverse().Where(e => e.Replaced))
                {
                    try
                    {
                        if (entry.Original == null) File.Delete(entry.Target);
                        else
                        {
                            File.WriteAllBytes(entry.Staged, entry.Original);
                            File.Move(entry.Staged, entry.Target, true);
                        }
                    }
                    catch (Exception rollbackError) { errors.Add(rollbackError); }
                }
                if (errors.Count > 1)
                    throw new AggregateException("保存失败，部分配置未能还原。请检查仓库配置文件。", errors);
                throw;
            }
        }

        private static bool Same(byte[] a, byte[] b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

        public void Dispose()
        {
            foreach (var entry in _entries.AsEnumerable().Reverse())
            {
                try { File.Delete(entry.Staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { File.Delete(entry.Staged + ".lock"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                entry.Reservation?.Dispose();
            }
        }
    }
}
