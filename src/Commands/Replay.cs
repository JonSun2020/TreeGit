using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;

namespace SourceGit.Commands
{
    public class Replay : Command
    {
        public Replay(string repo, string onto, string range)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            // Git 2.56 changed replay's default to updating refs. This command is
            // exclusively a preview; the config is harmless on older versions.
            Args = $"-c replay.refAction=print replay --onto {onto.Quoted()} {range.Quoted()}";
        }

        public async Task<int> GetExitCodeAsync()
        {
            if (CancellationToken.IsCancellationRequested) return -1;
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            var exitCode = -1;
            try
            {
                Native.ProcessLauncher.Start(proc);
                var gate = new object();
                var running = true;
                using var registration = CancellationToken.Register(() =>
                {
                    lock (gate)
                        if (running && !proc.HasExited) Native.OS.TerminateProcess(proc);
                });
                var stdout = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                var stderr = proc.StandardError.BaseStream.CopyToAsync(Stream.Null);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                lock (gate) running = false;
                exitCode = CancellationToken.IsCancellationRequested ? -1 : proc.ExitCode;
            }
            catch
            {
                // Ignore any exceptions and just return -1
            }

            return exitCode;
        }
    }
}
