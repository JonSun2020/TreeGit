using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SelfUpdate : ObservableObject
    {
        public SelfUpdate() : this(AppContext.BaseDirectory, Models.TreeGitRelease.Version, null) { }

        internal SelfUpdate(string root, string currentVersion, Func<Native.TreeGitUpdater> factory)
        {
            _root = root;
            _currentVersion = currentVersion;
            _factory = factory ?? (() => new Native.TreeGitUpdater(root));
        }
        public object Data
        {
            get => _release;
            set
            {
                if (value is Models.Version release) SetRelease(release);
                else if (value is Models.AlreadyUpToDate) SetState("current");
                else if (value is Models.SelfUpdateFailed) Fail("network");
            }
        }

        public string State => _state;
        public double DesiredHeight => ShowNotes ? 400 : _state == "failed" ? 320 : 260;
        public string Heading => App.Text("SelfUpdate.Flow." + _state);
        public string Message => _message;
        public string CurrentVersion => App.Text("SelfUpdate.Flow.CurrentVersion", _currentVersion);
        public string NewVersion => _newAvailable ? _release.TagName.Replace("treegit-v", "TreeGit ") : string.Empty;
        public string Notes => _release?.Body is { Length: > 20000 } body ? body[..20000] : _release?.Body ?? string.Empty;
        public bool ShowNotes => !string.IsNullOrWhiteSpace(Notes) && _state is not ("checking" or "current");
        public bool IsWorking => _state is "checking" or "download" or "verify" or "applying";
        public bool IsApplying => _state == "applying";
        public bool CanDismiss => !IsApplying;
        public bool CanAct => _state is "available" or "ready" or "failed" or "cancelled";
        public bool CanRestart => _plan != null && (_state == "ready" || (_state == "failed" && !_retryDownload));
        public bool CanDownload => _newAvailable && (_state is "available" or "cancelled" || (_state == "failed" && _retryDownload));
        public bool ShowManualDownload => _state == "failed";
        public bool IsIndeterminate => _state is "checking" or "verify" or "applying";
        public bool ShowProgress => IsWorking;
        public double Progress => _progress;
        public string ProgressText => _progressText;
        public string ActionText => App.Text(_state == "ready" || (_state == "failed" && _plan != null && !_retryDownload) ? "SelfUpdate.Flow.Restart" : _state == "available" ? "SelfUpdate.Flow.Download" : _state == "cancelled" || _retryDownload ? "SelfUpdate.Flow.Resume" : "SelfUpdate.Flow.Retry");
        public string DismissText => App.Text(_state is "download" or "verify" ? "SelfUpdate.Flow.Cancel" : _state == "current" ? "Close" : "SelfUpdate.Flow.Later");
        public bool Restarting { get; private set; }
        public event Action CloseRequested;
        public event Action RestartRequested;

        public async Task CheckAsync()
        {
            if (_taskActive) return;
            _taskActive = true;
            var cancellation = ResetCancellation();
            _retryDownload = false;
            _plan = null;
            SetState("checking");
            try
            {
                using var updater = _factory();
                var release = await updater.CheckAsync(cancellation.Token);
                if (!_closed)
                {
                    SetRelease(release);
                    if (_newAvailable)
                    {
                        var prepared = await Task.Run(() => updater.FindPrepared(release));
                        if (!_closed && prepared != null) { _plan = prepared; SetState("ready", App.Text("SelfUpdate.Flow.ReadyHint")); }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (OperationCanceledException) { if (!_closed) Fail("network"); }
            catch (Exception error) { if (!_closed) Fail(error); }
            finally { _taskActive = false; }
        }

        public async Task ActAsync()
        {
            if (_taskActive || IsApplying) return;
            if (_state == "ready" || (_state == "failed" && _plan != null && !_retryDownload)) { await InstallAsync(); return; }
            if (_state == "available" || (_release != null && (_retryDownload || _state == "cancelled"))) { await DownloadAsync(); return; }
            if (_state == "failed") await CheckAsync();
        }

        private void SetRelease(Models.Version release)
        {
            _release = release;
            _retryDownload = false;
            _newAvailable = Native.TreeGitUpdater.TryVersion(release.TagName, out var version) && new System.Version(version) > new System.Version(_currentVersion);
            SetState(_newAvailable ? "available" : "current", _newAvailable ? App.Text("SelfUpdate.Flow.AvailableHint") : string.Empty);
        }

        private async Task DownloadAsync()
        {
            _taskActive = true;
            var cancellation = ResetCancellation();
            _retryDownload = true;
            SetState("download", App.Text("SelfUpdate.Flow.DownloadHint"));
            var progress = new Progress<Models.UpdateProgress>(update =>
            {
                if (_closed || !_taskActive || _state is not ("download" or "verify")) return;
                _progress = update.Total > 0 ? 100.0 * update.Completed / update.Total : 0;
                _progressText = update.Total > 0 ? $"{update.Completed / 1048576.0:F1} MB / {update.Total / 1048576.0:F1} MB" : string.Empty;
                SetState(update.Phase, App.Text(update.Phase == "verify" ? "SelfUpdate.Flow.VerifyHint" : "SelfUpdate.Flow.DownloadHint"));
            });
            try
            {
                using var updater = _factory();
                _plan = await Task.Run(() => updater.PrepareAsync(_release, progress, cancellation.Token));
                if (!_closed) SetState(cancellation.IsCancellationRequested ? "cancelled" : "ready", App.Text(cancellation.IsCancellationRequested ? "SelfUpdate.Flow.CancelledHint" : "SelfUpdate.Flow.ReadyHint"));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!_closed) SetState("cancelled", App.Text("SelfUpdate.Flow.CancelledHint"));
            }
            catch (Exception error) { if (!_closed) Fail(error); }
            finally { _taskActive = false; }
        }

        private async Task InstallAsync()
        {
            if (_plan == null) return;
            if (HasGitOperations() || !Automation.GuiBridge.TryBeginUpdate())
            {
                SetState("ready", App.Text("SelfUpdate.Flow.BusyHint"));
                return;
            }
            Native.TreeGitUpdateInstaller.IsApplying = true;
            SetState("applying", App.Text("SelfUpdate.Flow.ApplyingHint"));
            await Task.Delay(100);
            if (HasGitOperations())
            {
                Native.TreeGitUpdateInstaller.IsApplying = false;
                Automation.GuiBridge.EndUpdate();
                SetState("ready", App.Text("SelfUpdate.Flow.BusyHint"));
                return;
            }
            try
            {
                Native.TreeGitUpdateInstaller.Launch(_plan);
                Restarting = true;
                RestartRequested?.Invoke();
            }
            catch (Exception error)
            {
                Native.TreeGitUpdateInstaller.IsApplying = false;
                Automation.GuiBridge.EndUpdate();
                _retryDownload = false;
                if (Native.TreeGitUpdateInstaller.ProblemCode(error) == "changed") _plan = null;
                Fail(error);
            }
        }

        public async Task FinishBootstrapAsync()
        {
            SetState("applying", App.Text("SelfUpdate.Flow.StartingHint"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(90))
            {
                await Task.Delay(200);
                if (!Native.TreeGitUpdateInstaller.HasActiveUpdate(AppContext.BaseDirectory))
                {
                    Native.TreeGitUpdateInstaller.FinishOrphanedBootstrap();
                    Native.TreeGitUpdateInstaller.IsApplying = false;
                    SetState("current");
                    CloseRequested?.Invoke();
                    return;
                }
            }
            Native.TreeGitUpdateInstaller.IsApplying = false;
            Fail("start");
        }

        public void Dismiss()
        {
            if (!CanDismiss) return;
            if (_state is "download" or "verify") _cancellation.Cancel();
            else CloseRequested?.Invoke();
        }

        public void OnClosed()
        {
            _closed = true;
            _cancellation?.Cancel();
        }

        private static bool HasGitOperations() => App.GetLauncher()?.Pages.Any(page => page.HasRunningOperation) == true;

        private CancellationTokenSource ResetCancellation()
        {
            _cancellation?.Dispose();
            return _cancellation = new CancellationTokenSource();
        }

        private void Fail(Exception error)
        {
            var code = Native.TreeGitUpdateInstaller.ProblemCode(error);
            if (code is "invalid" or "damaged" or "changed" or "not-newer") { _retryDownload = false; _plan = null; }
            if (error is Models.UpdateProblem { Code: "space", RequiredBytes: > 0 } space)
                SetState("failed", App.Text("SelfUpdate.Flow.SpaceNeeded", Math.Ceiling(space.RequiredBytes / 1048576.0)));
            else Fail(code);
        }

        private void Fail(string code) => SetState("failed", App.Text("SelfUpdate.Flow.Error." + (new[] { "network", "busy-service", "invalid", "platform", "not-newer", "permission", "space", "interrupted", "damaged", "changed", "locked", "busy", "start", "failed" }.Contains(code) ? code : "failed")));

        private void SetState(string state, string message = "")
        {
            _state = state;
            _message = message;
            OnPropertyChanged(string.Empty);
        }

        private Models.Version _release;
        private readonly string _root;
        private readonly string _currentVersion;
        private readonly Func<Native.TreeGitUpdater> _factory;
        private bool _newAvailable;
        private Models.UpdatePlan _plan;
        private CancellationTokenSource _cancellation = new();
        private string _state = "checking";
        private string _message = string.Empty;
        private double _progress;
        private string _progressText = string.Empty;
        private bool _retryDownload;
        private bool _closed;
        private bool _taskActive;
    }
}
