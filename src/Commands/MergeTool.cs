using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class MergeTool : Command
    {
        public MergeTool(string repo, string file)
        {
            WorkingDirectory = repo;
            Context = repo;
            // git-mergetool also passes this name through shell utilities such
            // as dirname. A leading '-' needs a path prefix there even after
            // Git's own '--', or its temporary BASE/LOCAL/REMOTE can be empty.
            _file = string.IsNullOrEmpty(file) ? string.Empty :
                (Path.IsPathRooted(file) ? file : "./" + file).Quoted();
            _filePath = file;
        }

        public async Task<bool> OpenAsync()
        {
            if (Native.OS.ExternalMergerType == 0)
                return await App.OpenInternalMergeAsync(WorkingDirectory, _filePath);
            var tool = Native.OS.GetDiffMergeTool(false);
            if (tool == null)
            {
                RaiseException(App.Text("TreeGit.Tool.NotConfigured"));
                return false;
            }

            string session = null;
            var gitExecutable = Native.OS.GitExecutable;
            try
            {
                if (string.IsNullOrEmpty(tool.Exec))
                {
                    var ok = await CheckGitConfigurationAsync();
                    if (!ok)
                        return false;

                    Args = $"mergetool -g --no-prompt -- {_file}";
                }
                else
                {
                    session = Path.Combine(Path.GetTempPath(), "TreeGit-external-merge-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(session);
                    var plan = Path.Combine(session, "plan.json");
                    File.WriteAllText(plan, JsonSerializer.Serialize(new Native.ExternalMergePlan
                    {
                        Executable = tool.Exec, Arguments = tool.Cmd, GitExecutable = gitExecutable,
                        UnsavedMessage = App.Text("TreeGit.Tool.MergeNotSaved"),
                    }, JsonCodeGen.Default.ExternalMergePlan));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(plan, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var executable = Environment.ProcessPath;
                    if (OperatingSystem.IsWindows()) { executable = executable.Replace('\\', '/'); plan = plan.Replace('\\', '/'); }
                    var cmd = Native.ExternalMergeSession.ShellQuote(executable) + " --external-merge-tool " +
                        Native.ExternalMergeSession.ShellQuote(plan) + " \"$BASE\" \"$LOCAL\" \"$REMOTE\" \"$MERGED\"";
                    Args = $"-c mergetool.sourcegit.cmd={cmd.Quoted()} -c mergetool.writeToTemp=true -c mergetool.keepBackup=false -c mergetool.sourcegit.trustExitCode=true mergetool --tool=sourcegit --no-prompt -- {_file}";
                }

                return await ExecAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                RaiseException(App.Text("TreeGit.Tool.CannotOpen"));
                Native.OS.LogException(error);
                return false;
            }
            finally
            {
                if (session != null)
                {
                    try
                    {
                        var recovery = await Task.Run(() => Native.ExternalMergeSession.RestoreFailedDrafts(session, WorkingDirectory, gitExecutable)).ConfigureAwait(false);
                        if (recovery.Restored > 0)
                            Models.Notification.Send(Context, App.Text("TreeGit.Tool.MergeDraftPreserved"));
                        foreach (var draft in recovery.RecoveryFiles)
                            RaiseException(App.Text("TreeGit.Tool.MergeDraftSaved", draft.File, draft.Recovery));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Native.OS.LogException(error);
                        RaiseException(App.Text("TreeGit.Tool.MergeDraftSaved", WorkingDirectory, session));
                    }
                    try
                    {
                        File.Delete(Path.Combine(session, "plan.json"));
                        // A retained record maps a draft to its original file
                        // when automatic restoration was deliberately refused.
                        if (Directory.GetFileSystemEntries(session).Length == 0) Directory.Delete(session);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Native.OS.LogException(error); }
                }
            }
        }

        private async Task<bool> CheckGitConfigurationAsync()
        {
            var tool = await new Config(WorkingDirectory).GetAsync("merge.guitool");
            if (string.IsNullOrEmpty(tool))
                tool = await new Config(WorkingDirectory).GetAsync("merge.tool");

            if (string.IsNullOrEmpty(tool))
            {
                RaiseException(App.Text("TreeGit.Tool.GitMergeMissing"));
                return false;
            }

            if (tool.StartsWith("vimdiff", StringComparison.Ordinal) ||
                tool.StartsWith("nvimdiff", StringComparison.Ordinal))
            {
                RaiseException(App.Text("TreeGit.Tool.ConsoleUnsupported", tool));
                return false;
            }

            return true;
        }

        private string _file;
        private readonly string _filePath;
    }
}
