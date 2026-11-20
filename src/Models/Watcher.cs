using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class Watcher : IDisposable
    {
        public class LockContext : IDisposable
        {
            public LockContext(Watcher target)
            {
                _target = target;
                lock (target._gate)
                    target._lockCount++;
            }

            public void Dispose()
            {
                var target = Interlocked.Exchange(ref _target, null);
                if (target == null) return;
                lock (target._gate)
                {
                    if (--target._lockCount == 0)
                        target.ArmNext();
                }
            }

            private Watcher _target;
        }

        public Watcher(IRepository repo, string fullpath, string gitDir)
        {
            _repo = repo;
            _root = new DirectoryInfo(fullpath).FullName;
            _watchers = new List<FileSystemWatcher>();

            var testGitDir = new DirectoryInfo(Path.Combine(fullpath, ".git")).FullName;
            var desiredDir = new DirectoryInfo(gitDir).FullName;
            if (testGitDir.Equals(desiredDir, StringComparison.Ordinal))
            {
                var combined = new FileSystemWatcher();
                combined.Path = fullpath;
                combined.Filter = "*";
                combined.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                combined.IncludeSubdirectories = true;
                combined.Created += OnRepositoryChanged;
                combined.Renamed += OnRepositoryChanged;
                combined.Changed += OnRepositoryChanged;
                combined.Deleted += OnRepositoryChanged;
                combined.Error += OnWatcherError;
                combined.EnableRaisingEvents = false;

                _watchers.Add(combined);
            }
            else
            {
                var wc = new FileSystemWatcher();
                wc.Path = fullpath;
                wc.Filter = "*";
                wc.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                wc.IncludeSubdirectories = true;
                wc.Created += OnWorkingCopyChanged;
                wc.Renamed += OnWorkingCopyChanged;
                wc.Changed += OnWorkingCopyChanged;
                wc.Deleted += OnWorkingCopyChanged;
                wc.Error += OnWatcherError;
                wc.EnableRaisingEvents = false;

                var git = new FileSystemWatcher();
                git.Path = gitDir;
                git.Filter = "*";
                git.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.FileName;
                git.IncludeSubdirectories = true;
                git.Created += OnGitDirChanged;
                git.Renamed += OnGitDirChanged;
                git.Changed += OnGitDirChanged;
                git.Deleted += OnGitDirChanged;
                git.Error += OnWatcherError;
                git.EnableRaisingEvents = false;

                _watchers.Add(wc);
                _watchers.Add(git);
            }

            _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);

            // Starts filesystem watchers in another thread to avoid UI blocking
            Task.Run(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                        foreach (var watcher in _watchers)
                            watcher.EnableRaisingEvents = true;
                    }
                }
                catch
                {
                    // Ignore exceptions. This may occur while `Dispose` is called.
                }
            });
        }

        public IDisposable Lock()
        {
            return new LockContext(this);
        }

        [Flags]
        private enum Changes { Branch = 1, Worktree = 2, Submodules = 4, Stashes = 8, Tags = 16 }

        public void MarkBranchUpdated() => Clear(Changes.Branch | Changes.Worktree);
        public void MarkTagUpdated() => Clear(Changes.Tags);
        public void MarkWorkingCopyUpdated() => Clear(Changes.Worktree);
        public void MarkStashUpdated() => Clear(Changes.Stashes);
        public void MarkSubmodulesUpdated() => Clear(Changes.Submodules);

        private void Clear(Changes changes)
        {
            lock (_gate)
                SetDeadlines(changes, 0);
        }

        private void Schedule(Changes changes, int delay)
        {
            lock (_gate)
            {
                if (_disposed) return;
                SetDeadlines(changes, Environment.TickCount64 + delay);
                ArmNext();
            }
        }

        private void SetDeadlines(Changes changes, long deadline)
        {
            for (var i = 0; i < _deadlines.Length; i++)
                if ((changes & (Changes)(1 << i)) != 0)
                    _deadlines[i] = deadline;
        }

        // Called under _gate. A quiet repository has no scheduled callbacks.
        private void ArmNext()
        {
            if (_disposed || _lockCount > 0 || !_repo.IsAutoRefreshEnabled)
                return;

            var next = long.MaxValue;
            foreach (var deadline in _deadlines)
                if (deadline > 0 && deadline < next)
                    next = deadline;
            if (next < _scheduledAt)
            {
                _scheduledAt = next;
                _timer.Change(Math.Max(0, next - Environment.TickCount64), Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Dispose();
            }
            foreach (var watcher in _watchers)
                watcher.Dispose();
        }

        private void Tick(object sender)
        {
            Changes due = 0;
            lock (_gate)
            {
                _scheduledAt = long.MaxValue;
                if (_disposed || _lockCount > 0 || !_repo.IsAutoRefreshEnabled)
                    return;
                var now = Environment.TickCount64;
                for (var i = 0; i < _deadlines.Length; i++)
                    if (_deadlines[i] > 0 && _deadlines[i] <= now)
                    {
                        due |= (Changes)(1 << i);
                        _deadlines[i] = 0;
                    }
                if ((due & Changes.Branch) != 0)
                {
                    due |= Changes.Worktree;
                    if (_repo.MayHaveSubmodules())
                        due |= Changes.Submodules;
                    SetDeadlines(due, 0);
                }
                ArmNext();
            }

            if ((due & Changes.Branch) != 0)
            {
                _repo.RefreshBranches();
                _repo.RefreshWorktrees();
            }
            if ((due & Changes.Worktree) != 0) _repo.RefreshWorkingCopyChanges();
            if ((due & Changes.Submodules) != 0) _repo.RefreshSubmodules();
            if ((due & Changes.Stashes) != 0) _repo.RefreshStashes();
            if ((due & Changes.Tags) != 0) _repo.RefreshTags();
            if ((due & (Changes.Branch | Changes.Tags)) != 0) _repo.RefreshCommits();
        }

        private void OnRepositoryChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name) || e.Name.Equals(".git", StringComparison.Ordinal))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            if (name.StartsWith(".git/", StringComparison.Ordinal))
                HandleGitDirFileChanged(name.Substring(5));
            else
                HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        /// <summary>
        ///     The internal buffer overflowed (a large fetch or checkout writes thousands of files), so
        ///     some events were dropped and we cannot know which. Reload everything once things settle.
        /// </summary>
        private void OnWatcherError(object o, ErrorEventArgs e)
        {
            var changes = Changes.Branch | Changes.Worktree | Changes.Stashes | Changes.Tags;
            if (_repo.MayHaveSubmodules()) changes |= Changes.Submodules;
            Schedule(changes, 1000);
        }

        private void OnGitDirChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            HandleGitDirFileChanged(name);
        }

        private void OnWorkingCopyChanged(object o, FileSystemEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Name))
                return;

            var name = e.Name.Replace('\\', '/').TrimEnd('/');
            if (name.Equals(".git", StringComparison.Ordinal) ||
                name.StartsWith(".git/", StringComparison.Ordinal) ||
                name.EndsWith("/.git", StringComparison.Ordinal))
                return;

            HandleWorkingCopyFileChanged(name, e.FullPath);
        }

        private void HandleGitDirFileChanged(string name)
        {
            if (name.Equals("config", StringComparison.Ordinal) || name.Equals("config.worktree", StringComparison.Ordinal) || name.EndsWith("/config.worktree", StringComparison.Ordinal))
            {
                Native.RepositoryCredentials.InvalidateMode(_root);
                Schedule(Changes.Branch, 500);
                return;
            }

            if (name.Contains("fsmonitor--daemon/", StringComparison.Ordinal) ||
                name.EndsWith(".lock", StringComparison.Ordinal) ||
                name.StartsWith("lfs/", StringComparison.Ordinal))
                return;

            if (name.StartsWith("modules", StringComparison.Ordinal))
            {
                if (name.EndsWith("/HEAD", StringComparison.Ordinal) ||
                    name.EndsWith("/ORIG_HEAD", StringComparison.Ordinal))
                {
                    Schedule(Changes.Submodules | Changes.Worktree, 1000);
                }
            }
            else if (name.Equals("MERGE_HEAD", StringComparison.Ordinal) ||
                name.Equals("AUTO_MERGE", StringComparison.Ordinal))
            {
                if (_repo.MayHaveSubmodules())
                    Schedule(Changes.Submodules, 1000);
            }
            else if (name.StartsWith("refs/tags", StringComparison.Ordinal))
            {
                Schedule(Changes.Tags, 500);
            }
            else if (name.StartsWith("refs/stash", StringComparison.Ordinal) || name.Equals("logs/refs/stash", StringComparison.Ordinal))
            {
                Schedule(Changes.Stashes, 500);
            }
            else if (name.Equals("HEAD", StringComparison.Ordinal) ||
                name.Equals("BISECT_START", StringComparison.Ordinal) ||
                name.Equals("BISECT_LOG", StringComparison.Ordinal) ||
                name.StartsWith("refs/bisect/", StringComparison.Ordinal) ||
                name.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                name.StartsWith("refs/remotes/", StringComparison.Ordinal) ||
                (name.StartsWith("worktrees/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                Schedule(Changes.Branch, 500);
            }
            else if (name.Equals("packed-refs", StringComparison.Ordinal))
            {
                Schedule(Changes.Branch | Changes.Tags, 500);
            }
            else if (name.StartsWith("reftable/", StringComparison.Ordinal))
            {
                Schedule(Changes.Branch | Changes.Tags | Changes.Stashes, 500);
            }
            else if (name.StartsWith("objects/", StringComparison.Ordinal) || name.Equals("index", StringComparison.Ordinal))
            {
                Schedule(Changes.Worktree, 1000);
            }
        }

        private void HandleWorkingCopyFileChanged(string name, string fullpath)
        {
            if (name.StartsWith(".vs/", StringComparison.Ordinal))
                return;

            if (name.Equals(".gitmodules", StringComparison.Ordinal))
            {
                Schedule(Changes.Submodules | Changes.Worktree, 1000);
                return;
            }

            var dir = Directory.Exists(fullpath) ? fullpath : Path.GetDirectoryName(fullpath);
            if (IsInSubmodule(dir))
            {
                Schedule(Changes.Submodules, 1000);
                return;
            }

            Schedule(Changes.Worktree, 1000);
        }

        private bool IsInSubmodule(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Equals(_root, StringComparison.Ordinal))
                return false;

            if (File.Exists($"{folder}/.git"))
                return true;

            return IsInSubmodule(Path.GetDirectoryName(folder));
        }

        private readonly IRepository _repo;
        private readonly string _root;
        private readonly List<FileSystemWatcher> _watchers;
        private readonly Timer _timer;
        private readonly object _gate = new();
        private readonly long[] _deadlines = new long[5];
        private long _scheduledAt = long.MaxValue;
        private int _lockCount;
        private bool _disposed;
    }
}
