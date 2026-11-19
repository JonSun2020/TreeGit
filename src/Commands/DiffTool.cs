using System;
using System.Threading.Tasks;
using System.IO;

namespace SourceGit.Commands
{
    public class DiffTool : Command
    {
        public DiffTool(string repo, Models.DiffOption option)
        {
            WorkingDirectory = repo;
            Context = repo;
            _option = option;
        }

        public void Open() => _ = OpenAsync();

        public async Task<bool> OpenAsync()
        {
            if (Native.OS.ExternalMergerType == 0)
            {
                App.OpenInternalComparison(WorkingDirectory, _option);
                return true;
            }
            var tool = Native.OS.GetDiffMergeTool(true);
            if (tool == null)
            {
                RaiseException(App.Text("TreeGit.Tool.NotConfigured"));
                return false;
            }

            string emptyFile = null;
            try
            {
                if (string.IsNullOrEmpty(tool.Exec))
                {
                    if (!await CheckGitConfigurationAsync()) return false;
                    Args = $"difftool -g --no-prompt {_option}";
                }
                else
                {
                    var executable = OperatingSystem.IsWindows() ? tool.Exec.Replace('\\', '/') : tool.Exec;
                    var cmd = Native.ExternalMergeSession.ShellQuote(executable) + " " + tool.Cmd;
                    if (OperatingSystem.IsWindows())
                    {
                        // Git uses /dev/null for an absent revision. Native
                        // Windows tools need a real empty file instead.
                        emptyFile = Path.Combine(Path.GetTempPath(), "TreeGit-external-diff-" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.WriteAllBytes(emptyFile, []);
                        var nativeEmpty = Native.ExternalMergeSession.ShellQuote(emptyFile.Replace('\\', '/'));
                        cmd = "treegit_native_path() { if test \"$1\" = /dev/null; then printf '%s' " + nativeEmpty +
                            "; elif test -n \"$1\"; then (cd -- \"$(dirname -- \"$1\")\" && printf '%s/%s' \"$(pwd -W)\" \"$(basename -- \"$1\")\"); fi; }; " +
                            "LOCAL=\"$(treegit_native_path \"$LOCAL\")\"; REMOTE=\"$(treegit_native_path \"$REMOTE\")\"; MSYS2_ARG_CONV_EXCL='*' " + cmd;
                    }
                    var selected = Native.OS.ExternalMergerType;
                    var icon = selected >= 0 && selected < Models.ExternalMerger.Supported.Count ? Models.ExternalMerger.Supported[selected].Icon : null;
                    // KDiff3 returns 1 when a comparison closes without saving
                    // a merge output. A read-only comparison does not require
                    // saving; merge sessions keep their separate success checks.
                    var successfulCodes = icon switch { "beyond_compare" => "0|1|2|11|12|13|14", "win_merge" or "kdiff3" => "0|1", _ => null };
                    if (successfulCodes != null)
                        cmd += "; result=$?; case $result in " + successfulCodes + ") exit 0 ;; *) exit $result ;; esac";
                    Args = $"-c difftool.sourcegit.cmd={cmd.Quoted()} difftool --trust-exit-code --tool=sourcegit --no-prompt {_option}";
                }

                // Observe Git and the adapter's exit status. A detached process
                // used to hide missing-executable and invalid-argument errors.
                return await ExecAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Native.OS.LogException(ex);
                RaiseException(App.Text("TreeGit.Tool.CannotOpen"));
                return false;
            }
            finally
            {
                if (emptyFile != null)
                    try { File.Delete(emptyFile); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Native.OS.LogException(error); }
            }
        }

        private async Task<bool> CheckGitConfigurationAsync()
        {
            var command = new Config(WorkingDirectory);
            var config = await command.ReadAllAsync().ConfigureAwait(false);
            if (!command.ReadSucceeded) return false;
            if (config.TryGetValue("diff.guitool", out var guiTool))
                return CheckCLIBasedTool(guiTool);
            if (config.TryGetValue("merge.guitool", out var mergeGuiTool))
                return CheckCLIBasedTool(mergeGuiTool);
            if (config.TryGetValue("diff.tool", out var diffTool))
                return CheckCLIBasedTool(diffTool);
            if (config.TryGetValue("merge.tool", out var mergeTool))
                return CheckCLIBasedTool(mergeTool);

            RaiseException(App.Text("TreeGit.Tool.GitDiffMissing"));
            return false;
        }

        private bool CheckCLIBasedTool(string tool)
        {
            if (tool.StartsWith("vimdiff", StringComparison.Ordinal) ||
                tool.StartsWith("nvimdiff", StringComparison.Ordinal))
            {
                RaiseException(App.Text("TreeGit.Tool.ConsoleUnsupported", tool));
                return false;
            }

            return true;
        }

        private Models.DiffOption _option;
    }
}
