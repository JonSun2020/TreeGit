using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal sealed partial class ExportCommitReport(string repository, IReadOnlyList<ExportCommitReport.Selection> commits,
        int context, string reportName, ExportCommitReport.Labels labels)
    {
        internal sealed record Selection(string Sha, string Subject, string Date, string Author, string Branch, bool BranchLookupFailed);
        internal sealed record Labels(string Language, string SelectCommit, string Context, string Author, string Date,
            string Parents, string Changes, string FirstParent, string EmptyTree, string NoChanges,
            string Filter, string NoMatches, string Wrap, string Binary, string Added, string Removed,
            string Revision, string OldVersion, string NewVersion, string PreviousChange, string NextChange, string Files,
            string Description, string CommitDate, string CommitColumn, string ResizeColumn,
            string Branch, string OriginalBranchUnknown, string BranchLookupFailed, string ResizePane, string Ordinal);
        private sealed record Change(string Status, string Path, string PreviousPath, long? Additions, long? Deletions)
        {
            public bool IsBinary => !Additions.HasValue;
        }
        private readonly record struct PatchLine(char Marker, string OldNumber, string NewNumber, string Text);
        private readonly HashSet<string> _commitIds = new(commits.Select(c => c.Sha), StringComparer.Ordinal);

        public async Task WriteAsync(string destination, CancellationToken cancellation)
        {
            if (context is not (20 or 50 or 100) || commits.Count == 0)
                throw new ArgumentException("Invalid commit report selection or context size.");

            foreach (var commit in commits)
                if (!IsObjectId(commit.Sha))
                    throw new ArgumentException("Invalid commit object ID.");

            destination = Path.GetFullPath(destination);
            var staged = Path.Combine(Path.GetDirectoryName(destination)!, $".treegit-report-{Guid.NewGuid():N}.tmp");
            var ownsStagedFile = false;
            try
            {
                await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    ownsStagedFile = true;
                    await using var html = new StreamWriter(stream, Encoding.UTF8, 65536);
                    WriteHeading(html);
                    foreach (var commit in commits)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await WriteCommitAsync(html, commit, cancellation).ConfigureAwait(false);
                    }
                    WriteFooter(html);
                }

                cancellation.ThrowIfCancellationRequested();
                File.Move(staged, destination, true);
                ownsStagedFile = false;
            }
            finally
            {
                if (ownsStagedFile)
                {
                    try { File.Delete(staged); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private async Task WriteCommitAsync(TextWriter html, Selection commit, CancellationToken cancellation)
        {
            var metadata = await ReadGitAsync(
                ["show", "--no-patch", "--no-notes", "--no-show-signature", "--no-decorate", "--encoding=UTF-8",
                    "--format=format:%H%x00%an%x00%aI%x00%P%x00%B", commit.Sha, "--"], cancellation).ConfigureAwait(false);
            var fields = metadata.Split('\0', 5);
            if (fields.Length != 5 || fields[0] != commit.Sha)
                throw new InvalidDataException("Git returned invalid commit metadata.");

            var parents = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 0)
            {
                // The pretty placeholders omit a shallow boundary's parents.
                // Raw format exposes the actual base so a truncated history
                // cannot pass off the entire tree as changes in that commit.
                var raw = await ReadGitAsync(
                    ["show", "--no-patch", "--no-notes", "--no-show-signature", "--no-decorate", "--format=raw", commit.Sha, "--"],
                    cancellation).ConfigureAwait(false);
                var realParents = new List<string>();
                using var lines = new StringReader(raw);
                while (lines.ReadLine() is { Length: > 0 } header)
                    if (header.StartsWith("parent ", StringComparison.Ordinal))
                        realParents.Add(header[7..]);
                parents = realParents.ToArray();
            }

            foreach (var parent in parents)
                if (!IsObjectId(parent))
                    throw new InvalidDataException("Git returned an invalid parent object ID.");

            // Both invocations use identical diff options and revisions, so
            // NUL-delimited paths and statistics can label the streamed patch in Git's order.
            string[] revisions = parents.Length == 0 ? [commit.Sha, "--"] : [parents[0], commit.Sha, "--"];
            string[] args = [
                .. (parents.Length == 0 ? new[] { "diff-tree", "--root", "--no-commit-id", "-r" } : ["diff"]),
                "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--ignore-submodules=none", "--submodule=short",
                "--find-renames=50%", "--diff-algorithm=myers", "--inter-hunk-context=0", "--src-prefix=a/", "--dst-prefix=b/"];
            var statistics = await ReadGitAsync([.. args, "--raw", "--numstat", "-z", .. revisions], cancellation).ConfigureAwait(false);
            var changes = ParseChanges(statistics);
            WriteCommitHeader(html, commit, fields[1], fields[2], fields[4], parents);
            WriteDiffLayout(html, commit.Sha, changes);
            if (changes.Count > 0)
            {
                await ReadGitAsync([.. args, "--patch", $"--unified={context.ToString(CultureInfo.InvariantCulture)}",
                        "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= ", .. revisions],
                    async reader =>
                    {
                        await WritePatchAsync(html, reader, commit.Sha, parents.FirstOrDefault(), changes, cancellation).ConfigureAwait(false);
                        return true;
                    }, cancellation).ConfigureAwait(false);
            }

            html.WriteLine("</section></div></article>");
        }

        private static List<Change> ParseChanges(string output)
        {
            var changes = new List<Change>();
            if (output.Length == 0)
                return changes;

            var fields = output.Split('\0');
            var i = 0;
            while (i < fields.Length - 1 && fields[i].StartsWith(':'))
            {
                var header = fields[i++];
                var status = header[(header.LastIndexOf(' ') + 1)..];
                if (status.Length == 0 || i >= fields.Length - 1)
                    throw new InvalidDataException("Git returned an invalid changed-file list.");

                var path = fields[i++];
                string previousPath = null;
                if (status[0] is 'R' or 'C')
                {
                    if (i >= fields.Length - 1)
                        throw new InvalidDataException("Git returned an incomplete rename or copy.");
                    previousPath = path;
                    path = fields[i++];
                }
                changes.Add(new Change(status, path, previousPath, null, null));
            }

            for (var index = 0; index < changes.Count; index++)
            {
                if (i >= fields.Length - 1)
                    throw new InvalidDataException("Git returned incomplete changed-file statistics.");
                var columns = fields[i++].Split('\t', 3);
                if (columns.Length != 3)
                    throw new InvalidDataException("Git returned invalid changed-file statistics.");
                var change = changes[index];
                var path = columns[2];
                if (path.Length == 0)
                {
                    if (i + 1 >= fields.Length - 1 || change.PreviousPath != fields[i++])
                        throw new InvalidDataException("Git returned inconsistent rename statistics.");
                    path = fields[i++];
                }
                if (path != change.Path)
                    throw new InvalidDataException("Git returned inconsistent changed-file statistics.");
                if (columns[0] == "-" && columns[1] == "-")
                    continue;
                if (!long.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) ||
                    !long.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                    throw new InvalidDataException("Git returned invalid diff line counts.");
                changes[index] = change with { Additions = added, Deletions = removed };
            }

            if (i != fields.Length - 1 || fields[i].Length != 0)
                throw new InvalidDataException("Git returned an unexpected changed-file entry.");
            return changes;
        }

        private async Task WritePatchAsync(TextWriter html, StreamReader patch, string sha, string baseline, List<Change> changes, CancellationToken cancellation)
        {
            var fileIndex = 0;
            long oldLine = 0, newLine = 0;
            var inHunk = false;
            var waitingForTypeChangeAddition = false;
            var pending = new List<PatchLine>();
            var pendingCharacters = 0;
            var highlightBlock = true;

            void FlushChanges(bool finishBlock = true)
            {
                var split = highlightBlock && pending.Count > 1 ? pending.FindIndex(line => line.Marker == '+') : -1;
                var paired = split > 0 && pending.Count == split * 2;
                for (var i = 0; i < pending.Count; i++)
                {
                    var line = pending[i];
                    WriteSourceLine(html, line, paired ? pending[(i + split) % pending.Count].Text : null);
                }
                pending.Clear();
                pendingCharacters = 0;
                highlightBlock = finishBlock;
            }

            while (await patch.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    FlushChanges();
                    inHunk = false;
                    // A type change is one file entry but two Git patch sections.
                    if (waitingForTypeChangeAddition)
                    {
                        waitingForTypeChangeAddition = false;
                        continue;
                    }
                    if (fileIndex == changes.Count)
                        throw new InvalidDataException("Git returned an unexpected patch section.");
                    if (fileIndex > 0)
                        EndFile(html, changes[fileIndex - 1]);

                    var change = changes[fileIndex];
                    WriteFileHeader(html, sha, baseline, fileIndex, change);
                    waitingForTypeChangeAddition = change.Status == "T";
                    fileIndex++;
                    continue;
                }

                if (fileIndex == 0 || changes[fileIndex - 1].IsBinary)
                    continue;
                if (line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    FlushChanges();
                    var hunk = HunkStart.Match(line);
                    inHunk = hunk.Success &&
                        long.TryParse(hunk.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out oldLine) &&
                        long.TryParse(hunk.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out newLine);
                    html.WriteLine($"""<div class="hunk"><code>{Encode(line)}</code></div>""");
                    continue;
                }
                if (inHunk && line.Length > 0)
                {
                    var marker = line[0];
                    if (marker is '+' or '-' or ' ')
                    {
                        var oldNumber = marker == '+' ? "" : (oldLine++).ToString(CultureInfo.InvariantCulture);
                        var newNumber = marker == '-' ? "" : (newLine++).ToString(CultureInfo.InvariantCulture);
                        var source = new PatchLine(marker, oldNumber, newNumber, line[1..]);
                        if (marker == ' ')
                        {
                            FlushChanges();
                            WriteSourceLine(html, source);
                        }
                        else if (highlightBlock && pending.Count < 128 && source.Text.Length <= 32768 - pendingCharacters)
                        {
                            pending.Add(source);
                            pendingCharacters += source.Text.Length;
                        }
                        else
                        {
                            highlightBlock = false;
                            FlushChanges(false);
                            WriteSourceLine(html, source);
                        }
                    }
                    else if (line.StartsWith("\\ ", StringComparison.Ordinal))
                    {
                        FlushChanges();
                        WritePatchNote(html, line[2..]);
                    }
                    continue;
                }

                var status = changes[fileIndex - 1].Status;
                if (status is not ("A" or "D") && (line.StartsWith("old mode ", StringComparison.Ordinal) ||
                    line.StartsWith("new mode ", StringComparison.Ordinal) || line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                    line.StartsWith("deleted file mode ", StringComparison.Ordinal)))
                    WritePatchNote(html, line);
            }

            if (fileIndex != changes.Count || waitingForTypeChangeAddition)
                throw new InvalidDataException("Git returned an incomplete patch.");
            FlushChanges();
            EndFile(html, changes[^1]);
        }

        private Task<string> ReadGitAsync(string[] args, CancellationToken cancellation) =>
            ReadGitAsync(args, reader => reader.ReadToEndAsync(cancellation), cancellation);

        private async Task<T> ReadGitAsync<T>(string[] args, Func<StreamReader, Task<T>> read, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var executable = Native.OS.GitExecutable;
            if (string.IsNullOrEmpty(executable))
                throw new InvalidOperationException("Git executable is unavailable.");

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "--no-pager", "--no-optional-locks", "--no-replace-objects",
                         "-c", "core.quotePath=false", "-c", "diff.suppressBlankEmpty=false" })
                start.ArgumentList.Add(arg);
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            // This environment variable overrides Git's explicit --unified option.
            start.Environment.Remove("GIT_DIFF_OPTS");
            start.Environment["GIT_NO_LAZY_FETCH"] = "1";
            start.Environment["GIT_ALLOW_PROTOCOL"] = "";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (OperatingSystem.IsWindows())
                Native.GitExecutableLocator.ConfigureProcess(start, executable);

            using var process = Native.ProcessLauncher.Start(start);
            using var stop = cancellation.Register(() => Stop(process));
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                var output = await read(process.StandardOutput).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
                var stderr = await error.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new IOException(string.IsNullOrWhiteSpace(stderr) ? $"Git exited with code {process.ExitCode}." : stderr.Trim());
                return output;
            }
            finally
            {
                Stop(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await error.ConfigureAwait(false);
            }
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) Native.OS.TerminateProcess(process); }
            catch (InvalidOperationException) { }
        }

        private static bool IsObjectId(string sha)
        {
            if (sha == null || sha.Length is not (40 or 64))
                return false;
            foreach (var c in sha)
                if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                    return false;
            return true;
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);

        private static readonly Regex HunkStart = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
