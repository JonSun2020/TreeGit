using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    internal sealed class CredentialDialogMessage
    {
        public string Action { get; set; }
        public string Repo { get; set; }
        public string Remote { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Prompt { get; set; }
        public string State { get; set; }
        public bool Retry { get; set; }
    }

    [JsonSerializable(typeof(CredentialDialogMessage))]
    internal partial class CredentialDialogJson : JsonSerializerContext { }

    // One lifetime per explicit transport operation. Secrets travel only through
    // a CurrentUserOnly pipe, never command arguments, environment or disk.
    internal sealed class CredentialSession : IDisposable
    {
        internal const string EnvironmentKey = "TREEGIT_CREDENTIAL_SESSION";
        internal const string CancelMarker = "TreeGit credential request cancelled.";
        internal const string RejectedMarker = "TreeGit credential request rejected.";
        internal readonly string Id = Guid.NewGuid().ToString("N");
        private readonly EventWaitHandle _cancel, _timeout, _accepted, _requested;
        private readonly EventWaitHandle _verifying, _rejected;
        private readonly RegisteredWaitHandle _cancelWait, _timeoutWait;
        private readonly RegisteredWaitHandle _verifyingWait, _rejectedWait;
        private readonly object _gate = new();
        private Process _process;
        private readonly CancellationTokenSource _stopLaunch = new();
        private Process _host;
        private volatile bool _timedOut;

        internal CredentialSession(Action<string> stateChanged = null)
        {
            _cancel = Create(Id, "cancel"); _timeout = new(false, EventResetMode.AutoReset, EventName(Id, "timeout"));
            _accepted = Create(Id, "accepted"); _requested = Create(Id, "requested");
            _verifying = new(false, EventResetMode.AutoReset, EventName(Id, "verifying"));
            _rejected = new(false, EventResetMode.AutoReset, EventName(Id, "rejected"));
            _verifyingWait = ThreadPool.RegisterWaitForSingleObject(_verifying, (_, _) => stateChanged?.Invoke("Verifying"), null, Timeout.Infinite, false);
            _rejectedWait = ThreadPool.RegisterWaitForSingleObject(_rejected, (_, _) => stateChanged?.Invoke("AuthenticationFailed"), null, Timeout.Infinite, false);
            _cancelWait = ThreadPool.RegisterWaitForSingleObject(_cancel, (_, _) => StopProcess(), null, Timeout.Infinite, true);
            _timeoutWait = ThreadPool.RegisterWaitForSingleObject(_timeout, (_, _) => { _timedOut = true; StopProcess(); }, null, Timeout.Infinite, false);
            _ = ListenForLaunchAsync();
        }

        private static string EventName(string id, string kind) => "Local\\TreeGit.Credential." + id + "." + kind;
        private static EventWaitHandle Create(string id, string kind) => new(false, EventResetMode.ManualReset, EventName(id, kind));
        internal static string PipeName(string id) => "TreeGit.Credential." + id;
        internal static string CurrentId => Environment.GetEnvironmentVariable(EnvironmentKey);
        internal static bool IsValid(string id) => Guid.TryParseExact(id, "N", out _);
        internal bool WasCancelled => _cancel.WaitOne(0);
        internal bool WasTimedOut => _timedOut;
        internal bool HasPrompt => _requested.WaitOne(0);
        internal bool WasAccepted => _accepted.WaitOne(0);

        internal static void Signal(string id, string kind)
        {
            if (!OperatingSystem.IsWindows() || !IsValid(id)) return;
            try { using var handle = EventWaitHandle.OpenExisting(EventName(id, kind)); handle.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        internal void Configure(ProcessStartInfo info)
        {
            info.Environment[EnvironmentKey] = Id;
            info.Environment["TREEGIT_CREDENTIAL_PARENT"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal void Bind(Process process)
        {
            lock (_gate)
            {
                _process = process;
                if (WasCancelled || WasTimedOut) StopProcess();
            }
        }

        internal void Unbind() { lock (_gate) _process = null; }
        internal void ResetTimeout() { _timedOut = false; _timeout.Reset(); }
        private void StopProcess()
        {
            lock (_gate)
                try { if (_process is { HasExited: false }) OS.TerminateProcess(_process); }
                catch (InvalidOperationException) { }
        }

        internal async Task NotifyAsync(string action)
        {
            if (!HasPrompt) return;
            try { await ExchangeAsync(Id, new() { Action = action }, 1500).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or JsonException) { }
        }

        internal static async Task<CredentialDialogMessage> ExchangeAsync(string id, CredentialDialogMessage message, int connectionTimeout = 10000, bool launch = false)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(id) + (launch ? ".launch" : string.Empty), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(connectionTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, CredentialDialogJson.Default.CredentialDialogMessage)).ConfigureAwait(false);
            // The user may legitimately leave a credential window open. Only
            // connecting is bounded here; verification itself has its own timer.
            var line = message.Action == "get" ? await reader.ReadLineAsync().ConfigureAwait(false) :
                await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return line == null || line.Length > 32768 ? null : JsonSerializer.Deserialize(line, CredentialDialogJson.Default.CredentialDialogMessage);
        }

        internal static CredentialDialogMessage Request(CredentialDialogMessage request)
        {
            var id = CurrentId;
            if (!OperatingSystem.IsWindows() || !IsValid(id)) return null;
            using var launch = new Mutex(false, "Local\\TreeGit.Credential.Launch." + id);
            try { if (!launch.WaitOne(TimeSpan.FromSeconds(30))) return null; }
            catch (AbandonedMutexException) { /* The abandoned launch lock is now owned by this helper. */ }
            try
            {
                // A busy window still has a separate pipe listener for status
                // messages; never create a second dialog for the same operation.
                try { ExchangeAsync(id, new() { Action = "ping" }, 300).GetAwaiter().GetResult(); }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                {
                    // Launch through the desktop process. Starting the lasting
                    // window from a Git helper can inherit Git's output handles
                    // through MSYS, preventing the completed transport from EOF.
                    ExchangeAsync(id, request, 10000, launch: true).GetAwaiter().GetResult();
                }
            }
            finally { launch.ReleaseMutex(); }
            return ExchangeAsync(id, request).GetAwaiter().GetResult();
        }

        private async Task ListenForLaunchAsync()
        {
            try
            {
                while (!_stopLaunch.IsCancellationRequested)
                {
                    using var pipe = new NamedPipeServerStream(PipeName(Id) + ".launch", PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stopLaunch.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(_stopLaunch.Token).ConfigureAwait(false);
                    if (line == null || line.Length > 32768) continue;
                    if (_host == null || _host.HasExited)
                    {
                        _host?.Dispose();
                        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        start.ArgumentList.Add("--credential-session"); start.ArgumentList.Add(Id);
                        Configure(start); start.Environment["SOURCEGIT_LAUNCH_AS_ASKPASS"] = "TRUE";
                        _host = ProcessLauncher.Start(start);
                        await _host.StandardInput.WriteAsync(line).ConfigureAwait(false); _host.StandardInput.Close();
                        _ = _host.StandardOutput.ReadToEndAsync(); _ = _host.StandardError.ReadToEndAsync();
                    }
                    await ExchangeAsync(Id, new() { Action = "ping" }).ConfigureAwait(false);
                    await writer.WriteLineAsync("{\"State\":\"ok\"}").ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        }

        internal static void Publish(string action)
        {
            var id = CurrentId;
            if (!OperatingSystem.IsWindows() || !IsValid(id)) return;
            if (action == "accepted") Signal(id, "accepted");
            if (action == "rejected") Signal(id, "rejected");
            // Stored credentials need no UI process. Avoid waiting for a pipe
            // that was never opened on every already authenticated operation.
            try
            {
                using var requested = EventWaitHandle.OpenExisting(EventName(id, "requested"));
                if (!requested.WaitOne(0)) return;
            }
            catch (WaitHandleCannotBeOpenedException) { return; }
            try { ExchangeAsync(id, new() { Action = action }, 1500).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or JsonException) { }
        }

        public void Dispose()
        {
            Unbind();
            if (HasPrompt)
                try { ExchangeAsync(Id, new() { Action = "finish" }, 1000).GetAwaiter().GetResult(); }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or JsonException) { }
            _cancelWait.Unregister(null); _timeoutWait.Unregister(null);
            _verifyingWait.Unregister(null); _rejectedWait.Unregister(null);
            _stopLaunch.Cancel();
            _host?.Dispose();
            _cancel.Dispose(); _timeout.Dispose(); _accepted.Dispose(); _requested.Dispose();
            _verifying.Dispose(); _rejected.Dispose();
        }
    }
}
