using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class ProcessCancellationProbe
    {
        private sealed class ReadQuery : Commands.Command
        {
            public Task<Result> ReadAsync() => ReadToEndAsync();
        }

        public static async Task<int> RunReadAsync(string root, string report)
        {
            if (Environment.GetEnvironmentVariable("CI") != "true" || !Path.IsPathFullyQualified(root) || Directory.Exists(root))
                throw new InvalidOperationException("Use a new, owned CI fixture for cancellation checks.");
            Directory.CreateDirectory(root);
            Native.OS.GitExecutable = Native.OS.FindGitExecutable();
            using var cancellation = new CancellationTokenSource(500);
            var clock = Stopwatch.StartNew();
            var cancelledRead = false;
            try
            {
                await new ReadQuery { WorkingDirectory = root, UseCredentialManager = false,
                    Args = "-c alias.treegit-cancel-read=\"!printf started > started; sleep 2; printf leaked > leaked\" treegit-cancel-read",
                    CancellationToken = cancellation.Token, RaiseError = false }.ReadAsync();
            }
            catch (OperationCanceledException) { cancelledRead = true; }
            var elapsed = clock.Elapsed.TotalSeconds;
            // The old reader cancels its await but leaves Git and the shell alive.
            // Give that owned, short-lived child time to finish before cleanup.
            await Task.Delay(2500);
            var started = File.Exists(Path.Combine(root, "started"));
            var leaked = File.Exists(Path.Combine(root, "leaked"));
            var afterwards = await new Commands.Command { WorkingDirectory = root, Args = "--version", RaiseError = false }.ExecAsync();
            var success = cancelledRead && elapsed < 3 && started && !leaked && afterwards;
            File.WriteAllText(report, JsonSerializer.Serialize(new { success, cancelledRead, elapsed, started, leaked, subsequentGitCommand = afterwards }));
            return success ? 0 : 1;
        }

        public static int Run(int pid, int sentinelPid, string expectedExecutable, string report)
        {
            if (GetConsoleProcessList(new uint[32], 32) != 0)
                throw new InvalidOperationException("The diagnostic caller must have no console of its own.");
            using var process = Process.GetProcessById(pid);
            using var sentinel = Process.GetProcessById(sentinelPid);
            if (!string.Equals(process.MainModule?.FileName, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The cancellation target is not the owned test helper.");
            if (pid == sentinelPid || !string.Equals(sentinel.MainModule?.FileName, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sentinel is not a separate owned test helper.");
            Native.OS.TerminateProcess(process);
            var ended = process.WaitForExit(5000);
            System.Threading.Thread.Sleep(200);
            File.WriteAllText(report, JsonSerializer.Serialize(new { target = pid, ended, sentinelAlive = !sentinel.HasExited }));
            return ended && !sentinel.HasExited ? 0 : 1;
        }
        public static async Task<int> RunGitAsync(string root, string report)
        {
            Directory.CreateDirectory(root);
            Native.OS.GitExecutable = Native.OS.FindGitExecutable();
            using var cancellation = new CancellationTokenSource(500);
            var clock = Stopwatch.StartNew();
            await new Commands.Command { WorkingDirectory = root, Args = "-c alias.treegit-cancel=\"!sleep 30\" treegit-cancel", CancellationToken = cancellation.Token, RaiseError = false }.ExecAsync();
            var elapsed = clock.Elapsed.TotalSeconds;
            var afterwards = await new Commands.Command { WorkingDirectory = root, Args = "--version", RaiseError = false }.ExecAsync();
            var success = cancellation.IsCancellationRequested && elapsed < 8 && afterwards;
            File.WriteAllText(report, JsonSerializer.Serialize(new { success, cancelled = cancellation.IsCancellationRequested, elapsed, subsequentGitCommand = afterwards }));
            return success ? 0 : 1;
        }
        [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
    }
}
