using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class PullSafetyProbe
    {
        private sealed class RecordLog(string file) : Models.ICommandLog
        {
            public void AppendLine(string line)
            {
                lock (_gate)
                {
                    var length = File.Exists(file) ? new FileInfo(file).Length : 0;
                    if (length < 65536) File.AppendAllText(file, (line ?? string.Empty)[..Math.Min(line?.Length ?? 0, 4096)] + "\n");
                }
            }
            private readonly object _gate = new();
        }

        public static async Task<int> RunAsync(string root, string mode, string report)
        {
            Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", "2020-01-02T03:04:05+00:00");
            Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "2020-01-02T03:04:05+00:00");
            Environment.SetEnvironmentVariable("GIT_MERGE_AUTOEDIT", "no");
            async Task<(int code, string output, string error)> Git(string directory, string arguments, bool requireSuccess = true)
            {
                using var process = new Process { StartInfo = new ProcessStartInfo(Native.OS.GitExecutable, arguments)
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                } };
                Native.ProcessLauncher.Start(process);
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
                var result = (process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
                if (requireSuccess && result.ExitCode != 0) throw new IOException(arguments + "\n" + result.Item3);
                return result;
            }
            async Task Configure(string directory)
            {
                await Git(directory, "config user.name PullQA");
                await Git(directory, "config user.email qa@example.invalid");
                await Git(directory, "config core.autocrlf false");
                await Git(directory, "config commit.gpgsign false");
            }
            async Task<string> Commit(string directory, string name, string contents, string message)
            {
                File.WriteAllText(Path.Combine(directory, name), contents, new UTF8Encoding(false));
                await Git(directory, "add -- " + name.Quoted());
                await Git(directory, "commit -m " + message.Quoted());
                return (await Git(directory, "rev-parse HEAD")).output;
            }

            var publisher = Path.Combine(root, "publisher"); Directory.CreateDirectory(publisher);
            var remote = Path.Combine(root, "remote.git");
            var client = Path.Combine(root, "client"); var native = Path.Combine(root, "native");
            await Git(publisher, "init -b main"); await Configure(publisher);
            var initial = await Commit(publisher, "values.txt", "base\n", "Initial");
            if (mode == "rewritten-upstream") await Commit(publisher, "values.txt", "old upstream\n", "Old upstream");
            await Git(root, "clone --bare " + publisher.Quoted() + " " + remote.Quoted());
            await Git(publisher, "remote add origin " + remote.Quoted());
            await Git(root, "clone " + remote.Quoted() + " " + client.Quoted());
            await Git(root, "clone " + remote.Quoted() + " " + native.Quoted());
            await Configure(client); await Configure(native);
            var divergent = mode is "merge" or "rebase" or "rewritten-upstream" or "ff-only" or "rebase-history-ignored";
            if (divergent)
            {
                var local = await Commit(client, "local.txt", "local work\n", "Local edit");
                if (mode == "rebase-history-ignored")
                {
                    await Commit(client, "transient.data", "temporary tracked content\n", "Add transient");
                    await Git(client, "rm transient.data"); await Git(client, "commit -m RemoveTransient");
                    local = (await Git(client, "rev-parse HEAD")).output;
                }
                await Git(native, "fetch " + client.Quoted() + " main");
                await Git(native, "reset --hard " + local);
            }
            if (mode == "rewritten-upstream") await Git(publisher, "reset --hard " + initial);
            var incomingFile = mode == "ignored" ? "ignored.data" : mode == "rewritten-upstream" ? "values.txt" : "remote.txt";
            await Commit(publisher, incomingFile, "new remote content\n", "Remote edit");
            await Git(publisher, "push " + (mode == "rewritten-upstream" ? "--force " : "") + "origin main");
            if (mode.StartsWith("config-", StringComparison.Ordinal))
            {
                var value = mode[7..];
                await Git(client, "config pull.ff " + value); await Git(native, "config pull.ff " + value);
            }
            var unborn = mode.StartsWith("unborn", StringComparison.Ordinal);
            if (unborn)
            {
                foreach (var directory in new[] { client, native })
                {
                    await Git(directory, "symbolic-ref HEAD refs/heads/unborn");
                    await Git(directory, "read-tree --empty");
                    await Git(directory, "clean -fdx");
                    if (mode == "unborn-no-ff") await Git(directory, "config pull.ff false");
                    if (mode == "unborn-untracked") File.WriteAllText(Path.Combine(directory, "keep.txt"), "preserve local file\n");
                }
            }
            var beforeResult = await Git(client, "rev-parse --verify --quiet HEAD", false);
            var before = beforeResult.code == 0 ? beforeResult.output : null;
            var beforeIndex = (await Git(client, "ls-files --stage")).output;
            string hook = null;
            if (mode == "ignored")
            {
                File.WriteAllText(Path.Combine(client, ".git/info/exclude"), "ignored.data\n");
                File.WriteAllText(Path.Combine(client, "ignored.data"), "preserve local ignored\n");
            }
            if (mode == "rebase-history-ignored")
            {
                File.WriteAllText(Path.Combine(client, ".git/info/exclude"), "*.data\n");
                File.WriteAllText(Path.Combine(client, "transient.data"), "preserve ignored local content\n");
            }
            if (mode == "fetch-hook")
            {
                hook = Path.Combine(client, ".git/hooks/reference-transaction");
                File.WriteAllText(hook, "#!/bin/sh\nif test \"$1\" = prepared; then\n echo invoked >> .git/fetch-hook-calls\n echo \"fatal: Authentication failed for 'https://example.invalid/qa'\" >&2\n exit 1\nfi\n", new UTF8Encoding(false));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var rebase = mode is "rebase" or "rewritten-upstream" or "rebase-history-ignored";
            using var cancel = new CancellationTokenSource();
            if (mode == "cancelled") cancel.Cancel();
            Native.ProcessLauncher.BeforeStart = (process, attempt) => File.AppendAllText(report + ".trace.log", process.StartInfo.Arguments + "\n");
            var outcome = await Commands.SafePull.RunAsync(client, Path.Combine(client, ".git"), new Models.Remote { Name = "origin" },
                unborn ? new Models.Branch { Name = "main" } : null, "refs/remotes/origin/main", rebase, mode == "ff-only", cancel.Token, new RecordLog(report + ".output.log"));
            Native.ProcessLauncher.BeforeStart = null;
            if (hook != null) File.Delete(hook);
            var afterResult = await Git(client, "rev-parse --verify --quiet HEAD", false);
            var after = afterResult.code == 0 ? afterResult.output : null;
            bool success;
            string expected = null;
            if (mode is "ignored" or "cancelled" or "fetch-hook" or "ff-only" or "rebase-history-ignored")
            {
                success = !outcome.IsSuccess && before == after && (await Git(client, "ls-files --stage")).output == beforeIndex;
                if (mode == "ignored") success &= File.ReadAllText(Path.Combine(client, "ignored.data")) == "preserve local ignored\n";
                if (mode == "fetch-hook") success &= File.ReadAllLines(Path.Combine(client, ".git/fetch-hook-calls")).Length == 1;
                if (mode == "fetch-hook") success &= Native.RepositoryCredentials.IsAuthenticationFailure("fatal: Authentication failed for 'https://example.invalid/hook.git'");
                if (mode == "rebase-history-ignored") success &= File.ReadAllText(Path.Combine(client, "transient.data")) == "preserve ignored local content\n" && outcome.UserMessage?.Contains("transient.data", StringComparison.Ordinal) == true;
            }
            else
            {
                await Git(native, "pull --no-edit --rebase=" + (rebase ? "true" : "false") + " origin" + (unborn ? " main" : string.Empty));
                expected = (await Git(native, "rev-parse HEAD")).output;
                success = outcome.IsSuccess && after == expected;
                if (mode == "unborn-untracked") success &= File.ReadAllText(Path.Combine(client, "keep.txt")) == "preserve local file\n";
            }
            success &= Directory.GetFiles(Path.Combine(client, ".git"), "treegit-pull-*").Length == 0;
            File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success, outcome.IsSuccess, outcome.ExitCode, outcome.StdErr, actual = after, expected }));
            return success ? 0 : 1;
        }
    }
}
