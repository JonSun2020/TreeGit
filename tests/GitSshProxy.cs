using System;
using System.Diagnostics;
using System.IO;

// Test-only: run real Git, adding an isolated SSH config to the app's own SSH command.
var executable = Environment.GetEnvironmentVariable("TREEGIT_TEST_REAL_GIT");
var configuration = Environment.GetEnvironmentVariable("TREEGIT_TEST_SSH_CONFIG");
if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(configuration)) return 90;
var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var argument in args) start.ArgumentList.Add(argument);
var command = Environment.GetEnvironmentVariable("GIT_SSH_COMMAND");
if (!string.IsNullOrEmpty(command))
{
    start.Environment["GIT_SSH_COMMAND"] = command + " -F '" + configuration.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    var trace = Environment.GetEnvironmentVariable("TREEGIT_TEST_SSH_TRACE");
    if (!string.IsNullOrEmpty(trace)) File.AppendAllText(trace, command + Environment.NewLine);
}
using var process = Process.Start(start);
var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
await process.WaitForExitAsync();
await System.Threading.Tasks.Task.WhenAll(output, error);
return process.ExitCode;
