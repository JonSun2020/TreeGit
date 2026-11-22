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
            public string UserMessage { get; set; }
            public bool AuthenticationCancelled { get; set; }

            internal bool IsSilentFailure => !IsSuccess && string.IsNullOrEmpty(StdOut) && string.IsNullOrEmpty(StdErr);

            public static Result Failed(string reason, string userMessage = null) => new Result() { StdErr = reason, UserMessage = userMessage };
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
        internal string HttpProxyTarget { get; set; }
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
        internal bool IgnoreOperationCancellation { get; set; }
        public string FailureMessage { get; private set; } = string.Empty;
        public int? LastExitCode { get; private set; }
        protected bool IsAuthenticationRetry { get; set; }
        public bool WasAuthenticationCancelled { get; private set; }
        internal bool WasCredentialAccepted { get; private set; }
        private protected Native.CredentialSession AuthenticationSession;

        private protected async Task<bool> RetryAuthenticationAsync(int exitCode, string error, bool allowed, bool requireTransportExit)
        {
            WasAuthenticationCancelled = AuthenticationSession?.WasCancelled == true ||
                error.Contains(Native.CredentialSession.CancelMarker, StringComparison.Ordinal) ||
                error.Contains("told us to quit", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("User canceled authentication", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("User cancelled authentication", StringComparison.OrdinalIgnoreCase);
            WasCredentialAccepted |= AuthenticationSession?.WasAccepted == true;
#if INTEGRATION_TESTS
            Tests.AskpassProbe.Trace($"transport-exit-{exitCode}-cancel-{WasAuthenticationCancelled}-event-{AuthenticationSession?.WasCancelled}-marker-{error.Contains(Native.CredentialSession.CancelMarker, StringComparison.Ordinal)}-quit-{error.Contains("told us to quit", StringComparison.OrdinalIgnoreCase)}-accepted-{WasCredentialAccepted}");
#endif
            if (WasAuthenticationCancelled) { Log?.MarkAuthenticationCancelled(); return false; }
            if (exitCode == 0)
            {
                if (AuthenticationSession != null) await AuthenticationSession.NotifyAsync("accepted").ConfigureAwait(false);
                if (allowed) Log?.ReportAuthenticationState("Authenticated");
                return false;
            }
            var timedOut = AuthenticationSession?.WasTimedOut == true;
            var rejected = Native.RepositoryCredentials.IsAuthenticationFailure(error);
            var connectionFailure = AuthenticationSession is { HasPrompt: true, WasAccepted: false } &&
                (error.Contains("fatal: unable to access", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase));
            if (!allowed || !OperatingSystem.IsWindows() ||
                !(timedOut || (!requireTransportExit || exitCode == 128) && (rejected || connectionFailure))) return false;
            if (AuthenticationSession != null)
            {
                Log?.ReportAuthenticationState(timedOut ? "VerificationTimeout" : rejected ? "AuthenticationFailed" : "ConnectionFailed");
                await AuthenticationSession.NotifyAsync(timedOut ? "timeout" : rejected ? "rejected" : "error").ConfigureAwait(false);
                AuthenticationSession.ResetTimeout();
            }
            IsAuthenticationRetry = true;
            return true;
        }
        // Local hooks can emit authentication errors too. Only replay a transport
        // operation; never repeat a rejected local mutation automatically.
        protected virtual bool SupportsAuthenticationRetry => false;
        protected virtual bool RequiresAuthenticationTransportExitCode => false;
        protected virtual bool RequiresInvariantOutput => false;

        public async Task<bool> ExecAsync()
        {
            if (!IgnoreOperationCancellation && Log?.OperationCancellation.IsCancellationRequested == true)
            {
                FailureMessage = "Operation cancelled.";
                return false;
            }
            IsAuthenticationRetry = false;
            WasAuthenticationCancelled = false;
            var originalCancellation = CancellationToken;
            using var operationCancellation = SupportsAuthenticationRetry && Log?.OperationCancellation.CanBeCanceled == true ?
                CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, Log.OperationCancellation) : null;
            if (operationCancellation != null) CancellationToken = operationCancellation.Token;
            using var session = OperatingSystem.IsWindows() && SupportsAuthenticationRetry ? new Native.CredentialSession(state => Log?.ReportAuthenticationState(state)) : null;
            AuthenticationSession = session;
            try { return await ExecWithAuthenticationAsync(session).ConfigureAwait(false); }
            finally { AuthenticationSession = null; CancellationToken = originalCancellation; }
        }

        private async Task<bool> ExecWithAuthenticationAsync(Native.CredentialSession session)
        {
            while (true)
            {
                FailureMessage = string.Empty;
                LastExitCode = null;
                if (CancellationToken.IsCancellationRequested) { FailureMessage = "Operation cancelled."; return false; }
                Log?.AppendLine($"$ git {Args}\n");

                var errs = new Models.BoundedTextBuffer(65536);

                using var proc = new Process();
                proc.StartInfo = await CreateGitStartInfoAsync().ConfigureAwait(false);
                if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(proc.StartInfo.FileName))
                {
                    FailureMessage = App.Text("TreeGit.Git.Unavailable");
                    if (RaiseError || Log?.HasOperationPanel == true) App.NotifyGitUnavailable();
                    return false;
                }
                if (CancellationToken.IsCancellationRequested) { FailureMessage = "Operation cancelled."; return false; }
                proc.OutputDataReceived += (_, e) => HandleOutput(e.Data, errs);
                proc.ErrorDataReceived += (_, e) => HandleOutput(e.Data, errs);

                var captured = new CapturedProcess() { Process = proc };
                var capturedLock = new object();
                try
                {
                    Native.ProcessLauncher.Start(proc);
                    session?.Bind(proc);

                }
                catch (Exception e)
                {
                    FailureMessage = e.Message;
                    if (RaiseError)
                        RaiseException(e.Message);

                    Log?.AppendLine(string.Empty);
                    return false;
                }

                // Only readonly operations may opt in to process cancellation.
                // Dispose the registration before its Process is disposed.
                using var registration = CancellationToken.Register(() =>
                {
                    lock (capturedLock)
                    {
                        if (captured is { Process: { HasExited: false } }) Native.OS.TerminateProcess(captured.Process);
                    }
                });

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
                session?.Unbind();

                Log?.AppendLine(string.Empty);

                if (CancellationToken.IsCancellationRequested)
                {
                    FailureMessage = "Operation cancelled.";
                    return false;
                }
                LastExitCode = proc.ExitCode;
                string authenticationError;
                lock (errs) authenticationError = errs.ToString().Trim();
                if (await RetryAuthenticationAsync(proc.ExitCode, authenticationError, SupportsAuthenticationRetry, RequiresAuthenticationTransportExitCode).ConfigureAwait(false)) continue;
                if (WasAuthenticationCancelled) { FailureMessage = string.Empty; return false; }
                if (proc.ExitCode != 0)
                {
                    string errMsg;
                    lock (errs) errMsg = errs.ToString().Trim();
                    FailureMessage = errMsg[..Math.Min(errMsg.Length, 65536)];
                    // Git rejects/erases an invalid HTTP credential before returning this
                    // error. Keep requesting corrected credentials until success or cancellation.
                    if (RaiseError)
                    {
                        if (!string.IsNullOrEmpty(errMsg))
                        {
                            Log?.MarkFailed(errMsg);
                            if (Log?.HasOperationPanel != true) RaiseException(errMsg);
                        }
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
            if (HttpProxyTarget != null)
                Native.GitSystemProxy.Configure(proc.StartInfo, HttpProxyTarget);

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

        protected async Task<Result> ReadToEndAsync(bool retrySilentFailure = false, Encoding outputEncoding = null)
        {
            var originalCancellation = CancellationToken;
            using var operationCancellation = !IgnoreOperationCancellation && SupportsAuthenticationRetry && Log?.OperationCancellation.CanBeCanceled == true ?
                CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, Log.OperationCancellation) : null;
            if (operationCancellation != null) CancellationToken = operationCancellation.Token;
            using var session = OperatingSystem.IsWindows() && SupportsAuthenticationRetry ? new Native.CredentialSession(state => Log?.ReportAuthenticationState(state)) : null;
            AuthenticationSession = session;
            IsAuthenticationRetry = false;
            WasAuthenticationCancelled = false;
            try
            {
                if (session == null)
                    return await ReadToEndAttemptAsync(retrySilentFailure, outputEncoding).ConfigureAwait(false);
                while (true)
                {
                    var result = await ReadToEndAttemptAsync(retrySilentFailure, outputEncoding).ConfigureAwait(false);
                    if (await RetryAuthenticationAsync(result.ExitCode ?? -1, result.StdErr, true, RequiresAuthenticationTransportExitCode).ConfigureAwait(false)) continue;
                    result.AuthenticationCancelled = WasAuthenticationCancelled;
                    if (WasAuthenticationCancelled) { result.IsSuccess = false; result.StdErr = string.Empty; }
                    return result;
                }
            }
            finally { session?.Unbind(); AuthenticationSession = null; CancellationToken = originalCancellation; }
        }

        private async Task<Result> ReadToEndAttemptAsync(bool retrySilentFailure, Encoding outputEncoding)
        {
            CancellationToken.ThrowIfCancellationRequested();
            using var proc = new Process();
            proc.StartInfo = await CreateGitStartInfoAsync().ConfigureAwait(false);
            CancellationToken.ThrowIfCancellationRequested();
            if (outputEncoding != null) proc.StartInfo.StandardOutputEncoding = outputEncoding;

            try
            {
                Native.ProcessLauncher.Start(proc);
                AuthenticationSession?.Bind(proc);
            }
            catch (Exception e)
            {
                return Result.Failed(e.Message);
            }

            using var cancellation = CancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) Native.OS.TerminateProcess(proc); }
                catch (InvalidOperationException) { /* The owned process already exited. */ }
            });
            var rs = new Result() { IsSuccess = true };
            var output = proc.StandardOutput.ReadToEndAsync(CancellationToken);
            var error = proc.StandardError.ReadToEndAsync(CancellationToken);
            var exited = proc.WaitForExitAsync(CancellationToken);
            await Task.WhenAll(output, error, exited).ConfigureAwait(false);
            AuthenticationSession?.Unbind();
            rs.StdOut = await output.ConfigureAwait(false);
            rs.StdErr = await error.ConfigureAwait(false);

            rs.IsSuccess = proc.ExitCode == 0;
            rs.ExitCode = proc.ExitCode;
            if (retrySilentFailure && rs.IsSilentFailure)
            {
                await Task.Delay(100, CancellationToken).ConfigureAwait(false);
                return await ReadToEndAttemptAsync(false, outputEncoding).ConfigureAwait(false);
            }
            return rs;
        }

        protected async Task<ProcessStartInfo> CreateGitStartInfoAsync()
        {
            var start = CreateGitStartInfo(true);
            if (OperatingSystem.IsWindows() && Native.GitSystemProxy.IsHttpUrl(HttpProxyTarget))
            {
                // PAC/WPAD lookup can take time. Keep it off the dispatch thread.
                await Task.Run(() => Native.GitSystemProxy.Configure(start, HttpProxyTarget)).ConfigureAwait(false);
            }
            return start;
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

            // Background reads (including diff/status inside submodules) must
            // not refresh an index under a foreground Git operation. Required
            // locks for explicit writes are unaffected by this setting.
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

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
            AuthenticationSession?.Configure(start);
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

            // Commands that parse human-readable fields need stable tokens on every platform.
            if (!OperatingSystem.IsWindows() || Log?.HasOperationPanel == true || RequiresInvariantOutput)
            {
                start.Environment["LANG"] = "C";
                start.Environment["LC_ALL"] = "C";
            }
            if (Log?.HasOperationPanel == true && redirect) start.Environment["GIT_LFS_FORCE_PROGRESS"] = "1";

            // Working directory
            if (!string.IsNullOrEmpty(WorkingDirectory))
                start.WorkingDirectory = WorkingDirectory;

            return start;
        }

        protected void RaiseException(string error)
        {
            Models.Notification.Send(Context, error, true);
        }

        private void HandleOutput(string line, Models.BoundedTextBuffer errs)
        {
            if (line == null)
                return;
            line = Native.RepositoryCredentials.RedactLog(line);

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

            lock (errs) errs.AppendLine(line);
        }

        private class CapturedProcess
        {
            public Process Process { get; set; } = null;
        }

        [GeneratedRegex(@"\d+%")]
        private static partial Regex REG_PROGRESS();
    }
}
