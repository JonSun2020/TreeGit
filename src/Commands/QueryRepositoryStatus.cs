using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryRepositoryStatus : Command
    {
        [GeneratedRegex(@"\+(\d+) \-(\d+)")]
        private static partial Regex REG_BRANCH_AB();

        public QueryRepositoryStatus(string repo)
        {
            WorkingDirectory = repo;
            RaiseError = false;
            Args = "--no-optional-locks status --porcelain=v2 -b -uall --ignore-submodules=dirty";
        }

        public async Task<Models.RepositoryStatus> GetResultAsync()
        {
            if (CancellationToken.IsCancellationRequested) return null;
            try
            {
                using var process = new Process { StartInfo = CreateGitStartInfo(true) };
                if (CancellationToken.IsCancellationRequested) return null;
                Native.ProcessLauncher.Start(process);
                using var registration = CancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) Native.OS.TerminateProcess(process); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                var errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                var status = new Models.RepositoryStatus();
                string oid = null;
                string head = null;
                // Only branch metadata and counts are needed for the Welcome row.
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                        oid = line[13..].Trim();
                    else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                        head = line[14..].Trim();
                    else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                        ParseTrackStatus(status, line[12..].Trim());
                    else if (line.Length > 0 && !line.StartsWith("# ", StringComparison.Ordinal))
                        status.LocalChanges++;
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await errorDrain.ConfigureAwait(false);
                if (process.ExitCode != 0 || CancellationToken.IsCancellationRequested || oid == null || head == null)
                    return null;

                status.CurrentBranch = head;
                if (head == "(detached)") status.CurrentBranch = oid.Length > 10 ? $"({oid.AsSpan(0, 10)})" : "-";
                return status;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        private void ParseTrackStatus(Models.RepositoryStatus status, string input)
        {
            var match = REG_BRANCH_AB().Match(input);
            if (match.Success)
            {
                status.Ahead = int.Parse(match.Groups[1].Value);
                status.Behind = int.Parse(match.Groups[2].Value);
            }
        }
    }
}
