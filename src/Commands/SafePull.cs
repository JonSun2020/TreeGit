using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Fetch once, validate the exact fetched trees, then integrate immutable IDs.
    // Preserve pull's pre-fetch fork point so rewritten upstream commits are not
    // accidentally replayed. No mutation is cancelled after integration starts.
    internal static class SafePull
    {
        private sealed class Read(string repo, string arguments) : Command
        {
            public async Task<Result> GetAsync()
            {
                WorkingDirectory = repo;
                Args = arguments;
                return await ReadToEndAsync().ConfigureAwait(false);
            }
        }

        public static async Task<Command.Result> RunAsync(string repo, string gitDir, Models.Remote remote, Models.Branch target,
            string trackingRef, bool rebase, bool ffOnly, CancellationToken cancellation, Models.ICommandLog log, Action integrating = null)
        {
            string snapshotFile = null, messageFile = null;
            try
            {
                var originalHead = await new QueryRevisionByRefName(repo, "HEAD").GetResultAsync().ConfigureAwait(false);
                var originalBranch = await new QuerySymbolicHead(repo).GetResultAsync().ConfigureAwait(false);
                if (!originalBranch.IsSuccess && originalBranch.ExitCode != 1) return Failed("WorkingCopy.RefreshFailed");
                string fork = null;
                if (rebase && !string.IsNullOrEmpty(trackingRef) && originalHead != null)
                {
                    var result = await new Read(repo, $"merge-base --fork-point {trackingRef.Quoted()} {originalHead.Quoted()}").GetAsync().ConfigureAwait(false);
                    if (result.IsSuccess && IsHash(result.StdOut.Trim())) fork = result.StdOut.Trim();
                }
                var fetch = new Fetch(repo, remote) { RaiseError = false, CancellationToken = cancellation };
                if (target != null) fetch.Args += " " + target.Name.Quoted();
                if (!await fetch.Use(log).ExecAsync().ConfigureAwait(false)) return From(fetch, false);
                if (cancellation.IsCancellationRequested) return Failed("Pull.Cancelled");

                var currentHead = await new QueryRevisionByRefName(repo, "HEAD").GetResultAsync().ConfigureAwait(false);
                var currentBranch = await new QuerySymbolicHead(repo).GetResultAsync().ConfigureAwait(false);
                if ((!currentBranch.IsSuccess && currentBranch.ExitCode != 1) || currentHead != originalHead ||
                    currentBranch.IsSuccess != originalBranch.IsSuccess || currentBranch.StdOut.Trim() != originalBranch.StdOut.Trim())
                    return Failed("Checkout.DestinationChanged");

                var snapshot = await ReadSnapshotAsync(Path.Combine(gitDir, "FETCH_HEAD")).ConfigureAwait(false);
                var heads = new List<string>();
                using (var reader = new StringReader(snapshot))
                {
                    while (reader.ReadLine() is { } line)
                    {
                        var fields = line.Split('\t', 3);
                        if (fields.Length < 3 || !IsHash(fields[0])) return Failed("Pull.StateUnavailable");
                        if (fields[1].Length == 0 && !heads.Contains(fields[0])) heads.Add(fields[0]);
                    }
                }
                if (heads.Count == 0 || rebase && heads.Count != 1) return Failed("Pull.NoMergeHead");
                var canFastForward = heads.Count == 1 && (currentHead == null || await new IsAncestor(repo, currentHead, heads[0]).GetResultAsync().ConfigureAwait(false));
                var willReplay = rebase && !ffOnly && !canFastForward && currentHead != null;
                if (willReplay && fork != null)
                {
                    var common = await new Read(repo, $"merge-base --octopus {currentHead.Quoted()} {heads[0].Quoted()} {fork.Quoted()}").GetAsync().ConfigureAwait(false);
                    if (common.IsSuccess && common.StdOut.Trim() == fork) fork = null;
                }
                var safety = new CheckMergeSafety(repo, heads, replayRange: willReplay ? $"{fork ?? heads[0]}..{currentHead}" : null);
                if (!await safety.GetResultAsync().ConfigureAwait(false))
                {
                    var message = App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "Merge.LocalFilesBlocked") +
                        (safety.Obstruction == null ? string.Empty : "\n" + safety.Obstruction);
                    return Command.Result.Failed(message, message);
                }
                if (!await new CheckRestoreSafety(repo, true).GetResultAsync().ConfigureAwait(false)) return Failed("Merge.LocalFilesBlocked");

                Command integrate;
                // Native pull initializes an unborn branch directly, regardless
                // of pull.ff=false; there is no local history to merge yet.
                if (currentHead == null || ffOnly || rebase && canFastForward)
                    integrate = new Merge(repo, heads, true, null, mode: "--ff-only");
                else if (rebase)
                {
                    integrate = new Rebase(repo, heads[0], fork ?? heads[0]);
                }
                else
                {
                    var config = await new Read(repo, "config --type=bool-or-str --get pull.ff").GetAsync().ConfigureAwait(false);
                    if (!config.IsSuccess && config.ExitCode != 1) return Failed("Pull.StateUnavailable");
                    var mode = config.IsSuccess ? config.StdOut.Trim() switch
                    {
                        "true" or "only" => "--ff", "false" => "--no-ff", _ => null,
                    } : string.Empty;
                    if (mode == null) return Failed("Pull.StateUnavailable");
                    snapshotFile = Path.Combine(gitDir, $"treegit-pull-{Guid.NewGuid():N}.fetch");
                    messageFile = Path.Combine(gitDir, $"treegit-pull-{Guid.NewGuid():N}.message");
                    await File.WriteAllTextAsync(snapshotFile, snapshot, new UTF8Encoding(false)).ConfigureAwait(false);
                    var message = await new Read(repo, $"fmt-merge-msg -F {snapshotFile.Quoted()}").GetAsync().ConfigureAwait(false);
                    if (!message.IsSuccess) return Failed("Pull.StateUnavailable");
                    await File.WriteAllTextAsync(messageFile, message.StdOut, new UTF8Encoding(false)).ConfigureAwait(false);
                    integrate = new Merge(repo, heads, true, null, mode: mode, messageFile: messageFile);
                }
                if (cancellation.IsCancellationRequested) return Failed("Pull.Cancelled");
                integrating?.Invoke();
                integrate.RaiseError = false;
                var success = await integrate.Use(log).ExecAsync().ConfigureAwait(false);
                return From(integrate, success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Native.OS.LogException(ex);
                return Failed("Pull.StateUnavailable");
            }
            finally
            {
                foreach (var file in new[] { snapshotFile, messageFile })
                {
                    if (file == null) continue;
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Native.OS.LogException(ex); }
                }
            }
        }

        private static async Task<string> ReadSnapshotAsync(string file)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            var buffer = new char[16384];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (text.Length + count > 4 * 1024 * 1024) throw new IOException("Fetched reference list exceeded the bounded snapshot size.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        private static bool IsHash(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
        private static Command.Result Failed(string key) => Command.Result.Failed(App.Text(key), App.Text(key));
        private static Command.Result From(Command command, bool success) => new()
        {
            IsSuccess = success, ExitCode = command.LastExitCode, StdErr = success ? string.Empty : command.FailureMessage,
            UserMessage = success ? null : App.Text("Pull.Failed"),
        };
    }
}
