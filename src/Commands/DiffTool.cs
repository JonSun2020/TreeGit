using System;
using System.Threading.Tasks;

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

            if (string.IsNullOrEmpty(tool.Exec))
            {
                if (!await CheckGitConfigurationAsync())
                    return false;

                Args = $"difftool -g --no-prompt {_option}";
            }
            else
            {
                var executable = OperatingSystem.IsWindows() ? tool.Exec.Replace('\\', '/') : tool.Exec;
                var cmd = Native.ExternalMergeSession.ShellQuote(executable) + " " + tool.Cmd;
                Args = $"-c difftool.sourcegit.cmd={cmd.Quoted()} difftool --trust-exit-code --tool=sourcegit --no-prompt {_option}";
            }

            try
            {
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
