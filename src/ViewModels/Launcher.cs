using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Launcher : ObservableObject
    {
        public AvaloniaList<LauncherPage> Pages
        {
            get;
            private set;
        }

        public Workspace ActiveWorkspace
        {
            get => _activeWorkspace;
            private set => SetProperty(ref _activeWorkspace, value);
        }

        public LauncherPage ActivePage
        {
            get => _activePage;
            set
            {
                if (value == null && _activePage != null && Pages.Contains(_activePage))
                    return;
                if (SetProperty(ref _activePage, value))
                    PostActivePageChanged();
            }
        }

        public ICommandPalette CommandPalette
        {
            get => _commandPalette;
            set => SetProperty(ref _commandPalette, value);
        }

        public Models.Version NewVersion
        {
            get => _newVersion;
            set => SetProperty(ref _newVersion, value);
        }

        public Launcher(string startupRepo)
        {
            Models.Notification.Raised += DispatchNotification;
            _ignoreIndexChange = true;

            ActiveWorkspace = Preferences.Instance.GetActiveWorkspace();
            Pages = new AvaloniaList<LauncherPage>();
            RestoreWorkspaceTabs();

            _ignoreIndexChange = false;

            if (!TryOpenRepositoryFromPath(startupRepo))
                EnsureRepositoryLoaded(ActivePage);

            PostActivePageChanged();
        }

        public bool TryOpenRepositoryFromPath(string repo)
        {
            if (!string.IsNullOrEmpty(repo) && Directory.Exists(repo))
            {
                var isBare = new Commands.IsBareRepository(repo).GetResult();
                if (isBare)
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(repo, null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }

                var test = new Commands.QueryRepositoryRootPath(repo).GetResult();
                if (test.IsSuccess && !string.IsNullOrEmpty(test.StdOut))
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(test.StdOut.Trim(), null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }
                else
                {
                    if (ActivePage is not { Data: Welcome { }, Popup: null })
                        AddNewTab();

                    if (Models.SafeDirectories.IsUntrustedRepository(test.StdErr) &&
                        Models.SafeDirectories.TryGetSafeDirectoryValue(repo, test.StdErr, out var safeDirectory))
                        ActivePage.Popup = new TrustRepository(ActivePage.Node.Id, repo, test.StdErr, safeDirectory, null, false, true, 0);
                    else
                        ActivePage.Popup = new Init(ActivePage.Node.Id, repo, null, 0, test.StdErr ?? "Unknown error occurred while opening the repository.");

                    return true;
                }
            }

            return false;
        }

        public void CloseAll()
        {
            _isClosing = true;
            _restoreGeneration++;
            _ignoreIndexChange = true;

            foreach (var one in Pages)
                CloseRepositoryInTab(one, false);

            _ignoreIndexChange = false;
        }

        public void SwitchWorkspace(Workspace to)
        {
            if (to == null || to.IsActive)
                return;

            _ignoreIndexChange = true;
            _restoreGeneration++;

            var pref = Preferences.Instance;
            foreach (var w in pref.Workspaces)
                w.IsActive = false;

            ActiveWorkspace = to;
            to.IsActive = true;

            foreach (var one in Pages)
                CloseRepositoryInTab(one, false);

            Pages.Clear();
            RestoreWorkspaceTabs();

            _ignoreIndexChange = false;
            PostActivePageChanged();
            LoadDeferredRepositories();
            Preferences.Instance.Save();
            GC.Collect();
        }

        public void AddNewTab()
        {
            var page = new LauncherPage();
            Pages.Add(page);
            ActivePage = page;
        }

        public void MoveTab(LauncherPage from, LauncherPage to)
        {
            _ignoreIndexChange = true;

            var fromIdx = Pages.IndexOf(from);
            var toIdx = Pages.IndexOf(to);
            Pages.Move(fromIdx, toIdx);

            _activeWorkspace.Repositories.Clear();
            foreach (var p in Pages)
            {
                if (p.Node.IsRepository)
                    _activeWorkspace.Repositories.Add(p.Node.Id);
            }

            _ignoreIndexChange = false;
            ActivePage = from;
        }

        public void GotoNextTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var nextIdx = (activeIdx + 1) % Pages.Count;
            ActivePage = Pages[nextIdx];
        }

        public void GotoPrevTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var prevIdx = activeIdx == 0 ? Pages.Count - 1 : activeIdx - 1;
            ActivePage = Pages[prevIdx];
        }

        public void CloseTab(LauncherPage page)
        {
            if (Pages.Count == 1)
            {
                var last = Pages[0];
                if (last.Node.IsRepository)
                {
                    _activeWorkspace.Repositories.Clear();
                    _activeWorkspace.ActiveIdx = 0;

                    if (last.Data is Repository repo)
                    {
                        if (last.Node.IsUnmanaged)
                            last.Node.SaveMinimalInfo(repo.GitDir);
                        repo.Close();
                    }

                    Welcome.Instance.ClearSearchFilter();
                    last.Node = new RepositoryNode() { Id = Guid.NewGuid().ToString() };
                    last.Data = Welcome.Instance;
                    last.Popup?.Cleanup();
                    last.Popup = null;

                    PostActivePageChanged();
                    GC.Collect();
                }
                else
                {
                    App.Quit(0);
                }

                return;
            }

            page ??= _activePage;

            var removeIdx = Pages.IndexOf(page);
            var activeIdx = Pages.IndexOf(_activePage);
            if (removeIdx == activeIdx)
                ActivePage = Pages[removeIdx > 0 ? removeIdx - 1 : removeIdx + 1];

            CloseRepositoryInTab(page);
            Pages.RemoveAt(removeIdx);
            GC.Collect();
        }

        public void CloseOtherTabs()
        {
            if (Pages.Count == 1)
                return;

            _ignoreIndexChange = true;

            var id = ActivePage.Node.Id;
            foreach (var one in Pages)
            {
                if (one.Node.Id != id)
                    CloseRepositoryInTab(one);
            }

            Pages = new AvaloniaList<LauncherPage> { ActivePage };
            OnPropertyChanged(nameof(Pages));

            _activeWorkspace.ActiveIdx = 0;
            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void CloseRightTabs()
        {
            _ignoreIndexChange = true;

            var endIdx = Pages.IndexOf(ActivePage);
            for (var i = Pages.Count - 1; i > endIdx; i--)
            {
                CloseRepositoryInTab(Pages[i]);
                Pages.Remove(Pages[i]);
            }

            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void OpenRepositoryInTab(string repo, LauncherPage page)
        {
            var normalizedPath = repo.Replace('\\', '/').TrimEnd('/');
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = Path.GetFileName(normalizedPath),
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };

            OpenRepositoryInTab(node, null);
        }

        public void OpenRepositoryInTab(RepositoryNode node, LauncherPage page)
        {
            foreach (var one in Pages)
            {
                if (one.Node.Id == node.Id)
                {
                    EnsureRepositoryLoaded(one);
                    ActivePage = one;
                    return;
                }
            }

            if (!Directory.Exists(node.Id))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Repository does NOT exist any more. Please remove it.",
                    IsError = true,
                });
                return;
            }

            var isBare = new Commands.IsBareRepository(node.Id).GetResult();
            var gitDir = isBare ? node.Id : GetRepositoryGitDir(node.Id);
            if (string.IsNullOrEmpty(gitDir))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            if (node.IsUnmanaged)
                node.LoadMinimalInfo(gitDir);

            var repo = new Repository(isBare, node.Id, gitDir);
            repo.Open();

            if (page == null)
            {
                if (_activePage == null || _activePage.Node.IsRepository)
                {
                    page = new LauncherPage(node, repo);
                    Pages.Add(page);
                }
                else
                {
                    page = _activePage;
                    page.Node = node;
                    page.Data = repo;
                }
            }
            else
            {
                page.Node = node;
                page.Data = repo;
            }

            _activeWorkspace.Repositories.Clear();
            foreach (var p in Pages)
            {
                if (p.Node.IsRepository)
                    _activeWorkspace.Repositories.Add(p.Node.Id);
            }

            if (_activePage == page)
                PostActivePageChanged();
            else
                ActivePage = page;
        }

        public void OpenSubRepository(LauncherPage ownerPage, string fullpath)
        {
            var normalizedPath = fullpath.Replace('\\', '/').TrimEnd('/');

            // Check if the sub-repository is already open in any of the tabs
            foreach (var one in Pages)
            {
                if (one.Node.Id.Equals(normalizedPath, StringComparison.Ordinal))
                {
                    ActivePage = one;
                    return;
                }
            }

            // Make sure the target directory exists
            if (!Directory.Exists(normalizedPath))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Repository path does NOT exist. Please check the path.",
                    IsError = true,
                });

                return;
            }

            // Check if the target directory is a valid git repository
            var gitDir = GetRepositoryGitDir(normalizedPath);
            if (string.IsNullOrEmpty(gitDir))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            // Get the owner repository's name and extract the pure owner name (without any prefix)
            var ownerName = ownerPage.Node.Name;
            var colonIdx = ownerName.LastIndexOf(':');
            var pureOwnerName = (colonIdx >= 0 && colonIdx < ownerName.Length - 1) ? ownerName.Substring(colonIdx + 1).Trim() : ownerName;

            // Find the sub-repository node in the preferences or create a new one if it doesn't exist
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = $"{pureOwnerName}: {Path.GetFileName(normalizedPath)}",
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };
            node.LoadMinimalInfo(gitDir);

            var repo = new Repository(false, node.Id, gitDir);
            repo.Open();

            var page = new LauncherPage(node, repo);
            var idxOfOwner = Pages.IndexOf(ownerPage);
            Pages.Insert(idxOfOwner + 1, page);
            _activeWorkspace.Repositories.Insert(idxOfOwner + 1, normalizedPath);
            ActivePage = page;
        }

        private void DispatchNotification(Models.Notification notification)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(() => DispatchNotification(notification));
                return;
            }

            if (string.IsNullOrEmpty(notification.Group))
            {
                _activePage?.Notifications.Add(notification);
                return;
            }

            foreach (var page in Pages)
            {
                var id = page.Node.Id.Replace('\\', '/').TrimEnd('/');
                if (id.Equals(notification.Group, StringComparison.OrdinalIgnoreCase))
                {
                    page.Notifications.Add(notification);
                    return;
                }
            }

            _activePage?.Notifications.Add(notification);
        }

        private string GetRepositoryGitDir(string repo)
        {
            var fullpath = Path.Combine(repo, ".git");
            if (Directory.Exists(fullpath))
            {
                if (Directory.Exists(Path.Combine(fullpath, "refs")) &&
                    Directory.Exists(Path.Combine(fullpath, "objects")) &&
                    File.Exists(Path.Combine(fullpath, "HEAD")))
                    return fullpath;

                return null;
            }

            if (File.Exists(fullpath))
            {
                var redirect = File.ReadAllText(fullpath).Trim();
                if (redirect.StartsWith("gitdir: ", StringComparison.Ordinal))
                    redirect = redirect.Substring(8);

                if (!Path.IsPathRooted(redirect))
                    redirect = Path.GetFullPath(Path.Combine(repo, redirect));

                if (Directory.Exists(redirect))
                    return redirect;

                return null;
            }

            return new Commands.QueryGitDir(repo).GetResult();
        }

        private void CloseRepositoryInTab(LauncherPage page, bool removeFromWorkspace = true)
        {
            if (removeFromWorkspace && page.Node.IsRepository)
                _activeWorkspace.Repositories.Remove(page.Node.Id);
            if (page.Data is Repository repo)
            {
                if (page.Node.IsUnmanaged)
                    page.Node.SaveMinimalInfo(repo.GitDir);

                repo.Close();
            }

            page.Popup?.Cleanup();
            page.Popup = null;
            page.Data = null;
        }

        private void PostActivePageChanged()
        {
            if (_ignoreIndexChange)
                return;

            EnsureRepositoryLoaded(_activePage);
            if (_activePage?.Node.IsRepository == true)
                _activeWorkspace.ActiveIdx = _activeWorkspace.Repositories.IndexOf(_activePage.Node.Id);

            CommandPalette = null;
        }

        private void RestoreWorkspaceTabs()
        {
            var paths = _activeWorkspace.Repositories.ToArray();
            var activeIdx = _activeWorkspace.ActiveIdx;
            foreach (var path in paths)
            {
                var normalized = path.Replace('\\', '/').TrimEnd('/');
                var node = Preferences.Instance.FindNode(normalized) ?? new RepositoryNode
                {
                    Id = normalized, Name = Path.GetFileName(normalized), IsRepository = true, IsUnmanaged = true,
                };
                Pages.Add(new LauncherPage(node, new RepositoryLoading()));
            }
            if (Pages.Count == 0) AddNewTab();
            else ActivePage = Pages[Math.Clamp(activeIdx, 0, Pages.Count - 1)];
        }

        private (bool IsBare, string GitDir) InspectRepository(string path)
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Repository directory does not exist.");
            var isBare = new Commands.IsBareRepository(path).GetResult();
            var gitDir = isBare ? path : GetRepositoryGitDir(path);
            if (string.IsNullOrEmpty(gitDir)) throw new IOException("Given path is not a valid Git repository.");
            return (isBare, gitDir);
        }

        private void CompleteRepositoryLoad(LauncherPage page, (bool IsBare, string GitDir) details)
        {
            if (page.Node.IsUnmanaged) page.Node.LoadMinimalInfo(details.GitDir);
            var repo = new Repository(details.IsBare, page.Node.Id, details.GitDir);
            repo.Open();
            page.Data = repo;
        }

        private void FailRepositoryLoad(LauncherPage page, RepositoryLoading pending, Exception error)
        {
            pending.Failed = true;
            pending.Message = error.Message;
            if (!page.Notifications.Any(notification => notification.Message == error.Message))
                page.Notifications.Add(new Models.Notification { Group = page.Node.Id, Message = error.Message, IsError = true });
        }

        private void EnsureRepositoryLoaded(LauncherPage page)
        {
            if (page?.Data is not RepositoryLoading pending) return;
            pending.Failed = false;
            pending.Message = App.Text("Repository.Loading");
            try
            {
                CompleteRepositoryLoad(page, InspectRepository(page.Node.Id));
                Native.StartupTrace.Mark("active-repository-loaded");
            }
            catch (Exception error) { FailRepositoryLoad(page, pending, error); }
        }

        public async void LoadDeferredRepositories()
        {
            if (_isClosing || _backgroundGeneration == _restoreGeneration) return;
            var generation = _restoreGeneration;
            _backgroundGeneration = generation;
            var pages = Pages.ToArray();
            await Task.Delay(500);
            foreach (var page in pages)
            {
                if (_isClosing || generation != _restoreGeneration) return;
                if (!Pages.Contains(page) || page.Data is not RepositoryLoading pending || pending.Failed) continue;
                // Let the visible history finish its first query before spending time on hidden tabs.
                for (var wait = 0; wait < 50 && _activePage?.Data is Repository { Histories.IsLoading: true }; wait++)
                {
                    await Task.Delay(100);
                    if (_isClosing || generation != _restoreGeneration) return;
                }
                if (!Pages.Contains(page) || !ReferenceEquals(page.Data, pending)) continue;
                try
                {
                    var path = page.Node.Id;
                    var details = await Task.Run(() => InspectRepository(path));
                    if (_isClosing || generation != _restoreGeneration) return;
                    if (Pages.Contains(page) && ReferenceEquals(page.Data, pending) && !pending.Failed)
                    {
                        CompleteRepositoryLoad(page, details);
                        Native.StartupTrace.Mark("deferred-repository-loaded", true);
                    }
                }
                catch (Exception error)
                {
                    if (!_isClosing && generation == _restoreGeneration && Pages.Contains(page) && ReferenceEquals(page.Data, pending))
                        FailRepositoryLoad(page, pending, error);
                }
                await Task.Delay(150);
            }
        }

        private Workspace _activeWorkspace;
        private LauncherPage _activePage;
        private bool _ignoreIndexChange;
        private bool _isClosing;
        private int _restoreGeneration;
        private int _backgroundGeneration = -1;
        private ICommandPalette _commandPalette;
        private Models.Version _newVersion = null;
    }
}
