using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class DataSafetyProbe
    {
        private sealed class Git : Commands.Command
        {
            public Git(string root, string args) { WorkingDirectory = root; Args = args; UseCredentialManager = false; }
            public async Task RunAsync()
            {
                var result = await ReadToEndAsync();
                if (!result.IsSuccess) throw new IOException(result.StdErr);
            }
        }

        public static async Task<int> RunAsync(string root, string mode, string report)
        {
            async Task Run(string args) => await new Git(root, args).RunAsync();
            await Run("init -b main");
            var file = Path.Combine(root, "sample.txt");
            File.WriteAllText(file, "original\n");
            await Run("add .");
            if (mode != "unborn") await Run("-c user.name=QA -c user.email=qa@example.invalid commit -m Initial");
            var success = false;
            switch (mode)
            {
                case "status-failure":
                    File.WriteAllText(Path.Combine(root, ".git/config"), "[broken");
                    var broken = new Commands.QueryLocalChanges(root);
                    await broken.GetResultAsync();
                    success = !broken.Succeeded && !string.IsNullOrWhiteSpace(broken.Error);
                    var countQuery = new Commands.CountLocalChanges(root, false);
                    await countQuery.GetResultAsync();
                    success &= !countQuery.Succeeded;
                    break;
                case "status-cancelled":
                    var cancelled = new Commands.QueryLocalChanges(root) { CancellationToken = new CancellationToken(true) };
                    var changes = await cancelled.GetResultAsync();
                    success = !cancelled.Succeeded && changes.Count == 0;
                    break;
                case "bounded-output":
                    var output = new Commands.Command { WorkingDirectory = root, RaiseError = false, UseCredentialManager = false,
                        Args = "-c alias.treegit-output=\"!printf '%1048576s' x >&2; printf '\\nEND_MARKER\\n' >&2; exit 9\" treegit-output" };
                    success = !await output.ExecAsync() && output.LastExitCode == 9 && output.FailureMessage.Length <= 65536 && output.FailureMessage.EndsWith("END_MARKER", StringComparison.Ordinal);
                    var bounded = new Models.BoundedTextBuffer(32);
                    bounded.Append(new string('x', 1000000));
                    bounded.Append("\U0001F600 tail");
                    success &= bounded.Length <= 32 && bounded.WasTruncated && bounded.ToString().EndsWith("\U0001F600 tail", StringComparison.Ordinal);
                    // Trimming in the middle of a surrogate pair must not leave
                    // an invalid leading low surrogate in visible output.
                    var unicode = new Models.BoundedTextBuffer(16);
                    unicode.Append("a\U0001F600" + new string('b', 15));
                    success &= unicode.Length <= 16 && !char.IsLowSurrogate(unicode.ToString()[0]);
                    break;
                case "normal":
                    File.WriteAllText(file, "modified\n");
                    File.WriteAllText(Path.Combine(root, "untracked.txt"), "preserve\n");
                    success = await Commands.Discard.AllAsync(root, true, false, false, null) &&
                        File.ReadAllText(file) == "original\n" && File.ReadAllText(Path.Combine(root, "untracked.txt")) == "preserve\n";
                    break;
                case "unborn":
                    success = await Commands.Discard.AllAsync(root, true, false, false, null);
                    break;
                case "obstruction":
                    File.Delete(file); Directory.CreateDirectory(file);
                    File.WriteAllText(Path.Combine(file, "untracked.txt"), "preserve\n");
                    success = !await Commands.Discard.AllAsync(root, true, false, false, null) &&
                        File.ReadAllText(Path.Combine(file, "untracked.txt")) == "preserve\n";
                    break;
                case "selected-obstruction":
                    File.Delete(file); Directory.CreateDirectory(file);
                    File.WriteAllText(Path.Combine(file, "untracked.txt"), "preserve\n");
                    var deleted = new Models.Change { Path = "sample.txt", WorkTree = Models.ChangeState.Deleted };
                    success = !await Commands.Discard.ChangesAsync(root, [deleted], null) &&
                        File.ReadAllText(Path.Combine(file, "untracked.txt")) == "preserve\n";
                    break;
                case "selected-literal":
                    var literal = Path.Combine(root, "[a].txt"); File.WriteAllText(literal, "literal\n");
                    await Run("add -- \"[a].txt\"");
                    File.WriteAllText(literal, "changed literal\n");
                    File.WriteAllText(Path.Combine(root, "a.txt"), "must survive\n");
                    success = await Commands.Discard.ChangesAsync(root, [new Models.Change { Path = "[a].txt", WorkTree = Models.ChangeState.Modified }], null) &&
                        File.ReadAllText(literal) == "literal\n" && File.ReadAllText(Path.Combine(root, "a.txt")) == "must survive\n";
                    break;
                case "nested-repository":
                case "selected-nested-repository":
                    var nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
                    await new Git(nested, "init -b inner").RunAsync();
                    File.WriteAllText(Path.Combine(nested, "keep.txt"), "preserve nested\n");
                    success = mode == "nested-repository"
                        ? await Commands.Discard.AllAsync(root, false, true, true, null)
                        : await Commands.Discard.ChangesAsync(root, [new Models.Change { Path = "nested/", WorkTree = Models.ChangeState.Untracked }], null);
                    success &= File.ReadAllText(Path.Combine(nested, "keep.txt")) == "preserve nested\n" && Directory.Exists(Path.Combine(nested, ".git"));
                    break;
                case "junction":
                    var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
                    File.WriteAllText(Path.Combine(external, "keep.txt"), "preserve external\n");
                    var link = Path.Combine(root, "junction");
                    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
                    start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{external.Replace("'", "''")}' | Out-Null");
                    using (var process = Process.Start(start)) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("Could not create owned junction fixture."); }
                    try
                    {
                        // Only select the junction, never its owned target directory.
                        success = await Commands.Discard.ChangesAsync(root, [new Models.Change { Path = "junction/", WorkTree = Models.ChangeState.Untracked }], null) &&
                            File.ReadAllText(Path.Combine(external, "keep.txt")) == "preserve external\n";
                    }
                    finally { if (Directory.Exists(link)) Directory.Delete(link, false); }
                    break;
                case "failed-discard":
                    File.WriteAllText(Path.Combine(root, "untracked.txt"), "preserve\n");
                    File.WriteAllText(Path.Combine(root, ".git/config"), "[broken");
                    success = !await Commands.Discard.AllAsync(root, true, true, true, null) && File.Exists(file) && File.Exists(Path.Combine(root, "untracked.txt"));
                    break;
                case "merge-ignored":
                case "merge-benign-directory":
                case "merge-file-ancestor":
                case "merge-nested-repository":
                    File.WriteAllText(Path.Combine(root, ".git/info/exclude"), "ignored.data\nnewdir/\n");
                    await Run("checkout -b incoming");
                    Directory.CreateDirectory(Path.Combine(root, "newdir"));
                    File.WriteAllText(Path.Combine(root, "newdir/incoming.txt"), "incoming child\n");
                    File.WriteAllText(Path.Combine(root, "ignored.data"), "incoming ignored\n");
                    await Run("add -f -- ignored.data newdir/incoming.txt");
                    await Run("-c user.name=QA -c user.email=qa@example.invalid commit -m Incoming");
                    await Run("checkout main");
                    if (mode == "merge-ignored") File.WriteAllText(Path.Combine(root, "ignored.data"), "preserve ignored\n");
                    else if (mode == "merge-file-ancestor") File.WriteAllText(Path.Combine(root, "newdir"), "preserve ancestor\n");
                    else
                    {
                        Directory.CreateDirectory(Path.Combine(root, "newdir"));
                        File.WriteAllText(Path.Combine(root, "newdir/keep.txt"), "preserve directory\n");
                        if (mode == "merge-nested-repository") await new Git(Path.Combine(root, "newdir"), "init -b inner").RunAsync();
                    }
                    var check = new Commands.CheckMergeSafety(root, ["incoming"]);
                    var safe = await check.GetResultAsync();
                    success = safe == (mode == "merge-benign-directory");
                    if (safe)
                        success &= await new Commands.Merge(root, "incoming", "", false).ExecAsync() &&
                            File.ReadAllText(Path.Combine(root, "newdir/keep.txt")) == "preserve directory\n" &&
                            File.ReadAllText(Path.Combine(root, "newdir/incoming.txt")) == "incoming child\n";
                    else success &= !string.IsNullOrEmpty(check.Obstruction);
                    if (mode == "merge-ignored") success &= File.ReadAllText(Path.Combine(root, "ignored.data")) == "preserve ignored\n";
                    if (mode == "merge-file-ancestor") success &= File.ReadAllText(Path.Combine(root, "newdir")) == "preserve ancestor\n";
                    if (mode == "merge-nested-repository") success &= Directory.Exists(Path.Combine(root, "newdir/.git")) && !File.Exists(Path.Combine(root, "newdir/incoming.txt"));
                    break;
                default: throw new ArgumentException(mode);
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success }));
            return success ? 0 : 1;
        }
    }
}
