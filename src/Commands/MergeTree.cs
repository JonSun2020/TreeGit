using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;

namespace SourceGit.Commands
{
    public class MergeTree : Command
    {
        public MergeTree(string repo, string source, string dest)
        {
            WorkingDirectory = repo;
            Args = $"merge-tree --write-tree {source.Quoted()} {dest.Quoted()}";
        }

        public async Task<int> GetExitCodeAsync()
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            var exitCode = -1;
            try
            {
                Native.ProcessLauncher.Start(proc);
                using var registration = CancellationToken.Register(() =>
                {
                    if (!proc.HasExited) Native.OS.TerminateProcess(proc);
                });
                await Task.WhenAll(proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken),
                    proc.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken),
                    proc.WaitForExitAsync(CancellationToken)).ConfigureAwait(false);
                exitCode = proc.ExitCode;
            }
            catch
            {
                // Ignore any exceptions and just return -1
            }

            return exitCode;
        }
    }
}
