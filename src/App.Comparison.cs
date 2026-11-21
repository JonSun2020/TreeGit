using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace SourceGit
{
    public partial class App
    {
        private static Window ComparisonOwner()
            => Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.Windows.LastOrDefault(w => w.IsVisible && w.IsActive) ?? desktop.MainWindow
                : null;

        internal static Views.MergeConflictEditor FindOpenMergeEditor(string repository, string file)
        {
            if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return desktop.Windows.OfType<Views.MergeConflictEditor>().FirstOrDefault(window => window.IsVisible &&
                window.DataContext is ViewModels.MergeConflictEditor editor &&
                editor.RepositoryPath.Replace('\\', '/').TrimEnd('/').Equals(repository.Replace('\\', '/').TrimEnd('/'), comparison) &&
                editor.FilePath.Replace('\\', '/') == file.Replace('\\', '/'));
        }

        public static void OpenInternalComparison(string repository, Models.DiffOption option, bool allowChanges = true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ComparisonOwner() is not { IsVisible: true } owner) return;
                var page = allowChanges && option.IsLocalChange ? FindComparisonRepositoryPage(repository) : null;
                var context = new ViewModels.DiffContext(repository, page == null ? option.AsDisplayOnly() : option, standalone: true);
                new Views.InternalDiffWindow(context, owner, page).Show(owner);
            });
        }

        private static ViewModels.LauncherPage FindComparisonRepositoryPage(string repository)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var path = repository.Replace('\\', '/').TrimEnd('/');
            return GetLauncher()?.Pages.FirstOrDefault(p => p.LoadedRepository is { WorkingCopy: not null } repo &&
                repo.FullPath.Replace('\\', '/').TrimEnd('/').Equals(path, comparison));
        }

        public static async Task<bool> OpenInternalMergeAsync(string repository, string file)
        {
            if (!Dispatcher.UIThread.CheckAccess())
                return await Dispatcher.UIThread.InvokeAsync(() => OpenInternalMergeAsync(repository, file));
            var repo = FindComparisonRepositoryPage(repository)?.LoadedRepository;
            if (repo?.WorkingCopy == null || ComparisonOwner() is not { IsVisible: true } owner) return false;
            var targets = string.IsNullOrEmpty(file)
                ? repo.WorkingCopy.Unstaged.Where(c => c.IsConflicted).Select(c => c.Path).Distinct().ToArray()
                : [file];
            foreach (var target in targets)
            {
                var entries = await new Commands.QueryConflictEntries(repository, target).GetResultAsync();
                if (entries == null) return false;
                if (entries.Length == 0) continue;
                var head = await new Commands.QuerySingleCommit(repository, "HEAD").GetResultAsync();
                if (head == null || !owner.IsVisible) return false;
                var editor = FindOpenMergeEditor(repository, target);
                var createWindow = editor == null;
                editor ??= new Views.MergeConflictEditor { DataContext = new ViewModels.MergeConflictEditor(repo, head, target) };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                editor.Closed += (_, _) => closed.TrySetResult();
                if (createWindow) editor.Show(owner);
                else
                {
                    if (editor.WindowState == WindowState.Minimized) editor.WindowState = WindowState.Normal;
                    editor.Activate();
                }
                await closed.Task;
                // Opening or cancelling an editor is not a successful merge.
                // Continue a batch only after the real index is resolved.
                entries = await new Commands.QueryConflictEntries(repository, target).GetResultAsync();
                if (entries == null || entries.Length != 0) return false;
            }
            return targets.Length > 0;
        }
    }
}
