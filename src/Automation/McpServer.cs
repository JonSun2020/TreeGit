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

        private static Mutex _desktopStartup;
        private static string DesktopStartupName => "TreeGit.Desktop.Start." + PipeName;

        internal static void BeginDesktopStartup() => _desktopStartup = new Mutex(false, DesktopStartupName);
        internal static void EndDesktopStartup() => Interlocked.Exchange(ref _desktopStartup, null)?.Dispose();

        internal static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
        { "ui_state", "ui_screenshot", "git_status", "git_log", "git_diff", "git_refs", "git_show" };
        internal static readonly HashSet<string> UiTools = new(StringComparer.Ordinal)
        { "ui_open_repository", "ui_select_view", "ui_select_commit", "ui_refresh", "ui_window", "ui_fonts", "ui_update", "ui_command", "ui_tabs", "ui_diff", "ui_stashes", "ui_search", "ui_graph", "ui_history_columns", "ui_working_copy", "ui_layout", "ui_merge_result" };

        private static object Field(string description, string type = "string", string[] values = null)
            => values == null ? new { type, description } : (object)new { type, description, @enum = values };

        private static object Tool(string name, string description, Dictionary<string, object> properties, string[] required = null)
            => new
            {
                name,
                description,
                inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false },
                annotations = new { readOnlyHint = ReadTools.Contains(name), destructiveHint = name is "git_action" or "git_run" or "ui_merge_result", idempotentHint = name is not ("git_action" or "git_clone" or "git_run" or "ui_command" or "ui_tabs" or "ui_update" or "ui_merge_result"), openWorldHint = name is "git_action" or "git_clone" or "git_run" or "ui_update" },
            };

        private static Dictionary<string, object> LayoutFields()
        {
            var actions = new List<string> { "list", "drag", "restore", "resize", "scroll" };
#if INTEGRATION_TESTS
            actions.Add("test_click");
            actions.Add("test_set");
#endif
            var fields = new Dictionary<string, object> { ["action"] = Field("Layout operation.", values: actions.ToArray()), ["window"] = Field("Main window or active dialog.", values: ["main", "active"]), ["splitter"] = Field("Index returned by list; required for drag/restore.", "integer"), ["scroller"] = Field("Scroll area index returned by list.", "integer"), ["scrollbar"] = Field("Scroll bar index returned by list; alternative to scroller.", "integer"), ["value"] = Field("Absolute scroll-bar value for scrollbar.", "number"), ["dx"] = Field("Horizontal drag change, or scroll offset, in UI units.", "number"), ["dy"] = Field("Vertical drag change, or scroll offset, in UI units.", "number"), ["width"] = Field("Window width for resize.", "number"), ["height"] = Field("Window height for resize.", "number"), ["columns"] = new { type = "array", items = new { type = "string" } }, ["rows"] = new { type = "array", items = new { type = "string" } }, ["twoColumnHistory"] = Field("Use side-by-side history/detail panes.", "boolean"), ["twoColumnDiff"] = Field("Use side-by-side text differences.", "boolean") };
#if INTEGRATION_TESTS
            fields["button"] = Field("Named button in the isolated integration application.");
            fields["tempDirectory"] = Field("CI-only fixture file used to exercise an unavailable temporary directory.");
            fields["text"] = Field("CI-only text value for a named input control.");
#endif
            return fields;
        }

        private static object[] Tools(bool allowWrite)
        {
            var repo = Field("Absolute path of a repository opened in this TreeGit window.");
            var workingCopyFields = new Dictionary<string, object> { ["repo"] = repo, ["filter"] = Field("Filter changed file paths; empty clears the filter."), ["status"] = Field("File-status filter.", values: ["all", "conflicts", "modified", "untracked"]), ["sort"] = Field("File-list sorting.", values: ["path", "name", "status"]) };
            if (allowWrite) workingCopyFields["message"] = Field("Replace the commit-message draft, including empty to clear; does not commit.");
            var tools = new List<object>
            {
                Tool("ui_state", "Inspect TreeGit tabs, selected view, branches and dialogs. Search metadata is paged; use the returned counts and truncation flags to read every page without changing the UI.", new() {
                    ["searchOffset"] = Field("Search result offset, default 0.", "integer"),
                    ["searchLimit"] = Field("Search results per repository, default 64, range 0–256. Zero reads counts only.", "integer"),
                    ["searchBranchOffset"] = Field("Containing-branch offset within each returned search result, default 0.", "integer"),
                    ["searchBranchLimit"] = Field("Containing branches per search result, default 128, range 0–512. Zero reads counts only.", "integer"),
                }),
                Tool("ui_tabs", "List tabs with their loading status, open their dropdown, select, close or move a tab. Index is zero-based.", new() { ["action"] = Field("Tab action.", values: ["list", "dropdown", "select", "close", "move"]), ["index"] = Field("Tab index; required for select, close and move.", "integer"), ["targetIndex"] = Field("Destination index; required for move.", "integer") }, ["action"]),
                Tool("ui_screenshot", "Capture the running TreeGit window or its open dialog as a PNG image.", new() { ["window"] = Field("Window to capture (default main).", values: ["main", "active"]) }),
                Tool("ui_open_repository", "Open or activate a local Git repository in the TreeGit GUI. Does not initialize a repository.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_select_view", "Select history, working copy or stashes in the TreeGit GUI.", new() { ["repo"] = repo, ["view"] = Field("View to display.", values: ["history", "working_copy", "stashes", "search"]) }, ["repo", "view"]),
                Tool("ui_select_commit", "Navigate to a commit in TreeGit's history and details view.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
                Tool("ui_search", "Open the search page, query commits or select a result to inspect its files and diff.", new() { ["repo"] = repo, ["query"] = Field("Search text; an empty string clears results."), ["method"] = Field("Search method.", values: ["sha", "author", "message", "path", "content"]), ["currentBranchOnly"] = Field("Limit the search to the checked-out branch.", "boolean"), ["select"] = Field("SHA or prefix of an existing search result."), ["open"] = Field("Open the selected result in history, equivalent to double-click. Switches to all branches with remotes and selects the commit.", "boolean"), ["showBranches"] = Field("Open the containing-branches popup for the selected visible result.", "boolean"), ["branchFilter"] = Field("Filter the opened containing-branches popup; empty clears it."), ["branch"] = Field("Full containing branch ref to select when open=true; does not check out the branch.") }, ["repo"]),
                Tool("ui_graph", "Set history visibility. The checked-out branch always stays in the leftmost graph lane. The optional branch navigates to its tip in all mode; other branches are ignored in current mode. Does not check out or modify Git refs.", new() { ["repo"] = repo, ["branch"] = Field("Navigate to a local/remote branch tip, full ref, or HEAD. Ignored for other branches in current mode."), ["showRemotes"] = Field("Show remote branches, including the checked-out branch upstream in current mode.", "boolean"), ["mode"] = Field("History visibility.", values: ["all", "current", "only", "merged"]) }, ["repo"]),
                Tool("ui_diff", "Select a changed file using the normal staged/unstaged/history view; inspect and navigate its binary comparison. Binary reads return at most 256 bytes per side at the visible offset. Select a commit first for history.", new() { ["repo"] = repo, ["action"] = Field("Diff action.", values: ["select", "state", "first", "last", "next", "previous", "goto", "window", "retry"]), ["file"] = Field("Repository-relative changed file path for select."), ["source"] = Field("Change list for select.", values: ["unstaged", "staged", "history", "stash"]), ["offset"] = Field("Absolute byte offset for goto.", "integer") }, ["repo"]),
                Tool("ui_stashes", "Inspect the stash sidebar; expand/collapse the group, filter or select a stash for viewing. Never applies or deletes a stash.", new() { ["repo"] = repo, ["action"] = Field("Sidebar action.", values: ["list", "expand", "collapse", "select"]), ["stash"] = Field("Stash name or full SHA for select."), ["filter"] = Field("Filter the stash list; empty clears the filter.") }, ["repo"]),
                Tool("ui_working_copy", "Show working-copy files beneath history; the synthetic row appears only when there are changes. Filter and sort the file lists, or edit the commit draft; never commits. Returns visible counts and up to 200 paths per list.", workingCopyFields, ["repo"]),
                Tool("ui_history_columns", "Inspect, resize and save history column widths for this repository. Widths are pixels; reset restores defaults. Graph minimum 40, description 100, other columns 60; maximum 8192.", new() { ["repo"] = repo, ["reset"] = Field("Restore default widths before applying supplied values.", "boolean"), ["graph"] = Field("Graph width.", "integer"), ["description"] = Field("Description width.", "integer"), ["date"] = Field("Commit date width.", "integer"), ["author"] = Field("Author width.", "integer"), ["commit"] = Field("Commit hash width.", "integer"), ["authorDate"] = Field("Author date width.", "integer") }, ["repo"]),
                Tool("ui_refresh", "Refresh the repository's branches, history, files, stashes, submodules and worktrees in the GUI.", new() { ["repo"] = repo }, ["repo"]),
                Tool("ui_fonts", "Inspect or choose interface/code fonts. list enumerates installed fonts only on demand; set accepts a listed font, or an empty font to restore the bundled/default font.", new() { ["action"] = Field("Font action.", values: ["state", "list", "set"]), ["kind"] = Field("Font target for list or set.", values: ["interface", "code"]), ["font"] = Field("Listed font family; empty restores default.") }, ["action"]),
                Tool("ui_layout", "Inspect TreeGit panes or drag a visible splitter using its normal drag events. List first to obtain indexes and grid lengths; restore reuses those lengths. Operates only on this application's windows, does not modify Git.", LayoutFields()),
                Tool("ui_window", "Adjust the desktop window size, theme or working-copy branch sidebar. All are optional; supplied preferences are saved. Returns the selected theme and effectiveTheme currently rendered.", new() { ["width"] = Field("Window width, 1024 to 3840.", "integer"), ["height"] = Field("Window height, 640 to 2160.", "integer"), ["theme"] = Field("Default follows the operating system; Light, Dark and Blue are fixed themes. SourceTree is a legacy alias for Blue.", values: ["Light", "Dark", "Default", "Blue", "SourceTree"]), ["state"] = Field("Window state.", values: ["normal", "maximized", "minimized", "toggle"]), ["zoom"] = Field("Interface zoom, 0.75 to 2.5.", "number"), ["collapseBranches"] = Field("Collapse branch groups in the working-copy view, retaining compact workspace navigation.", "boolean") }),
                Tool("ui_command", "Open TreeGit's original dialogs or dismiss notifications. No repository operation is submitted by this tool. save_preferences explicitly saves app and global Git settings in the open preferences dialog. Repository commands require repo. Opening another dialog is rejected while one is already open.", new() { ["repo"] = repo, ["file"] = Field("Repository-relative file or directory for file_history/directory_history."), ["revision"] = Field("Full commit hash for merge_dialog."), ["revisions"] = new { type = "array", items = new { type = "string" } }, ["mode"] = Field("Merge mode for merge_options.", values: ["", "--ff-only", "--no-ff", "--squash", "--no-ff --no-commit"]), ["branchName"] = Field("Name in the open create-branch dialog."), ["checkoutAfterCreated"] = Field("Switch to the new branch after creating it.", "boolean"), ["onlyStaged"] = Field("Stash only staged changes.", "boolean"), ["includeUntracked"] = Field("Include untracked files when stashing.", "boolean"), ["stashMode"] = Field("State after creating a stash.", values: ["discard", "keep_index", "keep_all"]), ["autoStash"] = Field("Keep and restore tracked local changes during merge.", "boolean"), ["allowUnrelated"] = Field("Explicitly allow unrelated histories.", "boolean"), ["base"] = Field("Base branch for compare_branches, or loaded commit SHA for compare_commits/interactive_rebase."), ["to"] = Field("Target branch for compare_branches, or loaded commit SHA for compare_commits."), ["page"] = Field("Preferences page; only for command=preferences.", values: ["general", "appearance", "git", "signing", "integration"]), ["command"] = Field("GUI command.", values: new[] { "new_tab", "close_tab", "about", "ssh_keys", "file_history", "directory_history", "merge_conflicts", "merge_dialog", "merge_multiple_dialog", "checkout_dialog", "checkout_fast_forward_dialog", "checkout_commit_dialog", "merge_options", "compare_branches", "compare_commits", "submodule_compare", "interactive_rebase", "commit_info", "commit_branches", "commit_changes", "commit_files", "git_logs", "check_updates", "external_diff", "mcp_connection", "preferences", "repository_settings", "repository_settings_advanced", "repository_settings_remotes", "credentials", "clear_notifications", "close_dialog", "save_preferences", "create_branch", "create_branch_options", "create_tag", "reset_dialog", "fetch_dialog", "pull_dialog", "push_dialog", "stash_dialog", "stash_options", "stash_apply_dialog", "stash_drop_dialog", "stash_branch_dialog", "apply_patch", "show_reflog", "cancel_popup" }.Where(command => allowWrite || command != "save_preferences").ToArray()) }, ["command"]),
                Tool("git_status", "Read tracked/untracked changes and branch status. Never returns saved credentials.", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_log", "Read recent commits with graph and references.", new() { ["repo"] = repo, ["count"] = Field("Maximum commits, 1 to 500 (default 50).", "integer"), ["all"] = Field("Include all branches.", "boolean") }, ["repo"]),
                Tool("git_diff", "Read staged or unstaged textual diff, optionally restricted to repository-relative paths.", new() { ["repo"] = repo, ["staged"] = Field("Read staged changes.", "boolean"), ["paths"] = new { type = "array", items = new { type = "string" } } }, ["repo"]),
                Tool("git_refs", "Read branches and tags (remote URLs and authentication data are excluded).", new() { ["repo"] = repo }, ["repo"]),
                Tool("git_show", "Read commit metadata and patch for a revision.", new() { ["repo"] = repo, ["revision"] = Field("Commit hash or reference.") }, ["repo", "revision"]),
            };
            if (allowWrite)
            {
                tools.Add(Tool("ui_merge_result", "Inspect or edit an open merge result, resolve one original conflict, undo/redo, or explicitly save and stage. Open it first with ui_command merge_conflicts. Never commits. A read-only connection cannot call this tool.", new() { ["repo"] = repo, ["file"] = Field("Repository-relative conflicted file."), ["action"] = Field("Operation.", values: ["read", "replace", "resolve", "undo", "redo", "save"]), ["offset"] = Field("Character offset, default 0.", "integer"), ["length"] = Field("Character count; read defaults to 32768, replace defaults to the remaining result.", "integer"), ["text"] = Field("Replacement text, including an empty string to delete."), ["region"] = Field("Zero-based original conflict index.", "integer"), ["choice"] = Field("Left/right follow the displayed Local/Remote panels. Ours/theirs retain native Git index semantics, which are reversed during rebase.", values: ["left", "right", "left_then_right", "right_then_left", "ours", "theirs", "ours_then_theirs", "theirs_then_ours", "restore"]) }, ["repo", "file", "action"]));
                tools.Add(Tool("ui_update", "Check official TreeGit releases, inspect visible or background progress, download in the background, restore the progress window, cancel or apply a ready update. apply restarts TreeGit and can disconnect this client. Existing Git operations must finish first.", new() { ["action"] = Field("Update action.", values: ["check", "state", "download", "background", "foreground", "cancel", "apply"]) }, ["action"]));
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
                    "integer" => argument.Value.ValueKind == JsonValueKind.Number && argument.Value.TryGetInt64(out _),
                    "number" => argument.Value.ValueKind == JsonValueKind.Number && argument.Value.TryGetDouble(out var numericValue) && double.IsFinite(numericValue),
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
            using var updateWatchStop = new CancellationTokenSource();
            var updateWatch = WaitForUpdateAsync(updateWatchStop.Token);
            try
            {
            while (true)
            {
                var read = input.ReadLineAsync();
                if (await Task.WhenAny(read, updateWatch).ConfigureAwait(false) == updateWatch) return 0;
                var line = await read.ConfigureAwait(false);
                if (line == null) break;
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
                                if (data.ValueKind == JsonValueKind.Object && data.EnumerateObject().Count() == 1 &&
                                    data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
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
            finally { updateWatchStop.Cancel(); }
        }

        private static async Task WaitForUpdateAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Native.TreeGitUpdateInstaller.HasActiveUpdate(AppContext.BaseDirectory)) return;
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
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
            if (Native.TreeGitUpdateInstaller.HasActiveUpdate(AppContext.BaseDirectory))
                throw new InvalidOperationException("TreeGit is updating. Retry after it restarts.");
            if (!Native.UiStartup.IsComplete(AppContext.BaseDirectory))
                throw new InvalidOperationException(Native.UiStartup.Message(AppContext.BaseDirectory, true));
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
                // A manually opened desktop can still be loading resources. Wait
                // for that process instead of launching a second window with the
                // same settings and automation endpoint.
                if (Mutex.TryOpenExisting(DesktopStartupName, out var starting))
                {
                    using (starting) probe.Connect(20000);
                    return;
                }
                // The desktop must never retain an AI client's standard streams.
                // ShellExecute provides that separation on Windows; Unix needs
                // explicit redirection before exec'ing the fixed executable.
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                if (!OperatingSystem.IsWindows())
                {
                    start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
                    start.ArgumentList.Add("treegit-desktop");
                    start.ArgumentList.Add(Environment.ProcessPath!);
                }
                Native.ProcessLauncher.Start(start);
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
