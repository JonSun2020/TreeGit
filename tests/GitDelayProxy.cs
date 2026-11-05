using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Test-only wrapper: pause the initial branch-list query while forwarding every real Git operation.
var executable = Environment.GetEnvironmentVariable("TREEGIT_TEST_REAL_GIT");
var gate = Environment.GetEnvironmentVariable("TREEGIT_TEST_BRANCH_GATE");
if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(gate)) return 90;
if (args.Contains("branch") && args.Contains("--all") && args.Any(argument => argument.StartsWith("--format=", StringComparison.Ordinal)))
{
    File.WriteAllText(gate + ".observed", "branch query started");
    var watch = Stopwatch.StartNew();
    while (!File.Exists(gate + ".release"))
    {
        if (watch.Elapsed.TotalSeconds > 25) return 124;
        await Task.Delay(10);
    }
}
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var argument in args) start.ArgumentList.Add(argument);
using var process = Process.Start(start)!;
var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
await process.WaitForExitAsync();await Task.WhenAll(output,error);
return process.ExitCode;
