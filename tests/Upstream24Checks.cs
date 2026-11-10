using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class Upstream24Checks
    {
        public static async Task RunAsync(string root, Action<string, bool> check)
        {
            var repo = Path.Combine(root, "upstream24");
            Directory.CreateDirectory(repo);
            async Task<string> Git(params string[] args)
            {
                var start = new ProcessStartInfo(Native.OS.GitExecutable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = repo,
                };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(output, error, process.WaitForExitAsync());
                if (process.ExitCode != 0) throw new InvalidOperationException(await error);
                return (await output).TrimEnd();
            }
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Upstream QA");
            await Git("config", "user.email", "upstream@example.invalid");
            await Git("config", "core.autocrlf", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "ignored.data\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "value.txt"), "base\n");
            await Git("add", "."); await Git("commit", "-m", "Base");
            var baseline = await Git("rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Combine(repo, "untracked1"), "one");
            await File.WriteAllTextAsync(Path.Combine(repo, "untracked2"), "two");
            check("upstream24: local changes are counted without a tracking branch",
                (await new Commands.QueryRepositoryStatus(repo).GetResultAsync()).LocalChanges == 2);
            File.Delete(Path.Combine(repo, "untracked1")); File.Delete(Path.Combine(repo, "untracked2"));
            await Git("checkout", "-b", "topic");
            await File.WriteAllTextAsync(Path.Combine(repo, "value.txt"), "changed\n");
            await Git("commit", "-am", "Topic update");
            var tip = await Git("rev-parse", "HEAD");
            await Git("tag", "topic", baseline);
            var remotePath = Path.Combine(root, "remote.git");
            await Git("init", "--bare", remotePath); await Git("remote", "add", "origin", remotePath);
            var push = new Commands.Push(repo, new() { Name = "topic", IsLocal = true },
                new Models.Remote { Name = "origin", URL = remotePath }, new() { Name = "topic", IsLocal = false }, false, false, false, false, false)
                { RaiseError = false };
            check("upstream24: branch push succeeds despite a tag with the same name", await push.ExecAsync());
            check("upstream24: the remote receives the branch commit instead of the tag", await Git("--git-dir=" + remotePath, "rev-parse", "refs/heads/topic") == tip);
            check("upstream24: push leaves the local tag unchanged", await Git("rev-parse", "refs/tags/topic") == baseline);
            var patch = Path.Combine(root, "topic.patch");
            await File.WriteAllTextAsync(patch, await Git("format-patch", "--stdout", "-1", "refs/heads/topic") + "\n");
            await Git("checkout", "main");
            check("upstream24: an unobstructed mailbox patch passes preflight", await new Commands.CheckMergeSafety(repo, [], patchFile: patch).GetResultAsync());
            check("upstream24: a mailbox patch creates its commit through git am", await new Commands.Am(repo, patch, false, "nowarn", "--3way") { RaiseError = false }.ExecAsync());
            check("upstream24: git am preserves patch message and contents", await Git("log", "-1", "--format=%s") == "Topic update" && File.ReadAllText(Path.Combine(repo, "value.txt")) == "changed\n");
            await Git("reset", "--hard", baseline);
            await Git("checkout", "topic");
            await File.WriteAllTextAsync(Path.Combine(repo, "ignored.data"), "incoming\n");
            await Git("add", "-f", "ignored.data"); await Git("commit", "-m", "Ignored collision");
            await File.WriteAllTextAsync(patch, await Git("format-patch", "--stdout", "-1", "refs/heads/topic") + "\n");
            await Git("checkout", "main");
            await File.WriteAllTextAsync(Path.Combine(repo, "ignored.data"), "protected local bytes\n");
            var safety = new Commands.CheckMergeSafety(repo, [], patchFile: patch);
            check("upstream24: mailbox preflight rejects an ignored local-file collision", !await safety.GetResultAsync() && safety.Obstruction == "ignored.data");
            check("upstream24: rejected mailbox preflight preserves HEAD and bytes", await Git("rev-parse", "HEAD") == baseline && File.ReadAllText(Path.Combine(repo, "ignored.data")) == "protected local bytes\n");
            File.Delete(Path.Combine(repo, "ignored.data"));
            await Git("checkout", "--detach", baseline);
            var branches = await new Commands.QueryBranches(repo).GetResultAsync();
            check("upstream24: detached HEAD has its own branch marker", branches.Exists(b => b.IsDetachedHead && b.Name == "HEAD"));
        }
    }
}
