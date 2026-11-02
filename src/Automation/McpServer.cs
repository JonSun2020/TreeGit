using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Automation
{
    // Explicitly launched stdio MCP server. The desktop process never owns protocol stdout.
    internal static class McpServer
    {
        internal static string PipeName => "SourceGit.MCP." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant() + "\n" + Environment.UserName)))[..24];

        internal static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
        { "ui_state", "ui_screenshot", "git_status", "git_log", "git_diff", "git_refs", "git_show" };
        internal static readonly HashSet<string> UiTools = new(StringComparer.Ordinal)
        { "ui_open_repository", "ui_select_view", "ui_select_commit", "ui_refresh", "ui_window" };

        private static object Field(string description, string type = "string", string[] values = null)
            => values == null ? new { type, description } : (object)new { type, description, @enum = values };

        private static object Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
            => new
            {
                name, description,
                inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false },
                annotations = new { readOnlyHint = ReadTools.Contains(name), destructiveHint = name == "git_action", idempotentHint = name != "git_action", openWorldHint = name == "git_action" },
            };

        private static object[] Tools(bool allowWrite)
        {
            var repo = Field("Absolute path of a repository opened in this SourceGit window.");
            var tools = new List<object>
            {
                Tool("ui_state", "Inspect the running SourceGit application's repository tabs, selected view, branch and pending dialog.", []),
                Tool("ui_screenshot", "Capture the running SourceGit window as a PNG image.", []),
                Tool("ui_open_repository", "Open or activate a local Git repository in the SourceGit GUI. Does not initialize a repository.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_select_view", "Select history, working copy or stashes in the SourceGit GUI.", new() { ["repo"] = repo, ["view"] = Field("View to display.", values: ["history", "working_copy", "stashes"]) }, ["repo", "view"]),
                Tool("ui_select_commit", "Navigate to a commit in SourceGit's history and details view.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
                Tool("ui_refresh", "Refresh the repository's branches, history, files, stashes, submodules and worktrees in the GUI.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_window", "Adjust the desktop window size or select an existing SourceGit theme. All are optional; saved theme preference is updated when supplied.", new() { ["width"] = Field("Window width, 1024 to 3840.", "integer"), ["height"] = Field("Window height, 640 to 2160.", "integer"), ["theme"] = Field("SourceGit theme.", values: ["Light", "Dark", "Default"]) }),
                Tool("git_status", "Read tracked/untracked changes and branch status. Never returns saved credentials.", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_log", "Read recent commits with graph and references.", new() { ["repo"] = repo, ["count"] = Field("Maximum commits, 1 to 500 (default 50).", "integer"), ["all"] = Field("Include all branches.", "boolean") }, ["repo"]),
                Tool("git_diff", "Read staged or unstaged textual diff, optionally restricted to repository-relative paths.", new() { ["repo"] = repo, ["staged"] = Field("Read staged changes.", "boolean"), ["paths"] = new { type = "array", items = new { type = "string" } } }, ["repo"]),
                Tool("git_refs", "Read branches and tags (remote URLs and authentication data are excluded).", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_show", "Read commit metadata and patch for a revision.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
            };
            if (allowWrite)
                tools.Add(Tool("git_action", "Perform an explicit Git operation in the selected GUI repository and refresh its UI. No arbitrary shell command, force push or hard reset is accepted. Hooks configured by the repository still execute. Commit uses already staged files. Remote authentication uses the application's normal dialogs and per-repository credentials.", new()
                {
                    ["repo"] = repo,
                    ["action"] = Field("Operation.", values: ["stage", "unstage", "commit", "checkout", "create_branch", "fetch", "pull", "push", "stash", "stash_pop", "merge", "rebase", "cherry_pick", "revert", "merge_abort", "rebase_abort", "cherry_pick_abort", "revert_abort", "merge_continue", "rebase_continue", "cherry_pick_continue", "revert_continue"]),
                    ["paths"] = new { type = "array", items = new { type = "string" }, description = "Required for stage/unstage. Use ['.'] explicitly for all files." },
                    ["revision"] = Field("Required for checkout, create_branch (new name), merge, rebase, cherry_pick and revert."),
                    ["message"] = Field("Commit or stash message. Required for commit."),
                    ["remote"] = Field("Configured remote name (defaults to origin)."),
                }, ["repo", "action"]));
            return tools.ToArray();
        }

        public static async Task<int> RunAsync(string[] args)
        {
            var allowWrite = args.Contains("--allow-write", StringComparer.Ordinal);
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var initialized = false;
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonElement id = default;
                bool hasId = false;
                object response;
                try
                {
                    if (line.Length > 2 * 1024 * 1024)
                        throw new ArgumentException("Request is too large.");
                    using var document = JsonDocument.Parse(line);
                    var request = document.RootElement;
                    hasId = request.TryGetProperty("id", out id);
                    id = hasId ? id.Clone() : default;
                    if (!request.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" || !request.TryGetProperty("method", out var methodElement))
                        throw new ArgumentException("Invalid JSON-RPC 2.0 request.");
                    var method = methodElement.GetString();
                    if (!hasId)
                        continue; // Initialized/cancelled notifications require no response.
                    object result;
                    switch (method)
                    {
                        case "initialize":
                            initialized = true;
                            var requestedVersion = request.TryGetProperty("params", out var init) && init.TryGetProperty("protocolVersion", out var requested) ? requested.GetString() : null;
                            var negotiatedVersion = requestedVersion is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? requestedVersion : "2025-11-25";
                            result = new { protocolVersion = negotiatedVersion, capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "sourcegit-desktop", version = "0.1.0" }, instructions = "Controls the running SourceGit desktop. Open a repository before using Git tools. Read-only by default; --allow-write exposes explicit mutation tools. Credentials are never exposed as tools." };
                            break;
                        case "ping": result = new { }; break;
                        case "tools/list" when initialized:
                            result = new { tools = Tools(allowWrite) };
                            break;
                        case "tools/call" when initialized:
                            try
                            {
                                var p = request.GetProperty("params");
                                var name = p.GetProperty("name").GetString();
                                if (!ReadTools.Contains(name ?? "") && !UiTools.Contains(name ?? "") && !(name == "git_action" && allowWrite))
                                    throw new ArgumentException("Unknown or disabled tool. Git writes require --allow-write.");
                                var arguments = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonSerializer.SerializeToElement(new { });
                                var data = await SendAsync(new { name, arguments, allowWrite }).ConfigureAwait(false);
                                if (data.TryGetProperty("error", out var error))
                                    throw new InvalidOperationException(error.GetString());
                                if (name == "ui_screenshot")
                                    result = new { content = new object[] { new { type = "image", data = data.GetProperty("png").GetString(), mimeType = "image/png" } }, isError = false };
                                else
                                    result = new { content = new[] { new { type = "text", text = data.GetRawText() } }, isError = data.TryGetProperty("isSuccess", out var success) && !success.GetBoolean() };
                            }
                            catch (Exception ex)
                            {
                                result = new { content = new[] { new { type = "text", text = ex.Message } }, isError = true };
                            }
                            break;
                        default:
                            response = new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found or initialize required." } };
                            await output.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                            continue;
                    }
                    response = new { jsonrpc = "2.0", id, result };
                }
                catch (JsonException)
                {
                    response = new { jsonrpc = "2.0", id = (object)null, error = new { code = -32700, message = "Parse error" } };
                }
                catch (Exception ex)
                {
                    response = new { jsonrpc = "2.0", id = hasId ? (object)id : null, error = new { code = -32600, message = ex.Message } };
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
            return 0;
        }

        private static async Task<JsonElement> SendAsync(object request)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(1500).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // The same portable executable hosts both the GUI and the stdio endpoint.
                // Do not let the long-lived GUI inherit the MCP client's stdio handles:
                // otherwise an exiting stdio server leaves its output pipe open forever.
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
                });
                await pipe.ConnectAsync(20000).ConfigureAwait(false);
            }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            // Authentication dialogs and Git operations may legitimately need user input.
            var response = await reader.ReadLineAsync().ConfigureAwait(false) ?? throw new IOException("SourceGit GUI connection closed.");
            using var document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }
    }
}
