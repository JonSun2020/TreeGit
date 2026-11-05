using System;
using System.IO;
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
            Directory.CreateDirectory(root);
            Native.OS.GitExecutable = Native.OS.FindGitExecutable();
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

        private static async Task<int> RunStreamAsync(string root, string mode, string report)
        {
            async Task Git(string arguments)
            {
                var result = await new Query { WorkingDirectory = root, Args = arguments, UseCredentialManager = false, Editor = Commands.Command.EditorType.None }.ReadAsync();
                if (!result.IsSuccess) throw new IOException(result.StdErr);
            }
            await Git("init -b main");
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
            bool success;
            switch (mode)
            {
                case "diff": success = (await new Commands.Diff(root, new Models.DiffOption(change, true), 4, false, false).ReadAsync()).TextDiff != null; break;
                case "commits": success = (await new Commands.QueryCommits(root, "-2").GetResultAsync()).Count == 2; break;
                case "changes": success = (await new Commands.QueryLocalChanges(root).GetResultAsync()).Count > 0; break;
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
