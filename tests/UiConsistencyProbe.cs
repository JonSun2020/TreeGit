using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class UiConsistencyProbe
    {
        public static async Task<int> RunAsync(string root, string report)
        {
            var checks = new List<string>();
            var ownsRoot = false;
            var environment = new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_COUNT" }
                .ToDictionary(key => key, Environment.GetEnvironmentVariable);
            void Check(string name, bool result)
            {
                if (!result) throw new InvalidOperationException(name);
                checks.Add(name);
            }
            async Task Reject(Func<Task> operation, string name)
            {
                var rejected = false;
                try { await operation(); } catch (Exception) { rejected = true; }
                Check(name, rejected);
            }
            try
            {
                if (Environment.GetEnvironmentVariable("CI") != "true" || !Path.IsPathFullyQualified(root) || Directory.Exists(root))
                    throw new InvalidOperationException("Use a new, isolated directory in CI for this probe.");
                Directory.CreateDirectory(root);
                ownsRoot = true;
                Native.OS.GitExecutable = Native.OS.FindGitExecutable();
                var source = new List<Models.Change>
                {
                    new() { Path = "src/file10.txt", Index = Models.ChangeState.Modified, WorkTree = Models.ChangeState.None },
                    new() { Path = "src/file2.txt", Index = Models.ChangeState.None, WorkTree = Models.ChangeState.Modified },
                    new() { Path = "new/item.txt", WorkTree = Models.ChangeState.Untracked },
                    new() { Path = "src/conflict.txt", WorkTree = Models.ChangeState.Conflicted },
                    new() { Path = "src/type.txt", Index = Models.ChangeState.Added, WorkTree = Models.ChangeState.TypeChanged },
                    new() { Path = "src/removed.txt", WorkTree = Models.ChangeState.Deleted },
                };
                var original = source.ToArray();
                Check("path sorting retains natural numeric order", Models.ChangeListOptions.Apply(source, "file", 0, 0, false).Select(c => c.Path).SequenceEqual(new[] { "src/file2.txt", "src/file10.txt" }));
                Check("modified worktree filter ignores index-only modifications", Models.ChangeListOptions.Apply(source, "", 2, 0, false).Select(c => c.Path).SequenceEqual(new[] { "src/file2.txt", "src/type.txt" }));
                Check("staged status filter uses the index state", Models.ChangeListOptions.Apply(source, "", 2, 0, true).Single() == source[0]);
                Check("untracked filter never exposes staged files", Models.ChangeListOptions.Apply(source, "", 3, 0, true).Count == 0);
                Check("untracked filter finds only new files", Models.ChangeListOptions.Apply(source, "", 3, 0, false).Single() == source[2]);
                Check("conflict filter and case-insensitive path filter intersect", Models.ChangeListOptions.Apply(source, "SRC", 1, 0, false).Single() == source[3]);
                Check("empty filter result remains empty", Models.ChangeListOptions.Apply(source, "missing", 0, 0, false).Count == 0);
                Check("name sorting sorts by basename across directories", Models.ChangeListOptions.Apply(source, "", 0, 1, false).First() == source[3]);
                Check("filter and sort never mutate the original list", source.SequenceEqual(original));
                var sorted = Models.ChangeListOptions.Apply(source.Where(c => c.Path.StartsWith("src/")).ToList(), "", 0, 2, false);
                var tree = ViewModels.ChangeTreeNode.Build(sorted, new(), false, true);
                Check("tree keeps requested status ordering inside folders", tree.Single().Children.Select(n => n.Change).SequenceEqual(sorted));
                Check("tree depths remain correct", tree.Single().Depth == 0 && tree.Single().Children.All(n => n.Depth == 1));

                var global = Path.Combine(root, "global.gitconfig");
                var system = Path.Combine(root, "system.gitconfig");
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
                Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", system);
                Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", null);
                Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
                File.WriteAllText(system, "[user]\nname = System QA\n");
                File.WriteAllText(global, "# preserve this comment\n[user]\nname = Original QA\nemail = qa@example.invalid\n[core]\nautocrlf = input\n");
                var before = File.ReadAllBytes(global);
                var baseline = Commands.GlobalConfigEdit.ReadEffective();
                Check("global values override system values", baseline["user.name"] == "Original QA");
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Preview QA" }, baseline))
                    Check("preparation leaves the real file unchanged", File.ReadAllBytes(global).SequenceEqual(before));
                Check("disposing a draft leaves the real file unchanged", File.ReadAllBytes(global).SequenceEqual(before));
                Check("cancel removes its own lock", !File.Exists(global + ".lock"));
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "保存 \"Quoted\" \\ Path\nNext" }, baseline)) edit.Commit();
                Check("save preserves Unicode quotes backslashes and newlines", Commands.GlobalConfigEdit.ReadEffective()["user.name"] == "保存 \"Quoted\" \\ Path\nNext");
                Check("save preserves comments and unrelated settings", File.ReadAllText(global).Contains("# preserve this comment") && Commands.GlobalConfigEdit.ReadEffective()["core.autocrlf"] == "input");

                baseline = Commands.GlobalConfigEdit.ReadEffective();
                File.WriteAllText(global + ".lock", "another Git process");
                await Reject(async () => { using var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Blocked" }, baseline); }, "existing Git lock rejects the save");
                Check("failed save preserves another process lock", File.ReadAllText(global + ".lock") == "another Git process");
                File.Delete(global + ".lock");
                before = File.ReadAllBytes(global);
                File.AppendAllText(global, "\n[user]\nname = External QA\n");
                await Reject(async () => { using var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Stale" }, baseline); }, "external changes to edited keys reject stale drafts");
                Check("stale draft never overwrites external changes", Commands.GlobalConfigEdit.ReadEffective()["user.name"] == "External QA");
                baseline = Commands.GlobalConfigEdit.ReadEffective();
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Prepared QA" }, baseline))
                {
                    File.AppendAllText(global, "\n[fetch]\nprune = true\n");
                    var changed = File.ReadAllBytes(global);
                    await Reject(() => { edit.Commit(); return Task.CompletedTask; }, "non-Git file edits after preparation reject replacement");
                    Check("commit failure preserves the external file", File.ReadAllBytes(global).SequenceEqual(changed));
                }
                Check("commit failure cleans its own lock", !File.Exists(global + ".lock"));
                baseline = Commands.GlobalConfigEdit.ReadEffective();
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.email"] = null }, baseline)) edit.Commit();
                Check("cleared fields remove only their own setting", !Commands.GlobalConfigEdit.ReadEffective().ContainsKey("user.email"));
                File.Delete(global);
                baseline = Commands.GlobalConfigEdit.ReadEffective();
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.email"] = "new@example.invalid" }, baseline)) edit.Commit();
                Check("save supports a previously missing global file", Commands.GlobalConfigEdit.ReadEffective()["user.email"] == "new@example.invalid");
                using (var edit = await Commands.GlobalConfigEdit.PrepareAsync(new(), new())) edit.Commit();
                Check("unchanged configuration needs no lock", !File.Exists(global + ".lock"));
                File.WriteAllText(global, "[fetch]\nprune\n[http]\nsslverify =\n");
                var booleans = Commands.GlobalConfigEdit.ReadEffective();
                Check("valueless true differs from an explicitly empty false value", booleans["fetch.prune"] == "true" && booleans["http.sslverify"].Length == 0);
                File.WriteAllText(Path.Combine(root, "included.gitconfig"), "[user]\nname = Included QA\n");
                File.WriteAllText(global, "[user]\nname = Main QA\n[include]\npath = included.gitconfig\n");
                baseline = Commands.GlobalConfigEdit.ReadEffective();
                before = File.ReadAllBytes(global);
                await Reject(async () => { using var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Invisible QA" }, baseline); }, "a later include cannot silently override a supposedly saved setting");
                Check("included configuration is preserved on rejected save", File.ReadAllBytes(global).SequenceEqual(before) && Commands.GlobalConfigEdit.ReadEffective()["user.name"] == "Included QA");
                File.WriteAllText(global, "[user]\nname = Atomic QA\n");
                baseline = Commands.GlobalConfigEdit.ReadEffective();
                before = File.ReadAllBytes(global);
                await Reject(async () => { using var edit = await Commands.GlobalConfigEdit.PrepareAsync(new() { ["user.name"] = "Partial QA", ["invalid"] = "bad key" }, baseline); }, "a failed second setting prevents partial configuration writes");
                Check("multi-setting failure preserves original bytes", File.ReadAllBytes(global).SequenceEqual(before));
                Check("all staged configuration copies are cleaned", !Directory.EnumerateFiles(root, ".treegit-settings-*").Any());
                File.WriteAllText(global, "[broken\n");
                var failedRead = false;
                try { Commands.GlobalConfigEdit.ReadEffective(); } catch { failedRead = true; }
                Check("malformed configuration is a read failure", failedRead);
                File.WriteAllText(global, "[user]\nname = Upstream QA\nemail = upstream@example.invalid\n");
                await Upstream24Checks.RunAsync(root, Check);
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = checks.Count, checks }));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = checks.Count, checks, error = error.ToString() }));
                return 1;
            }
            finally
            {
                foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
                // The directory was newly created by this probe, with no links.
                if (ownsRoot && Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
