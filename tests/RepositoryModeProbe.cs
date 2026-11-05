using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    internal static class OS
    {
        public static string GitExecutable { get; set; }
    }
}

namespace SourceGit.Tests
{
    internal static class RepositoryModeProbe
    {
        private static async Task<(int Code, string Output, string Error)> Git(string[] args)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("TREEGIT_MODE_REAL_GIT")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error);
            await process.WaitForExitAsync();
            return (process.ExitCode, output.Result, error.Result);
        }

        public static async Task<int> Main(string[] args)
        {
            // This executable also acts as an owned Git wrapper, delaying the first config read.
            if (args[0] == "-C")
            {
                var result = await Git(args);
                if (Array.IndexOf(args, "sourcegit.credentialmanager") >= 0)
                {
                    var marker = Path.Combine(args[1], "observed.txt");
                    if (!File.Exists(marker))
                    {
                        File.WriteAllText(marker + ".tmp", result.Output);
                        File.Move(marker + ".tmp", marker);
                        var watch = Stopwatch.StartNew();
                        while (!File.Exists(Path.Combine(args[1], "release.txt")))
                        {
                            if (watch.Elapsed.TotalSeconds > 15) return 124;
                            await Task.Delay(10);
                        }
                    }
                }
                Console.Out.Write(result.Output); Console.Error.Write(result.Error);
                return result.Code;
            }

            var outputRoot = Path.GetFullPath(args[0]); Directory.CreateDirectory(outputRoot);
            Environment.SetEnvironmentVariable("TREEGIT_MODE_REAL_GIT", Path.GetFullPath(args[1]));
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(outputRoot, "global.gitconfig"));
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            Native.OS.GitExecutable = Environment.ProcessPath!;
            var records = new List<object>(); var success = true;
            foreach (var explicitSetting in new[] { false, true })
            {
                var repo = Path.Combine(outputRoot, explicitSetting ? "explicit" : "invalidated"); Directory.CreateDirectory(repo);
                if ((await Git(["init", "-b", "main", repo])).Code != 0 || (await Git(["-C", repo, "config", "sourcegit.credentialmanager", "true"])).Code != 0) throw new Exception("Fixture setup failed");
                var reading = Task.Run(() => Native.RepositoryCredentials.UsesCredentialManager(repo));
                try
                {
                    var watch = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(repo, "observed.txt")))
                    {
                        if (watch.Elapsed.TotalSeconds > 10) throw new Exception("The owned config read did not start");
                        await Task.Delay(10);
                    }
                    if ((await Git(["-C", repo, "config", "sourcegit.credentialmanager", "false"])).Code != 0) throw new Exception("Fixture update failed");
                    if (explicitSetting) Native.RepositoryCredentials.SetCredentialManagerMode(repo, false);
                    else Native.RepositoryCredentials.InvalidateMode(repo);
                }
                finally { File.WriteAllText(Path.Combine(repo, "release.txt"), "continue"); }
                var original = await reading;
                var current = Native.RepositoryCredentials.UsesCredentialManager(repo);
                success &= !current;
                records.Add(new { explicitSetting, oldRead = original, currentMode = current, success = !current });
                Console.WriteLine((current ? "FAIL " : "PASS ") + (explicitSetting ? "explicit mode setting wins over an in-flight read" : "invalidation prevents an in-flight old value from becoming the cache"));
            }
            File.WriteAllText(Path.Combine(outputRoot, "repository-mode-results.json"), JsonSerializer.Serialize(new { success, records }));
            return success ? 0 : 1;
        }
    }
}
