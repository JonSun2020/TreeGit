using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class RevisionFileProbe
    {
        private sealed class FullStorage : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Simulated full storage");
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("Simulated full storage"));
        }

        public static async Task<int> RunAsync(string root, string mode, string report)
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
            var before = Directory.GetFiles(Path.GetTempPath()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var target = Path.Combine(root, "existing export.bin");
            var original = "keep existing target";
            File.WriteAllText(target, original);
            var success = false;
            object details = null;
            switch (mode)
            {
                case "valid":
                case "missing":
                case "missing-worktree":
                case "closed":
                {
                    var vm = new ViewModels.BinaryFileViewer(root, mode == "missing-worktree" ? "missing.bin" : "sample.txt",
                        mode == "missing-worktree" ? null : mode == "missing" ? "refs/heads/missing" : "HEAD");
                    if (mode == "closed") vm.Cleanup();
                    await vm.LoadAsync();
                    success = mode == "valid" ? vm.Content != null && !vm.IsLoading &&
                        System.Text.Encoding.UTF8.GetString(vm.Content.Read(0, 7)) == "before\n" && vm.ErrorMessage.Length == 0 :
                        vm.Content == null && (mode == "closed" || !vm.IsLoading && vm.ErrorMessage.Length > 0);
                    details = new { vm.IsLoading, vm.ErrorMessage, size = vm.Content?.Size };
                    vm.Cleanup();
                    break;
                }
                case "close-loading":
                {
                    var vm = new ViewModels.BinaryFileViewer(root, "sample.txt", "HEAD");
                    var ready = Path.Combine(root, "reader.ready");
                    var previous = Native.ProcessLauncher.BeforeStart;
                    Native.ProcessLauncher.BeforeStart = (process, attempt) =>
                    {
                        previous?.Invoke(process, attempt);
                        if (!process.StartInfo.Arguments.StartsWith("show ", StringComparison.Ordinal)) return;
                        process.StartInfo.FileName = "powershell.exe";
                        process.StartInfo.Arguments = string.Empty;
                        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                            "[IO.File]::WriteAllText('" + ready.Replace("'", "''") + "',[string]$PID);Start-Sleep -Seconds 20" })
                            process.StartInfo.ArgumentList.Add(argument);
                    };
                    var close = Task.Run(async () =>
                    {
                        for (var i = 0; i < 400 && !File.Exists(ready); i++) await Task.Delay(25);
                        if (!File.Exists(ready)) throw new IOException("Owned export reader did not start.");
                        vm.Cleanup();
                    });
                    try { await vm.LoadAsync(); await close; }
                    finally { vm.Cleanup(); Native.ProcessLauncher.BeforeStart = previous; }
                    var pid = int.Parse(File.ReadAllText(ready));
                    var exited = false;
                    try { using var process = Process.GetProcessById(pid); exited = process.HasExited; }
                    catch (ArgumentException) { exited = true; }
                    success = exited && vm.Content == null && vm.ErrorMessage.Length == 0;
                    details = new { pid, exited, vm.ErrorMessage };
                    break;
                }
                case "cancelled":
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    try { await Commands.SaveRevisionFile.RunAsync(root, "HEAD", "sample.txt", target, cancellation.Token); }
                    catch (OperationCanceledException) { success = File.ReadAllText(target) == original; }
                    break;
                }
                case "snapshot-write-failure":
                {
                    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                        "$s=[Console]::OpenStandardOutput();$b=New-Object byte[] 65536;while($true){$s.Write($b,0,$b.Length);$s.Flush()}" })
                        start.ArgumentList.Add(argument);
                    using var process = Native.ProcessLauncher.Start(start);
                    using var output = new FullStorage();
                    try { await Commands.BinarySnapshots.CopyAsync(process, output, default); }
                    catch (IOException error) { success = error.Message == "Simulated full storage" && process.HasExited; }
                    details = new { process.Id, exited = process.HasExited };
                    break;
                }
                case "write-failure":
                    using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        success = !await Commands.SaveRevisionFile.RunAsync(root, "HEAD", "sample.txt", target);
                    success &= File.ReadAllText(target) == original && !Directory.GetFiles(root, "*.treegit-*.tmp").Any();
                    break;
                case "literal":
                {
                    var name = "固件 [1] old's.bin";
                    File.WriteAllText(Path.Combine(root, name), "literal binary\0content");
                    var add = new Commands.Command { WorkingDirectory = root, Args = $"--literal-pathspecs add -- {name.Quoted()}" };
                    if (!await add.ExecAsync()) throw new IOException("Literal file fixture could not be staged.");
                    var commit = new Commands.Command { WorkingDirectory = root, Args = "-c user.name=ExportQA -c user.email=export@example.invalid commit -m Literal" };
                    if (!await commit.ExecAsync()) throw new IOException("Literal file fixture could not be committed.");
                    success = await Commands.SaveRevisionFile.RunAsync(root, "HEAD", name, target) &&
                        File.ReadAllText(target) == "literal binary\0content";
                    break;
                }
                default: throw new ArgumentException(mode);
            }
            var leftovers = Directory.GetFiles(Path.GetTempPath()).Where(path => !before.Contains(path)).ToArray();
            success &= leftovers.Length == 0;
            File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success, details, leftovers }));
            return success ? 0 : 1;
        }
    }
}
