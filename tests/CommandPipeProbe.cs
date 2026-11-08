using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class CommandPipeProbe
    {
        private sealed class Query : Commands.Command
        {
            public Result Read() => ReadToEnd();
            public Task<Result> ReadAsync() => ReadToEndAsync();
        }

        public static async Task<int> RunAsync(string root, string mode, string report)
        {
            try
            {
                Directory.CreateDirectory(root);
                Native.OS.GitExecutable = Native.OS.FindGitExecutable();
                if (mode.StartsWith("pull-safety-", StringComparison.Ordinal))
                    return await PullSafetyProbe.RunAsync(root, mode[12..], report);
                if (mode.StartsWith("safety-", StringComparison.Ordinal))
                    return await DataSafetyProbe.RunAsync(root, mode[7..], report);
                if (mode.StartsWith("stream-", StringComparison.Ordinal))
                    return await RunStreamAsync(root, mode[7..], report);
                if (mode == "credential-config")
                {
                    var config = Path.Combine(root, "malformed.gitconfig");
                    File.WriteAllText(config, "[sourcegit]\ncredentialmanager = " + new string('x', 16384) + "\n");
                    Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
                    Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", "1");
                    Environment.SetEnvironmentVariable("GIT_TRACE2_CONFIG_PARAMS", "sourcegit.credentialmanager");
                    var enabled = Native.RepositoryCredentials.UsesCredentialManager(root);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = !enabled }));
                    return enabled ? 1 : 0;
                }
                var failedProcess = mode.EndsWith("-failure", StringComparison.Ordinal);
                var query = new Query
                {
                    WorkingDirectory = root,
                    UseCredentialManager = false,
                    Editor = Commands.Command.EditorType.None,
                    Args = "-c alias.treegit-pipe=\"!printf begin; printf '%1048576s' x >&2; printf end; exit " + (failedProcess ? "9" : "0") + "\" treegit-pipe",
                };
                var result = mode.StartsWith("sync", StringComparison.Ordinal) ? query.Read() : await query.ReadAsync();
                var success = result.IsSuccess == !failedProcess && result.StdOut == "beginend" && result.StdErr.Length == 1048576;
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success, result.IsSuccess, stdout = result.StdOut, stderrLength = result.StdErr.Length }));
                return success ? 0 : 1;
            }
            catch (Exception error)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = false, error = error.ToString() }));
                return 1;
            }
        }

        private static async Task<int> RunStreamAsync(string root, string mode, string report)
        {
            async Task Git(string arguments)
            {
                var result = await new Query { WorkingDirectory = root, Args = arguments, UseCredentialManager = false, Editor = Commands.Command.EditorType.None }.ReadAsync();
                if (!result.IsSuccess) throw new IOException(result.StdErr);
            }
            await Git(mode == "index-sha256" ? "init -b main --object-format=sha256" : "init -b main");
            File.WriteAllText(Path.Combine(root, "sample.txt"), "before\n");
            await Git("add ."); await Git("-c user.name=StreamQA -c user.email=stream@example.invalid commit -m Initial");
            File.WriteAllText(Path.Combine(root, "second.txt"), "second\n");
            await Git("add ."); await Git("-c user.name=StreamQA -c user.email=stream@example.invalid commit -m Second");
            File.WriteAllText(Path.Combine(root, "sample.txt"), "after\n");
            var config = Path.Combine(root, "trace.gitconfig");
            File.WriteAllText(config, "[treegit]\nflood = " + new string('x', 32768) + "\n");
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", "1");
            Environment.SetEnvironmentVariable("GIT_TRACE2_CONFIG_PARAMS", "treegit.flood");
            var change = new Models.Change { Path = "sample.txt" };
            if (mode.StartsWith("keygen-", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                var keys = Path.Combine(root, "isolated keys");
                var bundledGit = Environment.GetEnvironmentVariable("TREEGIT_TEST_BUNDLED_GIT") ?? Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
                if (!File.Exists(bundledGit)) throw new IOException("The disposable bundled Git fixture is missing.");
                Native.OS.GitExecutable = mode == "keygen-missing" ? Path.Combine(root, "missing", "git.exe") : bundledGit;
                var oldPath = Environment.GetEnvironmentVariable("PATH");
                Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
                var generator = new ViewModels.SSHKeyGenerator(keys)
                {
                    Name = mode == "keygen-invalid" ? "../outside" : "qa_key", Email = "qa@example.invalid",
                    Type = Models.SSHKeyType.Supported.First(t => t.Cmdline.Contains("ed25519", StringComparison.Ordinal)),
                    UsePassphrase = mode == "keygen-protected", Passphrase = "QA-test@123", ConfirmedPassphrase = "QA-test@123",
                };
                var privateFile = Path.Combine(keys, "qa_key"); var publicFile = privateFile + ".pub";
                var keySuccess = false;
                try
                {
                    if (mode is "keygen-pub-only" or "keygen-private-only")
                    {
                        Directory.CreateDirectory(keys);
                        File.WriteAllText(mode == "keygen-pub-only" ? publicFile : privateFile, "keep existing key bytes");
                    }
                    var pair = generator.Run();
                    if (mode == "keygen-invalid") keySuccess = pair == null && generator.HasErrors && !Directory.Exists(keys);
                    else if (mode == "keygen-missing") keySuccess = pair == null && !string.IsNullOrEmpty(generator.ErrorMessage) && !File.Exists(privateFile) && !File.Exists(publicFile);
                    else if (mode is "keygen-pub-only" or "keygen-private-only")
                        keySuccess = pair == null && !string.IsNullOrEmpty(generator.ErrorMessage) &&
                            File.ReadAllText(mode == "keygen-pub-only" ? publicFile : privateFile) == "keep existing key bytes" &&
                            !File.Exists(mode == "keygen-pub-only" ? privateFile : publicFile);
                    else
                    {
                        keySuccess = pair != null && File.ReadAllText(privateFile).Contains("OPENSSH PRIVATE KEY", StringComparison.Ordinal);
                        if (keySuccess)
                        {
                            var beforePrivate = File.ReadAllBytes(privateFile); var beforePublic = File.ReadAllBytes(publicFile);
                            var tool = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bundledGit)), "usr", "bin", "ssh-keygen.exe");
                            var info = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                            foreach (var argument in new[] { "-y", "-P", mode == "keygen-protected" ? "QA-test@123" : "", "-f", privateFile }) info.ArgumentList.Add(argument);
                            using var check = Native.ProcessLauncher.Start(info); check.StandardInput.Close();
                            var stdout = check.StandardOutput.ReadToEndAsync(); var stderr = check.StandardError.ReadToEndAsync();
                            await Task.WhenAll(stdout, stderr, check.WaitForExitAsync());
                            keySuccess &= check.ExitCode == 0 && File.ReadAllText(publicFile).StartsWith(string.Join(' ', stdout.Result.Trim().Split(' ').Take(2)), StringComparison.Ordinal);
                            if (mode == "keygen-existing") keySuccess &= generator.Run() == null && !string.IsNullOrEmpty(generator.ErrorMessage) &&
                                File.ReadAllBytes(privateFile).SequenceEqual(beforePrivate) && File.ReadAllBytes(publicFile).SequenceEqual(beforePublic);
                        }
                    }
                }
                finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = keySuccess, isolatedDirectory = keys, generator.HasErrors, generator.ErrorMessage }));
                return keySuccess ? 0 : 1;
            }
            if (mode.StartsWith("launch-", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                var realGit = Native.OS.GitExecutable;
                var denied = Path.Combine(root, "denied.exe"); Directory.CreateDirectory(denied);
                var head = (await new Query { WorkingDirectory = root, Args = "rev-parse HEAD", UseCredentialManager = false }.ReadAsync()).StdOut.Trim();
                var args = "rev-parse HEAD";
                if (mode == "launch-exec")
                {
                    await Git("add sample.txt");
                    File.WriteAllText(Path.Combine(root, ".git/hooks/pre-commit"), "#!/bin/sh\necho called >> .git/launch-hook-count\nexit 0\n");
                    args = "-c user.name=LaunchQA -c user.email=launch@example.invalid commit -m LaunchOnce";
                }
                if (mode == "launch-exit") args = "-c alias.treegit-exit=\"!exit 9\" treegit-exit";
                var attempts = 0;
                Native.ProcessLauncher.BeforeStart = (process, _) =>
                {
                    if (process.StartInfo.WorkingDirectory != root || !process.StartInfo.Arguments.EndsWith(args, StringComparison.Ordinal)) return;
                    attempts++;
                    process.StartInfo.FileName = mode == "launch-missing" ? Path.Combine(root, "absent.exe") :
                        mode == "launch-permanent" || mode != "launch-exit" && attempts == 1 ? denied : realGit;
                };
                Commands.Command.Result result = null;
                var executed = false;
                try
                {
                    var command = new Query { WorkingDirectory = root, Args = args, UseCredentialManager = false, RaiseError = false, Editor = Commands.Command.EditorType.None };
                    if (mode == "launch-exec") executed = await command.ExecAsync();
                    else result = mode == "launch-sync" ? command.Read() : await command.ReadAsync();
                }
                finally { Native.ProcessLauncher.BeforeStart = null; }
                var after = (await new Query { WorkingDirectory = root, Args = "rev-parse HEAD", UseCredentialManager = false }.ReadAsync()).StdOut.Trim();
                var launchSuccess = mode switch
                {
                    "launch-exec" => executed && attempts == 2 && head != after && File.ReadAllLines(Path.Combine(root, ".git/launch-hook-count")).Length == 1,
                    "launch-permanent" => !result.IsSuccess && attempts == 3 && head == after,
                    "launch-missing" => !result.IsSuccess && attempts == 1 && head == after,
                    "launch-exit" => !result.IsSuccess && result.ExitCode == 9 && attempts == 1 && head == after,
                    _ => result.IsSuccess && result.StdOut.Trim() == head && attempts == 2 && head == after,
                };
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = launchSuccess, attempts, executed, headUnchanged = head == after, exitCode = result?.ExitCode, message = result?.StdErr }));
                return launchSuccess ? 0 : 1;
            }
            if (mode.StartsWith("pointer-", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                var original = "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 7\n";
                var updated = "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('b', 64) + "\nsize 9\n";
                File.WriteAllText(Path.Combine(root, "sample.txt"), mode == "pointer-added" ? "" : original);
                await Git("add sample.txt"); await Git("-c user.name=StreamQA -c user.email=stream@example.invalid commit -m Pointer");
                var invalid = mode is "pointer-invalid-size" or "pointer-overflow" or "pointer-short-oid" or "pointer-extra-text" or "pointer-version";
                if (mode == "pointer-invalid-size") updated = updated.Replace("size 9", "size not-a-number");
                if (mode == "pointer-overflow") updated = updated.Replace("size 9", "size 9999999999999999999999999999");
                if (mode == "pointer-short-oid") updated = updated.Replace(new string('b', 64), "bb");
                if (mode == "pointer-extra-text") updated += "This is ordinary text following a pointer example.\n";
                if (mode == "pointer-crlf") updated = updated.Replace("\n", "\r\n");
                if (mode == "pointer-zero") updated = updated.Replace("size 9", "size 0");
                if (mode == "pointer-version") updated = updated.Replace("spec/v1", "spec/v2");
                if (mode == "pointer-deleted") File.Delete(Path.Combine(root, "sample.txt"));
                else File.WriteAllText(Path.Combine(root, "sample.txt"), updated);
                var diff = await new Commands.Diff(root, new Models.DiffOption(change, true), 4, false, false).ReadAsync();
                var valid = invalid ? diff.LFSDiff == null && diff.TextDiff?.Lines.Any(line => line.Content.Contains(mode == "pointer-short-oid" ? "oid sha256:bb" : mode == "pointer-extra-text" ? "ordinary text" : mode == "pointer-version" ? "spec/v2" : "size ", StringComparison.Ordinal)) == true : diff.LFSDiff != null && diff.TextDiff == null;
                var modelSafe = true;
                try { var parsed = Models.LFSObject.Parse(updated); modelSafe = invalid ? parsed == null : parsed != null; }
                catch (Exception) { modelSafe = false; }
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = valid && modelSafe, diffCorrect = valid, modelSafe, lfs = diff.LFSDiff != null, textLines = diff.TextDiff?.Lines.Count }));
                return valid && modelSafe ? 0 : 1;
            }
            if (mode.StartsWith("index-", StringComparison.Ordinal))
            {
                if (mode != "index-input") Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                var blob = (await new Query { WorkingDirectory = root, Args = "rev-parse HEAD:sample.txt", UseCredentialManager = false }.ReadAsync()).StdOut.Trim();
                var changes = new System.Collections.Generic.List<Models.Change>();
                if (mode == "index-input")
                    for (var i = 0; i < 3000; i++) changes.Add(new Models.Change { Path = "absent-" + i + ".txt", Index = Models.ChangeState.Added });
                else if (mode == "index-mode")
                    changes.Add(new Models.Change { Path = "restored.sh", Index = Models.ChangeState.Deleted, DataForAmend = new Models.ChangeDataForAmend { ObjectHash = blob, FileMode = "100755" } });
                else if (mode == "index-rename")
                    changes.Add(new Models.Change { Path = "second.txt", OriginalPath = "restored.sh", Index = Models.ChangeState.Renamed, DataForAmend = new Models.ChangeDataForAmend { ObjectHash = blob, FileMode = "100755" } });
                else if (mode == "index-symlink")
                    changes.Add(new Models.Change { Path = "restored-link", Index = Models.ChangeState.Deleted, DataForAmend = new Models.ChangeDataForAmend { ObjectHash = blob, FileMode = "120000" } });
                else
                    changes.Add(new Models.Change { Path = mode == "index-path" ? "missing\n100644 " + blob + "\tinjected.txt" : "absent.txt", Index = Models.ChangeState.Added });
                var updatedIndex = await new Commands.UpdateIndexInfo(root, changes).ExecAsync();
                var entries = (await new Query { WorkingDirectory = root, Args = "ls-files --stage", UseCredentialManager = false }.ReadAsync()).StdOut;
                var correct = updatedIndex && !entries.Contains("injected.txt", StringComparison.Ordinal) &&
                    (mode is not ("index-mode" or "index-rename") || entries.Contains("100755 " + blob + " 0\trestored.sh", StringComparison.Ordinal)) &&
                    (mode != "index-rename" || !entries.Contains("second.txt", StringComparison.Ordinal)) &&
                    (mode != "index-symlink" || entries.Contains("120000 " + blob + " 0\trestored-link", StringComparison.Ordinal));
                File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success = correct, updatedIndex, entries }));
                return correct ? 0 : 1;
            }
            bool success;
            switch (mode)
            {
                case "diff": success = (await new Commands.Diff(root, new Models.DiffOption(change, true), 4, false, false).ReadAsync()).TextDiff != null; break;
                case "commits": success = (await new Commands.QueryCommits(root, "-2").GetResultAsync()).Count == 2; break;
                case "changes": success = (await new Commands.QueryLocalChanges(root).GetResultAsync()).Count > 0; break;
                case "count":
                    var counter = new Commands.CountLocalChanges(root, false);
                    success = await counter.GetResultAsync() == 1 && counter.Succeeded;
                    break;
                case "hashes": success = (await new Commands.QueryCurrentBranchCommitHashes(root, 0).GetResultAsync()).Count == 2; break;
                case "names": success = (await new Commands.QueryRevisionFileNames(root, "HEAD").GetResultAsync()).Count == 2; break;
                case "objects": success = (await new Commands.QueryRevisionObjects(root, "HEAD", "").GetResultAsync()).Count == 2; break;
                case "pickable": success = (await new Commands.QueryPickableCommits(root, "HEAD~1", "HEAD").GetResultAsync()).Count == 1; break;
                case "compare": success = (await new Commands.CompareRevisions(root, "HEAD~1", "HEAD").ReadAsync()).Count == 1; break;
                case "conflict": _ = await new Commands.QueryConflictFileState(root, change).GetResultAsync(); success = true; break;
                case "save":
                    var target = Path.Combine(root, "exported.txt");
                    await Commands.SaveRevisionFile.RunAsync(root, "HEAD", "sample.txt", target);
                    success = File.ReadAllText(target) == "before\n"; break;
                case "save-failure":
                    Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                    var existing = Path.Combine(root, "existing.txt"); File.WriteAllText(existing, "keep original target");
                    await Commands.SaveRevisionFile.RunAsync(root, "nonexistent-revision", "sample.txt", existing);
                    success = File.ReadAllText(existing) == "keep original target"; break;
                case "lfs":
                    Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                    var payload = System.Text.Encoding.UTF8.GetBytes("cached LFS data\0payload");
                    var oid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
                    var objects = Path.Combine(root, ".git/lfs/objects", oid[..2], oid.Substring(2, 2)); Directory.CreateDirectory(objects);
                    File.WriteAllBytes(Path.Combine(objects, oid), payload);
                    await using (var content = await Commands.QueryFileContent.FromLFSAsync(root, oid, payload.Length))
                    { using var buffer = new MemoryStream(); await content.CopyToAsync(buffer); success = Convert.ToHexString(buffer.ToArray()) == Convert.ToHexString(payload); }
                    break;
                case "save-lfs":
                case "save-lfs-failure":
                    Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
                    var lfsPayload = System.Text.Encoding.UTF8.GetBytes("LFS export content\0payload");
                    var lfsOid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(lfsPayload)).ToLowerInvariant();
                    var lfsObjects = Path.Combine(root, ".git/lfs/objects", lfsOid[..2], lfsOid.Substring(2, 2)); Directory.CreateDirectory(lfsObjects);
                    File.WriteAllBytes(Path.Combine(lfsObjects, lfsOid), lfsPayload);
                    File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.dat filter=lfs diff=lfs merge=lfs -text\n");
                    File.WriteAllText(Path.Combine(root, "asset.dat"), $"version https://git-lfs.github.com/spec/v1\noid sha256:{lfsOid}\nsize {lfsPayload.Length}\n");
                    await Git("add .gitattributes asset.dat"); await Git("-c user.name=StreamQA -c user.email=stream@example.invalid commit -m LFS");
                    var lfsTarget = Path.Combine(root, "exported.dat"); File.WriteAllText(lfsTarget, "keep original target");
                    await Commands.SaveRevisionFile.RunAsync(root, "HEAD", mode == "save-lfs" ? "asset.dat" : "missing.dat", lfsTarget);
                    success = mode == "save-lfs" ? Convert.ToHexString(File.ReadAllBytes(lfsTarget)) == Convert.ToHexString(lfsPayload) : File.ReadAllText(lfsTarget) == "keep original target";
                    success &= Directory.GetFiles(root, "*.treegit-*.tmp").Length == 0;
                    break;
                default: throw new ArgumentException("Unknown streaming query case");
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new { mode, success }));
            return success ? 0 : 1;
        }
    }
}
