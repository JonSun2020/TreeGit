using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SourceGit.Views
{
    // The credential helper may exit after receiving a password; this separate
    // UI lifetime stays alive to receive the real Git authentication outcome.
    internal sealed class CredentialSessionHost
    {
        private readonly string _id;
        private readonly IClassicDesktopStyleApplicationLifetime _desktop;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _requests = new(1, 1);
        private readonly DispatcherTimer _parentWatch = new() { Interval = TimeSpan.FromSeconds(2) };
        private TaskCompletionSource<Native.CredentialDialogMessage> _reply;
        private Askpass _window;
        private bool _finishing;
        private bool _started;
        private bool _firstGet;

        internal CredentialSessionHost(IClassicDesktopStyleApplicationLifetime desktop, string id, Native.CredentialDialogMessage first)
        {
            _desktop = desktop; _id = id;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _reply = NewReply();
            Show(first); _started = true;
            _parentWatch.Tick += (_, _) =>
            {
                if (!int.TryParse(Environment.GetEnvironmentVariable("TREEGIT_CREDENTIAL_PARENT"), out var pid)) return;
                try { using var parent = Process.GetProcessById(pid); if (parent.HasExited) Finish(); }
                catch (ArgumentException) { Finish(); }
            };
            _parentWatch.Start();
            _ = ListenAsync();
        }

        private void Show(Native.CredentialDialogMessage request)
        {
            _window = new Askpass();
            _firstGet = true;
            _window.ConfigureSession(request, Submit, Cancel, TimedOut);
            var shown = _window;
            shown.Closed += (_, _) => { if (_window == shown) _window = null; };
            _desktop.MainWindow = _window;
            // The desktop lifetime shows its initial MainWindow. Subsequent
            // challenges create another instance only after a verified success.
            if (_started) _window.Show();
            Native.CredentialSession.Signal(_id, "requested");
        }

        private void Submit(string username, string password)
        {
            Native.CredentialSession.Signal(_id, "verifying");
            _reply ??= NewReply();
            _reply?.TrySetResult(new() { State = "submitted", Username = username, Password = password });
        }

        private static TaskCompletionSource<Native.CredentialDialogMessage> NewReply() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Cancel()
        {
            if (_finishing) return;
            Native.CredentialSession.Signal(_id, "cancel");
            _reply?.TrySetResult(new() { State = "cancelled" });
            Finish();
        }

        private void TimedOut() => Native.CredentialSession.Signal(_id, "timeout");

        private async Task ListenAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var pipe = new NamedPipeServerStream(Native.CredentialSession.PipeName(_id), PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
                    catch { pipe.Dispose(); throw; }
                    _ = HandleAsync(pipe);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Dispatcher.UIThread.InvokeAsync(Finish); }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
            await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                try
                {
                    var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line == null || line.Length > 32768) return;
                    var request = JsonSerializer.Deserialize(line, Native.CredentialDialogJson.Default.CredentialDialogMessage);
#if INTEGRATION_TESTS
                    Tests.AskpassProbe.Trace("request-" + request.Action);
#endif
                    Native.CredentialDialogMessage result = new() { State = "ok" };
                    if (request.Action == "get")
                    {
                        await _requests.WaitAsync(_stop.Token).ConfigureAwait(false);
                        try
                        {
                            TaskCompletionSource<Native.CredentialDialogMessage> pending = null;
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                pending = _reply ??= NewReply();
                                if (_window?.SessionSucceeded == true) { _window.CloseSession(); _window = null; }
                                if (_window == null) Show(request);
                                else if (!_firstGet && !pending.Task.IsCompleted) _window.PrepareSessionRequest(request);
                                _firstGet = false;
                            });
                            result = await pending.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
#if INTEGRATION_TESTS
                            Tests.AskpassProbe.Trace("resolved-" + result.State);
#endif
                            await Dispatcher.UIThread.InvokeAsync(() => { if (_reply == pending) _reply = null; });
                        }
                        finally { _requests.Release(); }
                    }
                    else if (request.Action is "accepted" or "rejected" or "timeout" or "error")
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (request.Action == "accepted") _window?.SessionAccepted();
                            else
                            {
                                if (_reply?.Task.IsCompleted == true) _reply = NewReply();
                                _window?.SessionFailed(request.Action == "timeout" ? "TreeGit.Credentials.Timeout" :
                                    request.Action == "error" ? "TreeGit.Credentials.ConnectionFailed" : "TreeGit.Credentials.Retry");
                            }
                        });
                    }
                    else if (request.Action == "finish")
                        Dispatcher.UIThread.Post(async () => { await Task.Delay(650); Finish(); });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result, Native.CredentialDialogJson.Default.CredentialDialogMessage)).ConfigureAwait(false);
#if INTEGRATION_TESTS
                    Tests.AskpassProbe.Trace("reply-" + request.Action + "-" + result.State);
#endif
                }
                catch (Exception error)
                {
                    if (!_finishing && error is not OperationCanceledException) Native.OS.LogException(error);
#if INTEGRATION_TESTS
                    Tests.AskpassProbe.Trace("host-error-" + error.GetType().Name + "-" + error.Message);
#endif
                }
        }

        private void Finish()
        {
            if (_finishing) return;
            _finishing = true;
            _parentWatch.Stop();
            _reply?.TrySetResult(new() { State = "cancelled" });
            _stop.Cancel();
            _window?.CloseSession();
            App.Quit(0);
        }
    }
}
