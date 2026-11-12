using System;
using System.Collections.Generic;
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
            var indexBeforeStatus = File.ReadAllBytes(Path.Combine(repo, ".git", "index"));
            File.SetLastWriteTimeUtc(Path.Combine(repo, "value.txt"), DateTime.UtcNow.AddMinutes(1));
            var readOnlyStatus = await new Commands.QueryRepositoryStatus(repo).GetResultAsync();
            check("repository overview: reading clean status never rewrites index stat data",
                readOnlyStatus?.LocalChanges == 0 && File.ReadAllBytes(Path.Combine(repo, ".git", "index")).AsSpan().SequenceEqual(indexBeforeStatus));
            await Git("checkout", "-b", "topic");
            await File.WriteAllTextAsync(Path.Combine(repo, "value.txt"), "changed\n");
            await Git("commit", "-am", "Topic update");
            var tip = await Git("rev-parse", "HEAD");
            await Git("branch", "copied-branch", "topic");
            var origins = await new Commands.QueryLocalCommitOrigins(repo).GetResultAsync(new HashSet<string>(["refs/heads/main", "refs/heads/topic", "refs/heads/copied-branch"]));
            check("branch header: local creation evidence excludes branches later copied from the same tip", origins.TryGetValue(tip, out var origin) && origin == "refs/heads/topic");
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

            var selected = new Models.Commit { SHA = "feature-mid", Color = 1, Parents = ["base"] };
            var graph = new List<Models.Commit>
            {
                new() { SHA = "merge", Color = 0, Parents = ["main-mid", "feature-tip"] },
                new() { SHA = "feature-tip", Color = 1, Parents = ["feature-mid"] }, selected,
                new() { SHA = "main-mid", Color = 0, Parents = ["base"] }, new() { SHA = "base", Color = 0 },
            };
            var mainBranch = new Models.Branch { FullName = "refs/heads/main", Name = "main", Head = "merge", IsLocal = true, IsCurrent = true };
            var feature = new Models.Branch { FullName = "refs/heads/feature", Name = "feature", Head = "feature-tip", IsLocal = true };
            var remoteFeature = new Models.Branch { FullName = "refs/remotes/origin/feature", Name = "feature", Remote = "origin", Head = "feature-tip" };
            var coverage = new Models.SearchBranchCoverage([mainBranch, feature, remoteFeature]);
            var info = new Models.CommitBranchDetails(selected, coverage, graph, null);
            check("branch header: a merged side-line commit prefers its own visible graph line", info.Primary == feature && info.IsInferred);
            check("branch header: the extra badge excludes only the displayed primary ref", info.ExtraCount == 2 && info.HasMore && info.Commit.SearchBranches.Count == 3);
            check("branch header: the main line still prefers the main branch", new Models.CommitBranchDetails(graph[3], coverage, graph, null).Primary == mainBranch);
            check("branch header: verified creation evidence takes priority over graph inference", new Models.CommitBranchDetails(selected, coverage, graph, mainBranch) is { Primary: var primary, IsInferred: false } && primary == mainBranch);
            check("branch header: a single containing branch disables the more button", !new Models.CommitBranchDetails(selected, new([feature]), graph, null).HasMore);
            check("branch header: an unreachable commit does not invent a branch", new Models.CommitBranchDetails(selected, Models.SearchBranchCoverage.Empty, graph, null) is { Primary: null, ExtraCount: 0, HasMore: false });
            check("branch header: a recorded origin outside current containment is not subtracted from the badge", new Models.CommitBranchDetails(selected, new([feature]), graph, mainBranch).ExtraCount == 1);
            check("branch header: the display frame uses the selected graph color and branch decorator", info.DisplayCommit.Color == selected.Color && info.DisplayCommit.Decorators.Count == 1 && info.DisplayCommit.Decorators[0].Name == "feature");
            feature.Upstream = remoteFeature.FullName;
            remoteFeature.Head = selected.SHA;
            check("branch header: a containing local tracking branch takes priority over its exact remote tip", new Models.CommitBranchDetails(selected, coverage, graph, null).Primary == feature);
            check("branch header: a local branch that does not contain the commit is not highlighted", new Models.CommitBranchDetails(selected, new([remoteFeature]), graph, null).Primary == remoteFeature);
            var searchCommit = new Models.Commit { SHA = selected.SHA };
            var searchInfo = new Models.CommitBranchDetails(searchCommit, coverage, graph, null);
            check("branch header: search selection uses the same graph branch and color", searchInfo.Primary == feature && searchInfo.DisplayCommit.Color == selected.Color);

            var ancestor = new Commands.IsAncestor(repo, baseline, tip) { RaiseError = false };
            check("ancestry query: an ancestor is a successful positive answer", await ancestor.GetResultAsync() && ancestor.Succeeded);
            ancestor = new Commands.IsAncestor(repo, tip, baseline) { RaiseError = false };
            check("ancestry query: exit 1 is a successful negative answer", !await ancestor.GetResultAsync() && ancestor.Succeeded);
            ancestor = new Commands.IsAncestor(repo, "refs/heads/missing-query-fixture", baseline) { RaiseError = false };
            check("ancestry query: an invalid revision is a failure, not a negative answer", !await ancestor.GetResultAsync() && !ancestor.Succeeded);
            var hashes = new Commands.QueryCurrentBranchCommitHashes(repo, 0) { RaiseError = false };
            check("ancestry query: successful current history reports its status", (await hashes.GetResultAsync()).Contains(baseline) && hashes.Succeeded);
            Native.ProcessLauncher.BeforeStart = (process, _) =>
            {
                if (process.StartInfo.WorkingDirectory == repo && process.StartInfo.Arguments.EndsWith("--format=%H", StringComparison.Ordinal))
                    process.StartInfo.Arguments = "-c alias.treegit-query-failure=\"!printf aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; printf '\\n'; exit 9\" treegit-query-failure";
            };
            try
            {
                hashes = new Commands.QueryCurrentBranchCommitHashes(repo, 0) { RaiseError = false };
                check("ancestry query: partial output from a failed process is discarded", (await hashes.GetResultAsync()).Count == 0 && !hashes.Succeeded);
                var history = new Commands.QueryCommits(repo, "-1 refs/heads/topic") { RaiseError = false };
                check("ancestry query: failed auxiliary history query cannot publish partial commit data", (await history.GetResultAsync()).Count == 0 && !history.Succeeded);
            }
            finally { Native.ProcessLauncher.BeforeStart = null; }
        }
    }
}
