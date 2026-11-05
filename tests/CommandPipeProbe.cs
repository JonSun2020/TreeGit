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
    }
}
