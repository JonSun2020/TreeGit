using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Tests
{
    internal static class ExternalToolProbe
    {
        public static async Task<int> RunAsync(string repository, string file, string plan, string report)
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(repository)!, "treegit-owned-test"))) return 64;
            var messages = new List<string>();
            Models.Notification.Raised += notice => messages.Add(notice.Message);
            try
            {
                Native.OS.GitExecutable = Native.OS.FindGitExecutable();
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(plan));
                var settings = document.RootElement;
                Native.OS.ExternalMergerType = settings.TryGetProperty("adapter", out var adapter) ? Models.ExternalMerger.Supported.FindIndex(tool => tool.Icon == adapter.GetString()) : 1;
                Native.OS.ExternalMergerExecFile = settings.GetProperty("executable").GetString();
                var arguments = settings.GetProperty("arguments").GetString();
                var useSupported = settings.TryGetProperty("useSupportedArguments", out var supported) && supported.GetBoolean();
                var selected = Models.ExternalMerger.Supported[Native.OS.ExternalMergerType];
                Native.OS.ExternalMergeArgs = useSupported ? arguments + " " + selected.MergeCmd : arguments;
                Native.OS.ExternalDiffArgs = useSupported ? arguments + " " + selected.DiffCmd : arguments;
                var command = new Commands.MergeTool(repository, file);
                var success = settings.GetProperty("kind").GetString() == "diff"
                    ? await new Commands.DiffTool(repository, new Models.DiffOption(
                        settings.TryGetProperty("from", out var from) ? from.GetString() : "HEAD~1",
                        settings.TryGetProperty("to", out var to) ? to.GetString() : "HEAD", new Models.Change { Path = file })).OpenAsync()
                    : await command.OpenAsync();
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    success, messages, failure = command.FailureMessage,
                    builtinFirst = Models.ExternalMerger.Supported[0].IsBuiltIn,
                    adapterNames = Models.ExternalMerger.Supported.Select(tool => tool.Name).ToArray(),
                }));
                return 0;
            }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { error = error.ToString(), messages }));
                return 1;
            }
        }
    }
}
