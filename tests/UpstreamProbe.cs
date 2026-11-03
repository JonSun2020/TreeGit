using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class UpstreamProbe
    {
        public static async Task<int> FetchAsync(string repo, string url)
        {
            Native.OS.GitExecutable = Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
            var remote = new Models.Remote { Name = "origin", URL = url };
            return await new Commands.Fetch(repo, remote) { RaiseError = false }.ExecAsync() ? 0 : 1;
        }

        private sealed class Log : Models.ICommandLog
        {
            private readonly StringBuilder _text = new();
            public void AppendLine(string line = null) { lock (_text) _text.AppendLine(line); }
            public override string ToString() { lock (_text) return _text.ToString(); }
        }

        public static async Task<int> RunAsync(string root, string report)
        {
            var checks = new List<string>();
            var log = new Log();
            T Prepare<T>(T command) where T : Commands.Command { command.RaiseError = false; command.Log = log; return command; }
            void Check(string name, bool success)
            {
                if (!success) throw new InvalidOperationException(name);
                checks.Add(name);
            }
            async Task<bool> Git(string repo, string args) => await Prepare(new Commands.Command { WorkingDirectory = repo, Context = repo, Args = args, Editor = Commands.Command.EditorType.None }).ExecAsync();
            try
            {
                // Exercise branch pinning with crossing/merging histories, multiple
                // roots and a preferred head below newer unrelated commits.
                var random = new Random(7319);
                var history = new List<Models.Commit>();
                for (var i = 0; i < 100; i++)
                {
                    var c = new Models.Commit { SHA = i.ToString("x40"), Subject = "Graph regression " + i };
                    if (i < 99)
                        c.Parents.Add(random.Next(i + 1, 100).ToString("x40"));
                    if (i < 96 && random.Next(3) == 0)
                    {
                        var other = random.Next(i + 1, 100).ToString("x40");
                        if (!c.Parents.Contains(other)) c.Parents.Add(other);
                    }
                    history.Add(c);
                }
                Models.CommitGraph.SetDefaultPens();
                for (var h = 0; h < 80; h++)
                {
                    var graph = Models.CommitGraph.Generate(history, false, Models.CommitGraphHighlighting.All, [], history[h].SHA);
                    var next = h;
                    while (true)
                    {
                        if (graph.Dots[next].Center.X != 12)
                            throw new InvalidOperationException("Selected branch escaped the leftmost graph lane.");
                        if (history[next].Parents.Count == 0) break;
                        next = Convert.ToInt32(history[next].Parents[0], 16);
                    }
                    if (history.Select(c => c.LeftMargin).Distinct().Count() != 1 || graph.Dots.Any(d => !double.IsFinite(d.Center.X)))
                        throw new InvalidOperationException("Graph columns are not aligned/finite.");
                }
                Check("80 complex DAG branch selections stay in the leftmost lane", true);
                Check("graph descriptions stay aligned across merges and roots", true);
                Models.CommitGraph.SetPens([Avalonia.Media.Colors.Blue], 2);
                Check("single-color custom themes render without graph failure", Models.CommitGraph.Generate(history, false, Models.CommitGraphHighlighting.All, [], history[30].SHA).Dots.Count == 100);
                Models.CommitGraph.SetDefaultPens();
                root = Path.GetFullPath(root);
                Directory.CreateDirectory(root);
                Native.OS.GitExecutable = Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
                var repo = Path.Combine(root, "original 中文");
                Directory.CreateDirectory(repo);
                Check("upstream Command initializes repository", await Git(repo, "init -b main"));
                var config = Prepare(new Commands.Config(repo));
                Check("repository author configuration", await config.SetAsync("user.name", "Upstream integration") && await config.SetAsync("user.email", "probe@example.invalid"));
                await File.WriteAllTextAsync(Path.Combine(repo, "中文 file.txt"), "initial\n");
                Check("upstream staging", await Git(repo, "add -- ."));
                Check("upstream Commit", await Prepare(new Commands.Commit(repo, "Initial 中文", false, false, false, false)).RunAsync());
                Check("upstream Branch create", await Prepare(new Commands.Branch(repo, "feature/probe")).CreateAsync("HEAD", false));
                Check("upstream Branch rename", await Prepare(new Commands.Branch(repo, "feature/probe")).RenameAsync("feature/renamed"));
                Check("upstream QueryBranches", (await new Commands.QueryBranches(repo).GetResultAsync()).Any(b => b.Name == "feature/renamed"));
                Check("upstream annotated Tag", await Prepare(new Commands.Tag(repo, "probe-v1")).AddAsync("HEAD", "Annotated 中文", false));
                var bare = Path.Combine(root, "origin.git");
                var clone = Path.Combine(root, "clone spaces 中文");
                Check("upstream Clone bare", await Prepare(new Commands.Clone("probe", root, repo, "origin.git", "", "--bare")).ExecAsync());
                var cloneCommand = Prepare(new Commands.Clone("probe", root, bare, "clone spaces 中文", "", ""));
                Check("clone credentials use destination scope", cloneCommand.CredentialScope == clone);
                Check("upstream Clone working copy", await cloneCommand.ExecAsync() && File.Exists(Path.Combine(clone, "中文 file.txt")));
                var bundle = Path.Combine(root, "backup.bundle");
                Check("create repository bundle", await Git(repo, "bundle create " + bundle.Quoted() + " --all"));
                var bundleClone = Prepare(new Commands.Clone("probe", root, bundle, "", "", ""));
                Check("bundle clone destination credential scope", bundleClone.CredentialScope == Path.Combine(root, "backup"));
                Check("upstream Clone from bundle", await bundleClone.ExecAsync() && File.Exists(Path.Combine(root, "backup", "中文 file.txt")));
                var remotes = await new Commands.QueryRemotes(clone).GetResultAsync();
                Check("upstream QueryRemotes", remotes.Count == 1 && remotes[0].Name == "origin");
                Check("upstream Fetch", await Prepare(new Commands.Fetch(clone, remotes[0], false, false)).ExecAsync());
                Check("upstream Submodule add and update", await Prepare(new Commands.Submodule(repo)).AddAsync(clone, "modules/中文 module", true));
                Check("submodule working file available", File.Exists(Path.Combine(repo, "modules", "中文 module", "中文 file.txt")));
                Check("commit submodule", await Git(repo, "add -- .") && await Prepare(new Commands.Commit(repo, "Add submodule", false, false, false, false)).RunAsync());
                var worktree = Prepare(new Commands.Worktree(repo));
                var worktreePath = Path.Combine(root, "worktree 中文");
                Check("upstream Worktree add", await worktree.AddAsync(worktreePath, "feature/worktree"));
                Check("upstream Worktree enumeration", (await worktree.ReadAllAsync()).Count == 2);
                Check("upstream Worktree lock/unlock", await worktree.LockAsync(worktreePath) && await worktree.UnlockAsync(worktreePath));
                Check("upstream Worktree remove", await worktree.RemoveAsync(worktreePath, false));
                var lfs = Prepare(new Commands.LFS(repo));
                Check("upstream LFS local initialization", await lfs.InstallAsync());
                Check("upstream LFS tracking", await lfs.TrackAsync("*.bin", false));
                await File.WriteAllBytesAsync(Path.Combine(repo, "asset.bin"), [1, 2, 3, 4, 5, 6]);
                Check("LFS clean filter and original Commit", await Git(repo, "add -- .") && await Prepare(new Commands.Commit(repo, "LFS asset", false, false, false, false)).RunAsync());
                var start = new System.Diagnostics.ProcessStartInfo(Native.OS.GitExecutable) { WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("show"); start.ArgumentList.Add("HEAD:asset.bin");
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var pointer = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    Check("LFS stores pointer while preserving working bytes", pointer.Contains("https://git-lfs.github.com/spec/v1", StringComparison.Ordinal) && File.ReadAllBytes(Path.Combine(repo, "asset.bin")).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }));
                }
                for (var i = 0; i < 50 && Native.OS.GitFlowVersion == Models.GitFlowVersion.None; i++)
                    await Task.Delay(100);
                Check("bundled Git Flow Next discovery", Native.OS.GitFlowVersion == Models.GitFlowVersion.Next);
                var flow = Prepare(new Commands.GitFlow(repo));
                Check("upstream Git Flow init", await flow.InitAsync("main", "develop", "feature/", "release/", "hotfix/", ""));
                Check("upstream Git Flow feature start", await flow.StartAsync(Models.GitFlowBranchType.Feature, "integration", null));
                await File.WriteAllTextAsync(Path.Combine(repo, "flow.txt"), "feature\n");
                Check("commit on Git Flow feature", await Git(repo, "add -- flow.txt") && await Prepare(new Commands.Commit(repo, "Git Flow feature", false, false, false, false)).RunAsync());
                Check("upstream Git Flow feature finish", await flow.FinishAsync(Models.GitFlowBranchType.Feature, "integration", false, false, false));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed = checks.Count, checks, root }, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(report + ".log", log.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(report + ".failure", ex + "\n" + log);
                return 1;
            }
        }
    }
}
