using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCurrentBranchCommitHashes : Command
    {
        public bool Succeeded { get; private set; }

        public QueryCurrentBranchCommitHashes(string repo, ulong sinceTimestamp)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = sinceTimestamp == 0 ? "log --format=%H" : $"log --since=@{sinceTimestamp} --format=%H";
        }

        public async Task<HashSet<string>> GetResultAsync()
        {
            Succeeded = false;
            var outs = new HashSet<string>();

            try
            {
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                Native.ProcessLauncher.Start(proc);
                using var cancellation = CancellationToken.Register(() =>
                {
                    try { if (!proc.HasExited) Native.OS.TerminateProcess(proc); }
                    catch (System.InvalidOperationException) { }
                });
                proc.BeginErrorReadLine();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { Length: > 8 } line)
                    outs.Add(line);

                await proc.WaitForExitAsync().ConfigureAwait(false);
                Succeeded = proc.ExitCode == 0 && !CancellationToken.IsCancellationRequested;
            }
            catch
            {
                // Ignore exceptions;
            }

            return Succeeded ? outs : [];
        }
    }
}
