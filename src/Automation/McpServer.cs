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
        internal static string PipeName => "TreeGit.MCP." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant() + "\n" + Environment.UserName)))[..24];

        internal static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
        { "ui_state", "ui_screenshot", "git_status", "git_log", "git_diff", "git_refs", "git_show" };
        internal static readonly HashSet<string> UiTools = new(StringComparer.Ordinal)
        { "ui_open_repository", "ui_select_view", "ui_select_commit", "ui_refresh", "ui_window", "ui_command", "ui_graph", "ui_history_columns", "ui_working_copy" };

        private static object Field(string description, string type = "string", string[] values = null)
            => values == null ? new { type, description } : (object)new { type, description, @enum = values };

        private static object Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
            => new
            {
                name,
                description,
                inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false },
                annotations = new { readOnlyHint = ReadTools.Contains(name), destructiveHint = name is "git_action" or "git_run", idempotentHint = name is not ("git_action" or "git_clone" or "git_run"), openWorldHint = name is "git_action" or "git_clone" or "git_run" },
            };

        private static object[] Tools(bool allowWrite)
        {
            var repo = Field("Absolute path of a repository opened in this TreeGit window.");
            var tools = new List<object>
            {
                Tool("ui_state", "Inspect the running TreeGit application's repository tabs, selected view, branch and pending dialog.", []),
                Tool("ui_screenshot", "Capture the running TreeGit window or its open dialog as a PNG image.", new() { ["window"] = Field("Window to capture (default main).", values: ["main", "active"]) }),
                Tool("ui_open_repository", "Open or activate a local Git repository in the TreeGit GUI. Does not initialize a repository.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_select_view", "Select history, working copy or stashes in the TreeGit GUI.", new() { ["repo"] = repo, ["view"] = Field("View to display.", values: ["history", "working_copy", "stashes"]) }, ["repo", "view"]),
                Tool("ui_select_commit", "Navigate to a commit in TreeGit's history and details view.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
                Tool("ui_graph", "Choose the branch pinned to the leftmost graph lane and the visible history. Does not check out or modify Git refs. all shows all branches; only shows just the selected first-parent line; merged includes its merged ancestry.", new() { ["repo"] = repo, ["branch"] = Field("Existing local/remote branch name, full ref, or HEAD for the checked-out branch."), ["mode"] = Field("History visibility.", values: ["all", "only", "merged"]) }, ["repo"]),
                Tool("ui_working_copy", "Select the uncommitted-changes row at the top of history and show staged/unstaged files and diff beneath the graph.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_history_columns", "Inspect, resize and save history column widths for this repository. Widths are pixels; reset restores defaults. Graph minimum 40, description 100, other columns 60; maximum 8192.", new() { ["repo"] = repo, ["reset"] = Field("Restore default widths before applying supplied values.", "boolean"), ["graph"] = Field("Graph width.", "integer"), ["description"] = Field("Description width.", "integer"), ["date"] = Field("Commit date width.", "integer"), ["author"] = Field("Author width.", "integer"), ["commit"] = Field("Commit hash width.", "integer"), ["authorDate"] = Field("Author date width.", "integer") }, ["repo"]),
                Tool("ui_refresh", "Refresh the repository's branches, history, files, stashes, submodules and worktrees in the GUI.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_window", "Adjust the desktop window size, theme or working-copy branch sidebar. All are optional; supplied preferences are saved.", new() { ["width"] = Field("Window width, 1024 to 3840.", "integer"), ["height"] = Field("Window height, 640 to 2160.", "integer"), ["theme"] = Field("TreeGit theme.", values: ["Light", "Dark", "Default", "SourceTree"]), ["collapseBranches"] = Field("Collapse branch groups in the working-copy view, retaining compact workspace navigation.", "boolean") }),
                Tool("ui_command", "Open TreeGit's original dialogs or dismiss notifications. No Git operation is submitted by this tool. Repository commands require repo. Opening another dialog is rejected while one is already open.", new() { ["repo"] = repo, ["command"] = Field("GUI command.", values: ["about", "mcp_connection", "preferences", "repository_settings", "credentials", "clear_notifications", "close_dialog", "create_branch", "create_tag", "fetch_dialog", "pull_dialog", "push_dialog", "stash_dialog", "apply_patch", "cancel_popup"]) }, ["command"]),
                Tool("git_status", "Read tracked/untracked changes and branch status. Never returns saved credentials.", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_log", "Read recent commits with graph and references.", new() { ["repo"] = repo, ["count"] = Field("Maximum commits, 1 to 500 (default 50).", "integer"), ["all"] = Field("Include all branches.", "boolean") }, ["repo"]),
                Tool("git_diff", "Read staged or unstaged textual diff, optionally restricted to repository-relative paths.", new() { ["repo"] = repo, ["staged"] = Field("Read staged changes.", "boolean"), ["paths"] = new { type = "array", items = new { type = "string" } } }, ["repo"]),
                Tool("git_refs", "Read branches and tags (remote URLs and authentication data are excluded).", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_show", "Read commit metadata and patch for a revision.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
            };
            if (allowWrite)
            {
                tools.Add(Tool("git_run", "Execute any Git operation in an opened repository and refresh TreeGit. Supports all Git subcommands, including reset, clean, branch/tag deletion and force push. Arguments are passed directly to Git, without a shell. Uses normal repository credentials and authentication dialogs.", new()
                {
                    ["repo"] = repo,
                    ["arguments"] = new { type = "array", items = new { type = "string" }, description = "Git argument vector, e.g. ['status', '--short'] or ['reset', '--hard', 'HEAD']." },
                }, ["repo", "arguments"]));
                tools.Add(Tool("git_clone", "Clone through TreeGit's original clone dialog/workflow, show progress in the GUI, then open the resulting repository. Destination must not exist; parent directory must already exist. Authentication uses normal prompts. Credentials must not be embedded in the URL.", new()
                {
                    ["url"] = Field("Git remote URL or existing local repository path."),
                    ["destination"] = Field("Absolute path of a new repository directory."),
                    ["recursive"] = Field("Initialize submodules after cloning (default true).", "boolean"),
                    ["sshKey"] = Field("Optional absolute SSH private key path; passphrase is entered through the normal dialog."),
                    ["credentialManager"] = Field("Use the original system/OAuth authentication flow for this clone and resulting repository (default false).", "boolean"),
                }, ["url", "destination"]));
                tools.Add(Tool("git_action", "Perform an explicit Git operation in the selected GUI repository and refresh its UI. No arbitrary shell command, force push or hard reset is accepted. Hooks configured by the repository still execute. Commit uses already staged files. Remote authentication uses the application's normal dialogs and per-repository credentials.", new()
                {
                    ["repo"] = repo,
                    ["action"] = Field("Operation.", values: ["stage", "unstage", "commit", "checkout", "create_branch", "fetch", "pull", "push", "stash", "stash_pop", "merge", "rebase", "cherry_pick", "revert", "merge_abort", "rebase_abort", "cherry_pick_abort", "revert_abort", "merge_continue", "rebase_continue", "cherry_pick_continue", "revert_continue"]),
                    ["paths"] = new { type = "array", items = new { type = "string" }, description = "Required for stage/unstage. Use ['.'] explicitly for all files." },
                    ["revision"] = Field("Required for checkout, create_branch (new name), merge, rebase, cherry_pick and revert."),
                    ["message"] = Field("Commit or stash message. Required for commit."),
                    ["remote"] = Field("Configured remote name (defaults to origin)."),
                }, ["repo", "action"]));
            }
            return tools.ToArray();
        }

        private sealed class RpcException(int code, string message) : Exception(message)
        {
            public int Code { get; } = code;
        }

        private static void ValidateTool(string name, JsonElement arguments, bool allowWrite)
        {
            var tools = JsonSerializer.SerializeToElement(Tools(allowWrite));
            var tool = tools.EnumerateArray().FirstOrDefault(t => t.GetProperty("name").GetString() == name);
            if (tool.ValueKind == JsonValueKind.Undefined)
                throw new RpcException(-32602, "Unknown or disabled tool. This connection may use --read-only.");
            if (arguments.ValueKind != JsonValueKind.Object)
                throw new RpcException(-32602, "arguments must be an object.");
            var schema = tool.GetProperty("inputSchema");
            foreach (var required in schema.GetProperty("required").EnumerateArray())
            {
                if (!arguments.TryGetProperty(required.GetString(), out _))
                    throw new RpcException(-32602, "Missing required argument: " + required.GetString());
            }
            var properties = schema.GetProperty("properties");
            foreach (var argument in arguments.EnumerateObject())
            {
                if (!properties.TryGetProperty(argument.Name, out var property))
                    throw new RpcException(-32602, "Unknown argument: " + argument.Name);
                var type = property.GetProperty("type").GetString();
                var valid = type switch
                {
                    "string" => argument.Value.ValueKind == JsonValueKind.String,
                    "integer" => argument.Value.ValueKind == JsonValueKind.Number && argument.Value.TryGetInt32(out _),
                    "boolean" => argument.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "array" => argument.Value.ValueKind == JsonValueKind.Array && argument.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
                    _ => false,
                };
                if (!valid)
                    throw new RpcException(-32602, "Invalid argument type: " + argument.Name);
                if (property.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(v => v.GetString() == argument.Value.GetString()))
                    throw new RpcException(-32602, "Invalid argument value: " + argument.Name);
            }
        }

        public static async Task<int> RunAsync(string[] args)
        {
            var allowWrite = !args.Contains("--read-only", StringComparer.Ordinal);
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
                    if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                    {
                        hasId = false;
                        throw new ArgumentException("Invalid request id.");
                    }
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
                            result = new { protocolVersion = negotiatedVersion, capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "treegit-desktop", version = "0.1.0" }, instructions = "Controls the running TreeGit desktop. Open a repository before using Git tools. Read/write by default, including all Git operations through git_run. Use --read-only to disable mutations. Credentials are never exposed as tools." };
                            break;
                        case "ping":
                            result = new { };
                            break;
                        case "tools/list" when initialized:
                            result = new { tools = Tools(allowWrite) };
                            break;
                        case "tools/call" when initialized:
                            try
                            {
                                var p = request.GetProperty("params");
                                var name = p.GetProperty("name").GetString();
                                var arguments = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonSerializer.SerializeToElement(new { });
                                ValidateTool(name, arguments, allowWrite);
                                var data = await SendAsync(new { name, arguments, allowWrite }).ConfigureAwait(false);
                                if (data.TryGetProperty("error", out var error))
                                    throw new InvalidOperationException(error.GetString());
                                if (name == "ui_screenshot")
                                    result = new { content = new object[] { new { type = "image", data = data.GetProperty("png").GetString(), mimeType = "image/png" } }, isError = false };
                                else
                                    result = new { content = new[] { new { type = "text", text = data.GetRawText() } }, isError = data.TryGetProperty("isSuccess", out var success) && !success.GetBoolean() };
                            }
                            catch (RpcException) { throw; }
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
                catch (RpcException ex)
                {
                    response = new { jsonrpc = "2.0", id = hasId ? (object)id : null, error = new { code = ex.Code, message = ex.Message } };
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
            try
            { await pipe.ConnectAsync(1500).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                await EnsureDesktopAsync().ConfigureAwait(false);
                await pipe.ConnectAsync(20000).ConfigureAwait(false);
            }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            // Authentication dialogs and Git operations may legitimately need user input.
            var response = await reader.ReadLineAsync().ConfigureAwait(false) ?? throw new IOException("TreeGit GUI connection closed.");
            using var document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }

        private static Task EnsureDesktopAsync() => Task.Run(() =>
        {
            // Coordinate cold starts across different AI clients. Keep mutex ownership
            // on this synchronous worker thread, including while waiting for the GUI.
            using var gate = new Mutex(false, "TreeGit.MCP.Start." + PipeName);
            var acquired = false;
            try
            {
                try
                { acquired = gate.WaitOne(TimeSpan.FromSeconds(25)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                    throw new TimeoutException("TreeGit desktop startup is still in progress.");
                using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                try
                { probe.Connect(250); return; }
                catch (TimeoutException) { }
                // ShellExecute prevents the GUI from inheriting the stdio endpoint's
                // pipe handles, so client EOF remains observable after the server exits.
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                probe.Connect(20000);
            }
            finally
            {
                if (acquired)
                    gate.ReleaseMutex();
            }
        });
    }
}
