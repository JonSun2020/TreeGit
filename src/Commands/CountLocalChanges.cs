using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;

namespace SourceGit.Commands
{
    public class CountLocalChanges : Command
    {
        public bool Succeeded { get; private set; }
        public CountLocalChanges(string repo, bool includeUntracked)
        {
            var option = includeUntracked ? "-uall" : "-uno";
            WorkingDirectory = repo;
            Context = repo;
            Args = $"--no-optional-locks status {option} --ignore-submodules=all --porcelain";
        }

        public async Task<int> GetResultAsync()
        {
            Succeeded = false;
            if (CancellationToken.IsCancellationRequested) return 0;
            try
            {
                using var process = new Process { StartInfo = CreateGitStartInfo(true) };
                Native.ProcessLauncher.Start(process);
                var gate = new object();
                var running = true;
                using var registration = CancellationToken.Register(() =>
                {
                    lock (gate)
                        if (running && !process.HasExited) Native.OS.TerminateProcess(process);
                });
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                var buffer = new char[16384];
                var count = 0;
                var content = false;
                int read;
                // Count records without retaining every path or allocating a
                // second array for a large untracked directory.
                while ((read = await process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] is '\r' or '\n')
                        {
                            if (content) count++;
                            content = false;
                        }
                        else content = true;
                    }
                }
                if (content) count++;
                await process.WaitForExitAsync().ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                lock (gate) running = false;
                Succeeded = process.ExitCode == 0 && !CancellationToken.IsCancellationRequested;
                return Succeeded ? count : 0;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { Native.OS.LogException(ex); }
            return 0;
        }
    }
}
