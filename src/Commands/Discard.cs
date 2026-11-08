using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class Discard
    {
        public static async Task<bool> AllAsync(string repo, bool includeModified, bool includeUntracked, bool includeIgnored, Models.ICommandLog log)
        {
            // Validate before deleting anything; failure never means clean.
            var query = new QueryLocalChanges(repo);
            await query.GetResultAsync().ConfigureAwait(false);
            if (!query.Succeeded) return Failed(repo, "WorkingCopy.RefreshFailed");
            if (includeModified && !await new CheckRestoreSafety(repo, true).GetResultAsync().ConfigureAwait(false))
                return Failed(repo, "Discard.ProtectedFiles");

            // Native clean protects nested repositories and handles links itself.
            // Never bypass those safeguards with recursive Directory.Delete.
            if (includeUntracked || includeIgnored)
            {
                var mode = includeUntracked ? includeIgnored ? Models.CleanMode.UntrackedAndIgnoredFiles : Models.CleanMode.OnlyUntrackedFiles : Models.CleanMode.OnlyIgnoredFiles;
                if (!await new Clean(repo, mode).Use(log).ExecAsync().ConfigureAwait(false)) return false;
            }
            return !includeModified || await new Reset(repo, "", "--hard").Use(log).ExecAsync().ConfigureAwait(false);
        }

        public static async Task<bool> ChangesAsync(string repo, List<Models.Change> changes, Models.ICommandLog log)
        {
            var restores = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
                if (change.WorkTree is not (Models.ChangeState.Untracked or Models.ChangeState.Added)) restores.Add(change.Path);
            if (restores.Count > 0 && !await new CheckRestoreSafety(repo, false, restores).GetResultAsync().ConfigureAwait(false))
                return Failed(repo, "Discard.ProtectedFiles");

            string pathSpecFile = null;
            try
            {
                foreach (var change in changes)
                {
                    if ((change.WorkTree is Models.ChangeState.Untracked or Models.ChangeState.Added) &&
                        !await new Clean(repo, Models.CleanMode.OnlyUntrackedFiles, change.Path).Use(log).ExecAsync().ConfigureAwait(false)) return false;
                }
                if (restores.Count == 0) return true;
                pathSpecFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(pathSpecFile, string.Join('\0', restores) + '\0').ConfigureAwait(false);
                return await new Restore(repo, pathSpecFile, true).Use(log).ExecAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.OS.LogException(ex);
                return Failed(repo, "Discard.Failed");
            }
            finally
            {
                if (pathSpecFile != null)
                {
                    try { File.Delete(pathSpecFile); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Native.OS.LogException(ex); }
                }
            }
        }

        private static bool Failed(string repo, string key)
        {
            Models.Notification.Send(repo, App.Text(key), true);
            return false;
        }
    }
}
