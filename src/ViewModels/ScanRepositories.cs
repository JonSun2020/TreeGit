using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class ScanRepositories : Popup
    {
        public string RootPath
        {
            get => _rootPath;
            set => SetProperty(ref _rootPath, value);
        }

        public ScanRepositories()
        {
            _rootPath = Preferences.Instance.GitDefaultCloneDir ?? string.Empty;
            GetManagedRepositories(Preferences.Instance.RepositoryNodes, _managed);
        }

        public override async Task<bool> Sure()
        {
            var selectedDir = RootPath;
            if (string.IsNullOrWhiteSpace(selectedDir))
            {
                Models.Notification.Send(null, App.Text("ScanRepositories.NoRoot"), true);
                return false;
            }
            if (!Directory.Exists(selectedDir))
            {
                Models.Notification.Send(null, App.Text("TreeGit.Message.PathMissing"), true);
                return false;
            }

            SetProgressDescription("TreeGit.Progress.ScanRepositories", selectedDir);

            var minDelay = Task.Delay(500);
            var rootDir = new DirectoryInfo(selectedDir);
            var found = new List<string>();

            await GetUnmanagedRepositoriesAsync(rootDir, found, new EnumerationOptions()
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = true,
            });

            // Make sure this task takes at least 0.5s to avoid the popup panel disappearing too quickly.
            await minDelay;

            var normalizedRoot = rootDir.FullName.Replace('\\', '/').TrimEnd('/');
            for (var i = 0; i < found.Count; i++)
            {
                var f = found[i];
                SetProgressDescription("TreeGit.Progress.ScanRepositories.Register", i + 1, found.Count, f);
                var parent = new DirectoryInfo(f).Parent!.FullName.Replace('\\', '/').TrimEnd('/');
                if (parent.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(f, null, false, false);
                    await node.UpdateStatusAsync(false, null);
                }
                else if (parent.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = parent.Substring(normalizedRoot.Length).TrimStart('/');
                    var group = Preferences.Instance.FindOrCreateGroupRecursive(relative);
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(f, group, false, false);
                    await node.UpdateStatusAsync(false, null);
                }
            }

            Preferences.Instance.AutoRemoveInvalidNode();
            Preferences.Instance.Save();
            Welcome.Instance.Refresh();

            if (_untrusted > 0)
                Models.Notification.Send(null, App.Text("TrustRepository.ScanSkipped", _untrusted));

            return true;
        }

        private void GetManagedRepositories(List<RepositoryNode> group, HashSet<string> repos)
        {
            foreach (var node in group)
            {
                if (node.IsRepository)
                    repos.Add(OperatingSystem.IsLinux() ? node.Id : node.Id.ToLower(CultureInfo.CurrentCulture));
                else
                    GetManagedRepositories(node.SubNodes, repos);
            }
        }

        private async Task GetUnmanagedRepositoriesAsync(DirectoryInfo dir, List<string> outs, EnumerationOptions opts, int depth = 0)
        {
            var subdirs = dir.GetDirectories("*", opts);
            foreach (var subdir in subdirs)
            {
                if (subdir.Name.StartsWith(".", StringComparison.Ordinal) ||
                    subdir.Name.Equals("node_modules", StringComparison.Ordinal))
                    continue;

                SetProgressDescription("TreeGit.Progress.ScanRepositories.Folder", subdir.FullName);

                var normalizedSelf = subdir.FullName.Replace('\\', '/').TrimEnd('/');
                if (IsManaged(normalizedSelf))
                    continue;

                var gitDir = Path.Combine(subdir.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                {
                    var test = await new Commands.QueryRepositoryRootPath(subdir.FullName).GetResultAsync();
                    if (test.IsSuccess && !string.IsNullOrEmpty(test.StdOut))
                    {
                        var normalized = test.StdOut.Trim().Replace('\\', '/').TrimEnd('/');
                        if (!IsManaged(normalized))
                            outs.Add(normalized);
                    }
                    else if (Models.SafeDirectories.IsUntrustedRepository(test.StdErr))
                    {
                        _untrusted++;
                    }

                    continue;
                }

                var isBare = await new Commands.IsBareRepository(subdir.FullName).GetResultAsync();
                if (isBare)
                {
                    outs.Add(normalizedSelf);
                    continue;
                }

                if (depth < 5)
                    await GetUnmanagedRepositoriesAsync(subdir, outs, opts, depth + 1);
            }
        }

        private bool IsManaged(string path)
        {
            if (OperatingSystem.IsLinux())
                return _managed.Contains(path);

            return _managed.Contains(path.ToLower(CultureInfo.CurrentCulture));
        }

        private HashSet<string> _managed = new();
        private string _rootPath;
        private int _untrusted = 0;
    }
}
