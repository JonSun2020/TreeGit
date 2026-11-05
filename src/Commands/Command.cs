using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Command
    {
        public class Result
        {
            public bool IsSuccess { get; set; } = false;
            public string StdOut { get; set; } = string.Empty;
            public string StdErr { get; set; } = string.Empty;
            public int? ExitCode { get; set; }

            internal bool IsSilentFailure => !IsSuccess && string.IsNullOrEmpty(StdOut) && string.IsNullOrEmpty(StdErr);

            public static Result Failed(string reason) => new Result() { StdErr = reason };
        }

        public enum EditorType
        {
            None,
            CoreEditor,
            RebaseEditor,
        }

        public string Context { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = null;
        public string CredentialScope { get; set; } = null;
        public bool? UseCredentialManager { get; set; } = null;
        public EditorType Editor { get; set; } = EditorType.CoreEditor;
        public string SSHKey { get; set; } = string.Empty;
        internal string EditorMessageFile { get; set; }
        internal string EditorMessageGitDirectory { get; set; }
        internal bool PreserveSubmoduleArguments { get; set; }
        public string Args { get; set; } = string.Empty;

        // Only used in `ExecAsync` mode.
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool RaiseError { get; set; } = true;
        public Models.ICommandLog Log { get; set; } = null;
        protected bool IsAuthenticationRetry { get; set; }
        // Local hooks can emit authentication errors too. Only replay a transport
        // operation; never repeat a rejected local mutation automatically.
        protected virtual bool SupportsAuthenticationRetry => false;
        protected virtual bool RequiresAuthenticationTransportExitCode => false;

        public async Task<bool> ExecAsync()
        {
            IsAuthenticationRetry = false;
            while (true)
            {
                Log?.AppendLine($"$ git {Args}\n");

                var errs = new List<string>();

                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
                proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

                var captured = new CapturedProcess() { Process = proc };
                var capturedLock = new object();
                try
                {
                    Native.ProcessLauncher.Start(proc);

                    // Not safe, please only use `CancellationToken` in readonly commands.
                    if (CancellationToken.CanBeCanceled)
                    {
                        CancellationToken.Register(() =>
                        {
                            lock (capturedLock)
                            {
                                if (captured is { Process: { HasExited: false } })
                                    Native.OS.TerminateProcess(captured.Process);
                            }
                        });
                    }
                }
                catch (Exception e)
                {
                    if (RaiseError)
                        RaiseException(e.Message);

                    Log?.AppendLine(string.Empty);
                    return false;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                try
                {
                    await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    HandleOutput(e.Message, errs);
                }

                lock (capturedLock)
                {
                    captured.Process = null;
                }

                Log?.AppendLine(string.Empty);

                if (!CancellationToken.IsCancellationRequested && proc.ExitCode != 0)
                {
                    var errMsg = string.Join("\n", errs).Trim();
                    // Git rejects/erases an invalid HTTP credential before returning this
                    // error. Keep requesting corrected credentials until success or cancellation.
                    if (SupportsAuthenticationRetry && (!RequiresAuthenticationTransportExitCode || proc.ExitCode == 128) &&
                        OperatingSystem.IsWindows() && Native.RepositoryCredentials.IsAuthenticationFailure(errMsg))
                    {
                        Log?.AppendLine("Authentication was rejected; requesting updated credentials.");
                        IsAuthenticationRetry = true;
                        continue;
                    }
                    if (RaiseError)
                    {
                        if (!string.IsNullOrEmpty(errMsg))
                            RaiseException(errMsg);
                    }

                    return false;
                }

                return true;
            }
        }

        // Retry is opt-in for read-only discovery. Mutations must never use it.
        protected Result ReadToEnd(bool retrySilentFailure = false)
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                Native.ProcessLauncher.Start(proc);
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            var output = proc.StandardOutput.ReadToEndAsync();
            var error = proc.StandardError.ReadToEndAsync();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            rs.StdOut = output.GetAwaiter().GetResult();
            rs.StdErr = error.GetAwaiter().GetResult();
            proc.WaitForExit();

            rs.IsSuccess = proc.ExitCode == 0;
            rs.ExitCode = proc.ExitCode;
            if (retrySilentFailure && rs.IsSilentFailure)
            {
                System.Threading.Thread.Sleep(100);
                return ReadToEnd();
            }
            return rs;
        }

        protected async Task<Result> ReadToEndAsync(bool retrySilentFailure = false)
        {
            using var proc = new Process();
            proc.StartInfo = CreateGitStartInfo(true);

            try
            {
                Native.ProcessLauncher.Start(proc);
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            var rs = new Result() { IsSuccess = true };
            var output = proc.StandardOutput.ReadToEndAsync(CancellationToken);
            var error = proc.StandardError.ReadToEndAsync(CancellationToken);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            rs.StdOut = output.GetAwaiter().GetResult();
            rs.StdErr = error.GetAwaiter().GetResult();
            await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);

            rs.IsSuccess = proc.ExitCode == 0;
            rs.ExitCode = proc.ExitCode;
            if (retrySilentFailure && rs.IsSilentFailure)
            {
                await Task.Delay(100, CancellationToken).ConfigureAwait(false);
                return await ReadToEndAsync().ConfigureAwait(false);
            }
            return rs;
        }

        protected ProcessStartInfo CreateGitStartInfo(bool redirect)
        {
            var useSetSid = CancellationToken.CanBeCanceled && Native.OS.SupportSetSid();
            var selfExecFile = Environment.ProcessPath;
            var builder = new StringBuilder(2048);

            if (useSetSid)
                builder.Append(Native.OS.GitExecutable.Quoted()).Append(' ');

            builder.Append("--no-pager -c core.quotepath=off ");
            if (OperatingSystem.IsWindows())
            {
                builder.Append("-c credential.helper= -c credential.useHttpPath=true -c http.sslBackend=openssl ");
                if (UseCredentialManager ?? Native.RepositoryCredentials.UsesCredentialManager(CredentialScope ?? WorkingDirectory))
                    builder.Append("-c ").Append(("credential.helper=" + Native.OS.CredentialHelper).Quoted()).Append(' ');
                else
                {
                    var helper = "!'" + selfExecFile.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "' --credential-helper";
                    builder.Append("-c ").Append(("credential.helper=" + helper).Quoted()).Append(' ');
                }
            }
            else
                builder.Append("-c credential.helper=").Append(Native.OS.CredentialHelper).Append(' ');

            switch (Editor)
            {
                case EditorType.CoreEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --core-editor" """);
                    break;
                case EditorType.RebaseEditor:
                    builder.Append($"""-c core.editor="\"{selfExecFile}\" --rebase-message-editor" -c sequence.editor="\"{selfExecFile}\" --rebase-todo-editor" -c rebase.abbreviateCommands=true """);
                    break;
                default:
                    builder.Append("-c core.editor=true ");
                    break;
            }

            builder.Append(Args);

            var start = new ProcessStartInfo();
            start.FileName = useSetSid ? Native.OS.GetSetSidExecutable() : Native.OS.GitExecutable;
            start.Arguments = builder.ToString();
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            if (OperatingSystem.IsWindows() && PreserveSubmoduleArguments)
            {
                // MSYS expands wildcard-looking arguments when Git launches its
                // shell helpers. Keep the supplied argv intact; Git itself still
                // interprets intentional pathspecs in commands that support them.
                start.Environment.TryGetValue("MSYS", out var msys);
                start.Environment["MSYS"] = string.IsNullOrWhiteSpace(msys) ? "noglob" : msys + " noglob";
            }

            if (redirect)
            {
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.UTF8;
            }

            // Force using this app as SSH askpass program
            start.Environment["SSH_ASKPASS"] = selfExecFile; // Raw executable path, including spaces, is required by exec.
            start.Environment["SSH_ASKPASS_REQUIRE"] = "prefer";
            start.Environment["SOURCEGIT_LAUNCH_AS_ASKPASS"] = "TRUE";
            start.Environment["SOURCEGIT_AUTHENTICATION_RETRY"] = IsAuthenticationRetry ? "TRUE" : "FALSE";
            start.Environment["GIT_ASKPASS"] = selfExecFile;
            if (!string.IsNullOrEmpty(EditorMessageFile))
            {
                start.Environment["TREEGIT_REBASE_MESSAGE_FILE"] = EditorMessageFile;
                start.Environment["TREEGIT_REBASE_MESSAGE_GIT_DIR"] = EditorMessageGitDirectory;
            }
            start.Environment["SOURCEGIT_CREDENTIAL_SCOPE"] = CredentialScope ?? WorkingDirectory ?? Context;
            if (!OperatingSystem.IsLinux())
                start.Environment["DISPLAY"] = "required";

            var runtimeSSH = OperatingSystem.IsWindows() ? Native.GitExecutableLocator.ConfigureProcess(start, Native.OS.GitExecutable) : null;
            if (runtimeSSH != null)
            {
                if (!start.Environment.ContainsKey("GIT_SSH_COMMAND"))
                {
                    var ssh = "'" + runtimeSSH.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
                    if (!string.IsNullOrEmpty(SSHKey))
                        ssh += " -i '" + SSHKey.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "' -o AddKeysToAgent=yes";
                    start.Environment["GIT_SSH_COMMAND"] = ssh;
                }
            }

            // If an SSH private key was provided, sets the environment.
            if (!start.Environment.ContainsKey("GIT_SSH_COMMAND") && !string.IsNullOrEmpty(SSHKey))
            {
                var key = SSHKey.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
                start.Environment.Add("GIT_SSH_COMMAND", $"ssh -i '{key}' -o AddKeysToAgent=yes");
            }

            // Force the C locale on Unix, so that git's output (including fatal errors) can always be parsed.
            if (!OperatingSystem.IsWindows())
            {
                start.Environment.Add("LANG", "C");
                start.Environment.Add("LC_ALL", "C");
            }

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private void HandleOutput(string line, List<string> errs)
        {
            if (line == null)
                return;

            Log?.AppendLine(line);

            // Lines to hide in error message.
            if (line.Length > 0)
            {
                if (line.StartsWith("remote: Enumerating objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Counting objects:", StringComparison.Ordinal) ||
                    line.StartsWith("remote: Compressing objects:", StringComparison.Ordinal) ||
                    line.StartsWith("Filtering content:", StringComparison.Ordinal) ||
                    line.StartsWith("hint:", StringComparison.Ordinal))
                    return;

                if (REG_PROGRESS().IsMatch(line))
                    return;
            }

            errs.Add(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
